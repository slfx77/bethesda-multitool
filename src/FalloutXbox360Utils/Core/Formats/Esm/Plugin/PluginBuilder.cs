using System.Buffers.Binary;
using System.Diagnostics;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema;
using FalloutXbox360Utils.Core.Formats.Esm.Merge;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Records;
using FalloutXbox360Utils.Core.Formats.Esm.Reporting;
using FalloutXbox360Utils.Core.Semantic;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin;

/// <summary>
///     Orchestrator that converts an Xbox 360 DMP into a PC plugin ESP using a base
///     FalloutNV.esm as the source of subrecord data the DMP doesn't carry.
///
///     Pipeline:
///       1. Load PC ESM (raw + semantic) and index FormIDs.
///       2. Load DMP semantically (cells with placed objects, simple-type record lists).
///       3. Merge top-level records: GMST/GLOB/WEAP/ARMO/AMMO/etc. become override records
///          inside their respective top-level GRUPs.
///       4. Merge cell-children (v2): for each DMP cell that maps to a PC ESM cell, classify
///          the merge mode (persistent-only vs has-temporary), encode REFR/ACHR/ACRE
///          overrides, and bundle by parent cell.
///       5. Assemble ESP: TES4 header + top-level GRUPs + CELL hierarchy with cell-children
///          overrides.
///       6. Optionally validate by re-parsing.
/// </summary>
[Obsolete("Use PluginConversionPipeline as the conversion orchestration entrypoint.")]
public sealed class PluginBuilder
{
    private readonly RecordEncoderRegistry _encoderRegistry;
    private readonly IConversionProgressSink _sink;

    /// <summary>
    ///     Master ESM FormID set, populated at the start of <c>Build</c>. Used by post-encode
    ///     FormID validation (e.g., dropping SCRI subrecords whose script FormID doesn't
    ///     exist in master). Null until Build sets it; consumers must tolerate that.
    /// </summary>
    private HashSet<uint>? _masterFormIds;

    /// <summary>
    ///     Per-record-type subset of <see cref="_masterFormIds" />. Lets validators answer
    ///     "is FormID X a SCPT in the master?" rather than the weaker "is FormID X anything
    ///     in the master?" — the loose check let SCRI references through that pointed at
    ///     STAT/ACTI/etc. FormIDs and produced "Unable to find script" load-time errors
    ///     (master FormID exists, but it's the wrong record type).
    /// </summary>
    private Dictionary<string, HashSet<uint>>? _masterFormIdsByType;

    /// <summary>
    ///     FormIDs being emitted via the new-record path in the current <c>Build</c> run.
    ///     Populated as records dispatch through <c>TryEncodeNewTopLevelRecord</c>. Used by
    ///     <see cref="ValidateScriRefs" /> so an override-NPC's SCRI pointing at a freshly
    ///     emitted SCPT (which won't be in <see cref="_masterFormIds" />) survives the
    ///     dangling-ref drop. Reset to empty at the start of each Build.
    /// </summary>
    private readonly HashSet<uint> _emittedNewFormIds = [];

    /// <summary>
    ///     Per-record-type subset of <see cref="_emittedNewFormIds" />. Used by SCRI's
    ///     type-aware validator so it accepts new SCPT FormIDs but rejects new STAT/etc.
    /// </summary>
    private readonly Dictionary<string, HashSet<uint>> _emittedNewFormIdsByType = new();

    /// <summary>
    ///     Tracks every new exterior cell emitted by <c>TryBuildSyntheticExteriorContext</c>,
    ///     keyed by (parent-worldspace FormID, gridX, gridY). The FNV runtime rejects two
    ///     cells claiming the same grid coords within one worldspace ("Cell ... already
    ///     exists at coord", "Error adding cell ... will be destroyed"), so we dedup at
    ///     emit time — first cell wins, subsequent ones are skipped with a v22 warning.
    ///     Reset to empty at the start of each <see cref="BuildAsync" />.
    /// </summary>
    private readonly Dictionary<(uint Worldspace, int GridX, int GridY), uint> _emittedExteriorCellCoords = new();

    /// <summary>
    ///     Per-type EditorID → master FormID lookup for the master ESM, built lazily at
    ///     <see cref="BuildAsync" /> entry. Used to skip new-record emission of an NPC (or
    ///     other type) whose EditorID already names a master record — those are duplicate
    ///     captures of the same logical entity, and emitting both makes the engine show
    ///     two NPCs in-game ("Arcade Gannon" + "Arcade Gannon (10024)"). The override path
    ///     for the master record handles the prototype's mutations cleanly.
    /// </summary>
    private Dictionary<string, Dictionary<string, uint>>? _masterEditorIdToFormIdByType;

    /// <summary>
    ///     Phase C: per-type normalized-EditorID-stem → list of master FormIDs sharing
    ///     that stem. Built alongside <see cref="_masterEditorIdToFormIdByType"/>. Used by
    ///     the REFR base-EditorID remap fallback to find rename-suffix candidates (e.g. a
    ///     prototype <c>SCOLParkingLotChunk03</c> mapping to master
    ///     <c>SCOLParkingLotChunk03b</c>). Values are lists so ambiguity (multiple master
    ///     records sharing the same stem) can be detected and refused.
    /// </summary>
    private Dictionary<string, Dictionary<string, List<uint>>>? _masterStemToFormIdsByType;

    /// <summary>
    ///     Phase C: DMP-prototype FormID → record-type signature. Built once at
    ///     <see cref="BuildAsync"/> entry from the typed <see cref="RecordCollection"/>
    ///     lists. The REFR remap predicate uses it to pick the expected base type when
    ///     scanning master candidates (a STAT-typed prototype base remaps to STAT only,
    ///     not to ACTI / SCOL / etc.).
    /// </summary>
    private Dictionary<uint, string>? _dmpBaseFormIdToRecordType;

    /// <summary>
    ///     DMP-source FormID → freshly-allocated plugin-local FormID, populated as new
    ///     top-level records (STAT, NPC_, WEAP, etc.) flow through
    ///     <see cref="TryEncodeNewTopLevelRecord" />. The downstream cell-children pipeline
    ///     uses it to rewrite each new REFR's NAME (base FormID) before encoding — without
    ///     this remap the REFR's NAME stays pointing at the DMP source, which doesn't exist
    ///     in either master or the freshly-allocated 0x01xxxxxx plugin range, and the
    ///     runtime fails to bind the placement to its base. Reset at each Build entry.
    /// </summary>
    private readonly Dictionary<uint, uint> _newRecordSourceToAllocated = new();

    /// <summary>
    ///     New worldspaces (not in master) whose DMP carries child cells. These are
    ///     pre-encoded so the cell-children pipeline can emit them as full WRLD GRUPs
    ///     (anchor record + World Children GRUP containing the captured cells). Keyed by
    ///     the ORIGINAL DMP-source FormID of the worldspace so cell-children grouping by
    ///     <c>dmpCell.WorldspaceFormId</c> finds them directly. Null until pre-encode runs.
    /// </summary>
    private Dictionary<uint, NewWorldspaceEntry>? _newWorldspacesForCellPipeline;


    /// <summary>
    ///     Engine runtime-state FormIDs that should NEVER be re-emitted from a DMP capture.
    ///     The DMP snapshots live gameplay state, so these records carry whatever transient
    ///     value they had at capture time (player position, current in-game date/hour, last
    ///     equipped weapon, etc.). Re-emitting them as overrides clobbers the engine's
    ///     freshly-initialized runtime state during plugin load and was the cause of the
    ///     v20.x EXCEPTION_ACCESS_VIOLATION crash at FalloutNV+0x46025A.
    /// </summary>
    private static readonly HashSet<uint> RuntimeStateFormIds =
    [
        0x00000007, // Player NPC
        0x00000014, // PlayerRef ACHR (player's placed-actor instance)
        0x00000035, // GameYear GLOB
        0x00000036, // GameMonth GLOB
        0x00000037, // GameDay GLOB
        0x00000038, // GameHour GLOB
        0x00000039, // GameDaysPassed GLOB
        0x0000003A, // TimeScale GLOB
        0x000001F4  // Hand-to-Hand WEAP (engine default unarmed fallback)
    ];

    private static readonly HashSet<string> StructuralCellRefSubrecords = new(StringComparer.Ordinal)
    {
        "XPOD", // room/portal connection
        "XOCP", // occlusion plane geometry
        "XORD", // linked occlusion planes
        "XMBO", // room/bound marker extents
        "XPRM", // primitive marker geometry
        "XNDP"  // navigation door portal
    };

    private static readonly string[] StructuralBaseEditorIdNeedles =
    [
        "RoomMarker",
        "PortalMarker",
        "Occlusion",
        "MultiBound",
        "Culling"
    ];

    public PluginBuilder(RecordEncoderRegistry registry, IConversionProgressSink? sink = null)
    {
        _encoderRegistry = registry;
        _sink = sink ?? NullConversionProgressSink.Instance;
    }

    /// <summary>
    ///     Run the conversion pipeline. The output is a plugin ESP file at
    ///     <see cref="DmpToEspInputs.OutputEspPath" /> on success.
    /// </summary>
    public async Task<PluginBuildResult> BuildAsync(DmpToEspInputs inputs, CancellationToken ct = default)
    {
        var stats = new ConversionPipelineStats();
        var sw = Stopwatch.StartNew();

        try
        {
            // Phase 1: load PC ESM (raw bytes for record indexing + semantic for cell parentage).
            _sink.OnPhaseStart("Loading PC ESM", null);
            var pcEsmFileInfo = new FileInfo(inputs.PcEsmPath);
            if (!pcEsmFileInfo.Exists)
            {
                return Fail($"PC ESM not found at: {inputs.PcEsmPath}", stats, sw);
            }

            var pcEsmBytes = await File.ReadAllBytesAsync(inputs.PcEsmPath, ct);
            var (pcRecordsList, pcGrupHeaders) = EsmParser.EnumerateRecordsWithGrups(pcEsmBytes);
            var masterIndex = MasterRecordIndex.Build(pcRecordsList, pcGrupHeaders);
            var pcRecordsByFormId = masterIndex.RecordsByFormId;

            // Populate the validation set used by post-encode FormID checks (e.g. SCRI
            // dangling-ref nullification). Any FormID an emitted subrecord points at that
            // isn't in this set (and isn't sentinel-0 or 0xFFFFFFFF) is unresolvable at
            // runtime and gets dropped to avoid null-deref during master-binding.
            _masterFormIds = masterIndex.FormIds;
            _masterFormIdsByType = masterIndex.FormIdsByType;
            _emittedNewFormIds.Clear();
            _emittedNewFormIdsByType.Clear();
            _emittedExteriorCellCoords.Clear();
            _newRecordSourceToAllocated.Clear();
            _masterEditorIdToFormIdByType = masterIndex.EditorIdToFormIdByType;
            _masterStemToFormIdsByType = masterIndex.StemToFormIdsByType;

            // Build the parent-cell index by walking records in offset order. Children always
            // appear after their parent CELL in the file, so the "most recent CELL" tracker
            // gives correct parentage without needing GRUP context.
            var refToCell = masterIndex.RefToCell;

            // NAVM (NavMesh) records live in each cell's Temporary Children GRUP. When a
            // plugin overrides a cell, FNV's full-GRUP-replacement semantics drop the
            // master's NAVMs unless we re-emit them — and a cell with no navmesh leaves
            // idle NPCs unanchored (they sink under physics when standing still while
            // pathfinding still works mid-walk). Build a per-cell NAVM index so override
            // bundles can copy these records verbatim.
            var navmsByCell = masterIndex.NavmsByCell;
            var landsByCell = masterIndex.LandsByCell;

            // Build the cell-context index — maps each CELL FormID to its master GRUP context
            // (block/subblock labels, parent worldspace if exterior). Plugin overrides reuse
            // these labels verbatim so we reproduce the master's exact layout.
            var cellContexts = masterIndex.CellContexts;

            _sink.Info("Loading PC ESM",
                $"Loaded {pcRecordsByFormId.Count:N0} PC records, {refToCell.Count:N0} child→cell links, {cellContexts.Count:N0} cell contexts.");
            _sink.OnPhaseEnd("Loading PC ESM", stats);
            ct.ThrowIfCancellationRequested();

            // Phase 2: load DMP and parse semantic records.
            _sink.OnPhaseStart("Reading DMP", null);
            using var unified = await SemanticFileLoader.LoadAsync(inputs.DmpPath, cancellationToken: ct);
            var dmpRecords = unified.Records;
            _dmpBaseFormIdToRecordType = ReferenceBaseRemapper.BuildDmpBaseFormIdToRecordType(dmpRecords);
            _sink.Info("Reading DMP", "DMP semantic load complete.");
            _sink.OnPhaseEnd("Reading DMP", stats);
            ct.ThrowIfCancellationRequested();

            // v22 asset-rename pass: rewrite record paths in-place when fuzzy resolution
            // matches a differently-named asset in an indexed Data folder. Runs BEFORE
            // encoding so the output ESP carries the unified paths. No-op when the user
            // didn't configure rename folders.
            TryApplyAssetRenames(dmpRecords, inputs.Options, ct);

            var classifier = new NewVsOverrideClassifier(pcRecordsByFormId.Keys);

            // Single allocator shared across phases — Phase 3 (new top-level records) and
            // Phase 4 (new cells/refs). NextObjectId in TES4 reflects the high-water mark.
            var allocator = new FormIdAllocator(inputs.Options.NewRecordBaseFormId);

            // Pre-Phase-3 step: identify new WRLDs (not in master) that have at least one
            // captured child cell in the DMP, allocate their FormIDs upfront, and encode
            // their record bytes. The per-type emit loop skips these — they'll be emitted
            // by the cell-children pipeline instead, so each new WRLD's record sits
            // immediately above its World Children GRUP (the canonical ESM layout).
            _newWorldspacesForCellPipeline = PreEncodeNewWorldspacesWithCells(
                dmpRecords, classifier, allocator, inputs.Options);
            if (_newWorldspacesForCellPipeline.Count > 0)
            {
                _sink.Info("Merging top-level records",
                    $"Deferred {_newWorldspacesForCellPipeline.Count:N0} new WRLD(s) with child cells " +
                    "to the cell-children pipeline (so their child cells emit under the right WRLD).");
            }

            // Phase 3: top-level record merging (GMST, GLOB, WEAP, …).
            _sink.OnPhaseStart("Merging top-level records", null);
            var grupBytesByType = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var (recordType, models) in EnumerateModelsByType(dmpRecords))
            {
                ct.ThrowIfCancellationRequested();
                if (_encoderRegistry.Get(recordType) is not { } encoder)
                {
                    var skipped = 0;
                    foreach (var _ in models)
                    {
                        skipped++;
                        stats.IncrementSkipped(recordType);
                    }

                    if (skipped > 0)
                    {
                        _sink.Warn("Merging top-level records",
                            $"No encoder for {recordType} — {skipped} record(s) skipped.",
                            recordType, code: $"skipped:{recordType}");
                    }

                    continue;
                }

                var grupBytes = BuildGrupForType(
                    recordType, encoder, models, pcRecordsByFormId, classifier, allocator, inputs.Options, stats);
                if (grupBytes.Length > 0)
                {
                    grupBytesByType[recordType] = grupBytes;
                }
            }

            // DIAL+INFO are not in EnumerateModelsByType — emit them as a single nested
            // section so each DIAL is followed by its type-7 Topic Children GRUP of INFOs.
            // Master FormIDs feed the FormID validator inside the builder: any FormID
            // reference (QSTI/ANAM/NAME/TCLT/TCLF/SCRO/CTDA-FormID-params) that isn't a
            // known master record AND isn't one of our newly-allocated FormIDs is replaced
            // with 0 to keep the runtime from null-deref'ing on dangling cross-refs.
            var dialogResult = DialogGrupBuilder.BuildDialogSection(
                dmpRecords.DialogTopics, dmpRecords.Dialogues, classifier, allocator,
                pcRecordsByFormId.Keys, stats, _sink);
            if (dialogResult.DialogSection.Length > 0)
            {
                grupBytesByType["DIAL"] = dialogResult.DialogSection;
            }

            // The placeholder DIAL needs a parent quest or the FNV runtime refuses to attach
            // INFOs to it. DialogGrupBuilder allocated + encoded the placeholder QUST; inject
            // it into the QUST GRUP body so it loads as a normal top-level record.
            if (dialogResult.PlaceholderQustRecord is { Length: > 0 } placeholderQust)
            {
                grupBytesByType["QUST"] = TopLevelRecordEmitter.AppendOrCreateQustGrup(
                    grupBytesByType.GetValueOrDefault("QUST"), placeholderQust);
            }

            _sink.OnPhaseEnd("Merging top-level records", stats);
            ct.ThrowIfCancellationRequested();

            // Phase 4: cell-children merging. Builds CellOverrideBundles for each affected cell.
            // v3 also allocates plugin-index FormIDs for new cells/refs and synthesizes
            // deletion-flag overrides for HasTemporary cells.
            _sink.OnPhaseStart("Merging cell children", null);
            var pcRefFormIds = new HashSet<uint>(refToCell.Keys);
            var bundles = BuildCellOverrideBundles(
                dmpRecords, pcRecordsByFormId, refToCell, pcRefFormIds, cellContexts,
                navmsByCell, landsByCell, allocator, inputs.Options, stats, ct);
            _sink.Info("Merging cell children",
                $"Built {bundles.Count:N0} cell-override bundle(s); allocated {allocator.NextLocalId - allocator.BaseLocalId:N0} new FormID(s).");
            _sink.OnPhaseEnd("Merging cell children", stats);
            ct.ThrowIfCancellationRequested();

            // Phase 5: assemble TES4 + top-level GRUPs + cell-children GRUP and write output.
            _sink.OnPhaseStart("Writing ESP", null);
            var outputBytes = new EspAssembler(_encoderRegistry).Assemble(
                inputs.Options,
                pcEsmFileInfo.Length,
                stats,
                grupBytesByType,
                bundles,
                pcRecordsByFormId,
                allocator,
                _newWorldspacesForCellPipeline);
            await File.WriteAllBytesAsync(inputs.OutputEspPath, outputBytes, ct);
            stats.OutputBytes = outputBytes.LongLength;
            _sink.OnPhaseEnd("Writing ESP", stats);

            // Phase 6 (optional): validate by re-parsing.
            string? validationReport = null;
            if (inputs.Options.ValidateOutput)
            {
                _sink.OnPhaseStart("Validating output", null);
                validationReport = Validation.PluginRoundTripValidator.Validate(outputBytes, stats.RecordsEmitted);
                _sink.Info("Validating output", validationReport);
                _sink.OnPhaseEnd("Validating output", stats);
            }

            sw.Stop();
            stats.Elapsed = sw.Elapsed;
            _sink.OnComplete(stats);

            return new PluginBuildResult
            {
                Success = true,
                Stats = stats,
                OutputPath = inputs.OutputEspPath,
                ValidationReport = validationReport
            };
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            stats.Elapsed = sw.Elapsed;
            _sink.OnComplete(stats);
            return new PluginBuildResult
            {
                Success = false,
                Stats = stats,
                ErrorMessage = "Cancelled."
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            stats.Elapsed = sw.Elapsed;
            stats.Errors++;
            _sink.Error("PluginBuilder", $"Conversion failed: {ex.Message}");
            _sink.OnComplete(stats);
            return new PluginBuildResult
            {
                Success = false,
                Stats = stats,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    ///     Pre-encode every new (non-master) worldspace that has at least one captured child
    ///     cell. The cell-children pipeline emits these alongside their World Children GRUP
    ///     so the WRLD record sits directly above its cells (canonical ESM layout). New WRLDs
    ///     with no child cells stay in the standard top-level emit path.
    /// </summary>
    private Dictionary<uint, NewWorldspaceEntry> PreEncodeNewWorldspacesWithCells(
        Models.RecordCollection dmpRecords,
        NewVsOverrideClassifier classifier,
        FormIdAllocator allocator,
        PluginBuildOptions options)
    {
        var result = new Dictionary<uint, NewWorldspaceEntry>();

        // Build the set of worldspace FormIDs that have at least one DMP cell pointing at
        // them. Cheap pass — we only need membership, not counts.
        var worldspacesWithCells = new HashSet<uint>();
        foreach (var cell in dmpRecords.Cells)
        {
            if (cell.WorldspaceFormId is uint wsId)
            {
                worldspacesWithCells.Add(wsId);
            }
        }

        if (worldspacesWithCells.Count == 0)
        {
            return result;
        }

        foreach (var wrld in dmpRecords.Worldspaces)
        {
            // Only new WRLDs (not in master) need this special handling. Override WRLDs go
            // through the standard cell-children pipeline already.
            if (classifier.IsOverride(wrld.FormId))
            {
                continue;
            }

            if (!worldspacesWithCells.Contains(wrld.FormId))
            {
                continue;
            }

            var encoded = Writers.Encoders.WrldEncoder.EncodeNew(wrld);
            if (encoded.Subrecords.Count == 0)
            {
                continue; // Encoder declined this WRLD (rare; e.g., insufficient metadata).
            }

            var emittedFormId = allocator.Allocate();
            var flags = options.CompressRecords ? 0x00040000u : 0u;
            var recordBytes = PluginRecordByteBuilder.BuildNewRecordBytes("WRLD", emittedFormId, flags, encoded.Subrecords);

            result[wrld.FormId] = new NewWorldspaceEntry(emittedFormId, recordBytes);

            if (options.VerboseDecisions)
            {
                _sink.Decision("Merging top-level records",
                    $"Pre-encoded new WRLD 0x{wrld.FormId:X8} → emitted 0x{emittedFormId:X8} " +
                    "(deferred to cell-children pipeline so child cells nest under it).",
                    "WRLD", wrld.FormId, code: "wrld.deferred-with-cells");
            }
        }

        return result;
    }

    private byte[] BuildGrupForType(
        string recordType,
        IRecordEncoder encoder,
        IEnumerable<object> models,
        Dictionary<uint, ParsedMainRecord> pcRecords,
        NewVsOverrideClassifier classifier,
        FormIdAllocator allocator,
        PluginBuildOptions options,
        ConversionPipelineStats stats)
    {
        var policy = SubrecordMergePolicy.ForRecordType(recordType);

        using var grupBodyStream = new MemoryStream();
        var anyEmitted = false;

        foreach (var model in models)
        {
            stats.RecordsConsidered++;

            // Phase B (SCOL census): tally every DMP SCOL we look at, regardless of branch.
            if (recordType == "SCOL")
            {
                stats.Scols.TotalParsed++;
            }

            var formId = ExtractFormId(model);

            if (RuntimeStateFormIds.Contains(formId))
            {
                stats.IncrementSkipped(recordType);
                _sink.Decision("Merging top-level records",
                    $"Skipping runtime-state record 0x{formId:X8} — DMP captures live gameplay " +
                    "state for engine-reserved records; re-emitting would clobber the engine's " +
                    "runtime setup (player / clock / default weapon).",
                    recordType, formId, code: "runtime-state.skip");
                continue;
            }

            // Deferred WRLD: pre-encoded into _newWorldspacesForCellPipeline so it can emit
            // alongside its captured child cells in the cell-children pipeline. Skipping here
            // prevents double-emission.
            if (recordType == "WRLD"
                && _newWorldspacesForCellPipeline is not null
                && _newWorldspacesForCellPipeline.ContainsKey(formId))
            {
                stats.IncrementEmitted(recordType);
                stats.NewRecordsEmitted++;
                continue;
            }

            // (v20.13a-j bisection diagnostic removed. The bug was traced to AVIF: the DMP
            // parser identifies engine-hardcoded actor values as "new" records, but emitting
            // them with only an EDID subrecord (no FULL/DESC/ANAM, since the parser doesn't
            // capture metadata) crashed FNV at startup. Fix lives in AvifEncoder.EncodeNew
            // which now skips new AVIF emission entirely.)

            if (!classifier.IsOverride(formId))
            {
                // v22: when the DMP captured a "new" NPC/CREA whose EditorID matches a
                // master record, it's almost always a duplicate observation of the same
                // logical actor in runtime memory. Emitting it produces in-game ghosts
                // ("Arcade Gannon" + "Arcade Gannon (10024)" side-by-side) because the
                // engine disambiguates same-named actors by FormID suffix. The master
                // override path is the right channel for those mutations.
                if (TryFindMasterByEditorId(recordType, model, out var masterFormId))
                {
                    if (recordType == "LTEX")
                    {
                        _newRecordSourceToAllocated[formId] = masterFormId;
                    }

                    stats.IncrementSkipped(recordType);
                    _sink.Decision("Merging top-level records",
                        $"Skipping new {recordType} 0x{formId:X8} — its EditorID already names " +
                        $"master record 0x{masterFormId:X8}. Override path handles the master's mutations.",
                        recordType, formId, code: "new-record.dup-editor-id");
                    continue;
                }

                // v4: route through new-record path if this type has a new-record encoder.
                if (TryEncodeNewTopLevelRecord(recordType, model, allocator, options, stats, out var newBytes))
                {
                    grupBodyStream.Write(newBytes);
                    anyEmitted = true;
                    stats.IncrementEmitted(recordType);
                    stats.NewRecordsEmitted++;
                    if (recordType == "SCOL")
                    {
                        stats.Scols.NewEmitted++;
                    }
                    // v22: track FormIDs emitted via the new-record path so ValidateScriRefs
                    // accepts SCRI references to freshly-emitted SCPTs (or other new records).
                    // Without this, an override-NPC pointing at a new prototype script would
                    // have its SCRI dropped because the target isn't in _masterFormIds.
                    _emittedNewFormIds.Add(formId);
                    if (!_emittedNewFormIdsByType.TryGetValue(recordType, out var typeSet))
                    {
                        typeSet = [];
                        _emittedNewFormIdsByType[recordType] = typeSet;
                    }
                    typeSet.Add(formId);
                }
                else
                {
                    stats.IncrementSkipped(recordType);
                }

                continue;
            }

            // Phase B (SCOL census): observe master-vs-DMP delta for SCOLs without emitting
            // anything. Counts toward the Phase D gate (only implement SCOL override emission
            // once non-zero deltas are observed across the active DMP corpus).
            if (recordType == "SCOL"
                && model is Models.Records.World.StaticCollectionRecord dmpScol)
            {
                stats.Scols.InMaster++;
                if (pcRecords.TryGetValue(formId, out var masterScolRecord)
                    && TryDetectScolOverrideDelta(dmpScol, masterScolRecord, out var deltaReason))
                {
                    stats.Scols.OverrideDeltaObserved++;
                    stats.IncrementDropReason("scol.override-delta-observed");
                    _sink.Decision("Merging top-level records",
                        $"DMP SCOL 0x{formId:X8} differs from master ({deltaReason}). " +
                        "Override-emission for SCOL is currently a no-op; this run captured the delta " +
                        "as evidence that the override path may be worth implementing.",
                        recordType, formId, code: "scol.override-delta-observed");
                }
            }

            EncodedRecord encoded;
            try
            {
                encoded = encoder.Encode(model);
            }
            catch (Exception ex)
            {
                stats.RecordsFailed++;
                stats.Errors++;
                _sink.Error("Merging top-level records",
                    $"Encoder threw {ex.GetType().Name}: {ex.Message}", recordType, formId);
                continue;
            }

            if (encoded.Subrecords.Count == 0)
            {
                stats.IncrementSkipped(recordType);
                if (options.VerboseDecisions)
                {
                    _sink.Decision("Merging top-level records",
                        "Encoder produced no subrecords — record retains ESM verbatim, skipped.",
                        recordType, formId);
                }

                continue;
            }

            if (!pcRecords.TryGetValue(formId, out var esmRecord))
            {
                stats.IncrementSkipped(recordType);
                _sink.Warn("Merging top-level records",
                    "Classifier marked record as override but it was not in the PC ESM index.",
                    recordType, formId);
                continue;
            }

            var merge = RecordMergeEngine.Merge(esmRecord, encoded, policy);
            foreach (var w in merge.Warnings)
            {
                stats.Warnings++;
                _sink.Warn("Merging top-level records", w, recordType, formId);
            }

            var recordBytes = BuildRecordBytes(esmRecord, merge.SubrecordBytes, options);
            grupBodyStream.Write(recordBytes);

            stats.IncrementEmitted(recordType);
            stats.OverridesEmitted++;
            anyEmitted = true;

            if (options.VerboseDecisions)
            {
                _sink.Info("Merging top-level records",
                    $"Override emitted ({merge.DmpSignaturesUsed.Count} DMP, {merge.EsmSignaturesRetained.Count} ESM).",
                    recordType, formId);
            }
        }

        if (!anyEmitted)
        {
            return [];
        }

        return TopLevelRecordEmitter.WrapInTopLevelGrup(recordType, grupBodyStream.ToArray());
    }

    /// <summary>
    ///     Dispatch a DMP model that lacks a master FormID through the appropriate type-specific
    ///     <c>EncodeNew</c> method. v4 covers GMST, GLOB, MISC, KEYM, ALCH, BOOK, AMMO. Other
    ///     types fall through to a "skipped" decision (their `EncodeNew` paths are deferred).
    /// </summary>
    private bool TryEncodeNewTopLevelRecord(
        string recordType,
        object model,
        FormIdAllocator allocator,
        PluginBuildOptions options,
        ConversionPipelineStats stats,
        out byte[] recordBytes)
    {
        recordBytes = [];

        EncodedRecord? encoded;
        try
        {
            encoded = recordType switch
            {
                "GMST" => Writers.Encoders.GmstEncoder.EncodeNew(
                    (Models.Records.Misc.GameSettingRecord)model),
                "GLOB" => Writers.Encoders.GlobEncoder.EncodeNew(
                    (Models.Records.Misc.GlobalRecord)model),
                "MISC" => Writers.Encoders.MiscEncoder.EncodeNew(
                    (Models.Records.Item.MiscItemRecord)model),
                "KEYM" => Writers.Encoders.KeymEncoder.EncodeNew(
                    (Models.Records.Item.KeyRecord)model),
                "ALCH" => Writers.Encoders.AlchEncoder.EncodeNew(
                    (Models.Records.Item.ConsumableRecord)model),
                "BOOK" => Writers.Encoders.BookEncoder.EncodeNew(
                    (Models.Records.Item.BookRecord)model),
                "AMMO" => Writers.Encoders.AmmoEncoder.EncodeNew(
                    (Models.Records.Item.AmmoRecord)model),
                "WEAP" => Writers.Encoders.WeapEncoder.EncodeNew(
                    (Models.Records.Item.WeaponRecord)model),
                "ARMO" => Writers.Encoders.ArmoEncoder.EncodeNew(
                    (Models.Records.Item.ArmorRecord)model),
                "FACT" => Writers.Encoders.FactEncoder.EncodeNew(
                    (Models.Records.Character.FactionRecord)model),
                "NPC_" => Writers.Encoders.NpcEncoder.EncodeNew(
                    (Models.Records.Character.NpcRecord)model),
                "SCPT" => Writers.Encoders.ScptEncoder.EncodeNew(
                    (Models.Records.Quest.ScriptRecord)model),
                "DIAL" => Writers.Encoders.DialEncoder.EncodeNew(
                    (Models.Records.Quest.DialogTopicRecord)model),
                "INFO" => Writers.Encoders.InfoEncoder.EncodeNew(
                    (Models.Records.Quest.DialogueRecord)model),
                "QUST" => Writers.Encoders.QustEncoder.EncodeNew(
                    (Models.Records.Quest.QuestRecord)model),
                "PACK" => Writers.Encoders.PackEncoder.EncodeNew(
                    (Models.Records.AI.PackageRecord)model),
                "ACTI" => Writers.Encoders.ActiEncoder.EncodeNew(
                    (Models.Records.World.ActivatorRecord)model),
                "DOOR" => Writers.Encoders.DoorEncoder.EncodeNew(
                    (Models.Records.World.DoorRecord)model),
                "LIGH" => Writers.Encoders.LighEncoder.EncodeNew(
                    (Models.Records.World.LightRecord)model),
                "STAT" => Writers.Encoders.StatEncoder.EncodeNew(
                    (Models.Records.World.StaticRecord)model),
                "SCOL" => Writers.Encoders.ScolEncoder.EncodeNew(
                    (Models.Records.World.StaticCollectionRecord)model,
                    _masterFormIds ?? new HashSet<uint>(),
                    _emittedNewFormIdsByType.TryGetValue("STAT", out var statSet)
                        ? statSet
                        : new HashSet<uint>()),
                "CONT" => Writers.Encoders.ContEncoder.EncodeNew(
                    (Models.Records.Item.ContainerRecord)model),
                "FURN" => Writers.Encoders.FurnEncoder.EncodeNew(
                    (Models.Records.World.FurnitureRecord)model),
                "TERM" => Writers.Encoders.TermEncoder.EncodeNew(
                    (Models.Records.World.TerminalRecord)model),
                "PROJ" => Writers.Encoders.ProjEncoder.EncodeNew(
                    (Models.Records.Magic.ProjectileRecord)model),
                "EXPL" => Writers.Encoders.ExplEncoder.EncodeNew(
                    (Models.Records.Magic.ExplosionRecord)model),
                "IMOD" => Writers.Encoders.ImodEncoder.EncodeNew(
                    (Models.Records.Item.WeaponModRecord)model),
                "ARMA" => Writers.Encoders.ArmaEncoder.EncodeNew(
                    (Models.Records.Item.ArmaRecord)model),
                "RCPE" => Writers.Encoders.RcpeEncoder.EncodeNew(
                    (Models.Records.Item.RecipeRecord)model),
                "RCCT" => Writers.Encoders.RcctEncoder.EncodeNew(
                    (Models.Records.Misc.RecipeCategoryRecord)model),
                "COBJ" => Writers.Encoders.CobjEncoder.EncodeNew(
                    (Models.Records.Item.ConstructibleObjectRecord)model),
                "EYES" => Writers.Encoders.EyesEncoder.EncodeNew(
                    (Models.Records.Character.EyesRecord)model),
                "HAIR" => Writers.Encoders.HairEncoder.EncodeNew(
                    (Models.Records.Character.HairRecord)model),
                "REPU" => Writers.Encoders.RepuEncoder.EncodeNew(
                    (Models.Records.Character.ReputationRecord)model),
                "AVIF" => Writers.Encoders.AvifEncoder.EncodeNew(
                    (Models.Records.Character.ActorValueInfoRecord)model),
                "MUSC" => Writers.Encoders.MuscEncoder.EncodeNew(
                    (Models.Records.Misc.MusicTypeRecord)model),
                "MESG" => Writers.Encoders.MesgEncoder.EncodeNew(
                    (Models.Records.Quest.MessageRecord)model),
                "NOTE" => Writers.Encoders.NoteEncoder.EncodeNew(
                    (Models.Records.Item.NoteRecord)model),
                "FLST" => Writers.Encoders.FlstEncoder.EncodeNew(
                    (Models.Records.Misc.FormListRecord)model),
                "LVLI" or "LVLN" or "LVLC" => Writers.Encoders.LvliEncoder.EncodeNew(
                    (Models.Records.Item.LeveledListRecord)model),
                "CREA" => Writers.Encoders.CreaEncoder.EncodeNew(
                    (Models.Records.Character.CreatureRecord)model),
                "CLAS" => Writers.Encoders.ClasEncoder.EncodeNew(
                    (Models.Records.Character.ClassRecord)model),
                "SOUN" => Writers.Encoders.SounEncoder.EncodeNew(
                    (Models.Records.Misc.SoundRecord)model),
                "TXST" => Writers.Encoders.TxstEncoder.EncodeNew(
                    (Models.Records.Misc.TextureSetRecord)model),
                "LTEX" => Writers.Encoders.LtexEncoder.EncodeNew(
                    (Models.Records.Misc.LandscapeTextureRecord)model),
                "CHAL" => Writers.Encoders.ChalEncoder.EncodeNew(
                    (Models.Records.Misc.ChallengeRecord)model),
                "BPTD" => Writers.Encoders.BptdEncoder.EncodeNew(
                    (Models.Records.Character.BodyPartDataRecord)model),
                "ENCH" => Writers.Encoders.EnchEncoder.EncodeNew(
                    (Models.Records.Magic.EnchantmentRecord)model),
                "SPEL" => Writers.Encoders.SpelEncoder.EncodeNew(
                    (Models.Records.Magic.SpellRecord)model),
                "PERK" => Writers.Encoders.PerkEncoder.EncodeNew(
                    (Models.Records.Magic.PerkRecord)model),
                "MGEF" => Writers.Encoders.MgefEncoder.EncodeNew(
                    (Models.Records.Magic.BaseEffectRecord)model),
                "WRLD" => Writers.Encoders.WrldEncoder.EncodeNew(
                    (Models.Records.World.WorldspaceRecord)model),
                "RACE" => Writers.Encoders.RaceEncoder.EncodeNew(
                    (Models.Records.Character.RaceRecord)model),
                _ => null
            };
        }
        catch (Exception ex)
        {
            stats.RecordsFailed++;
            stats.Errors++;
            var sourceFormId = ExtractFormId(model);
            _sink.Error("Merging top-level records",
                $"New-record encoder threw {ex.GetType().Name}: {ex.Message}",
                recordType, sourceFormId);
            return false;
        }

        if (encoded is null)
        {
            // Type doesn't have a v4 new-record path (WEAP/ARMO/FACT/NPC_ deferred to v5).
            if (options.VerboseDecisions)
            {
                _sink.Decision("Merging top-level records",
                    "New-record emission deferred for this type — skipped.",
                    recordType, ExtractFormId(model), code: $"skipped:new-{recordType}");
            }

            return false;
        }

        foreach (var w in encoded.Warnings)
        {
            stats.Warnings++;
            _sink.Warn("Merging top-level records", w, recordType, ExtractFormId(model));
        }

        // Phase B (SCOL census): tally parts dropped per record + whole-record drops caused
        // by all-parts-unreachable. The encoder embeds the canonical phrases below in each
        // dropped-part warning + the final all-dropped warning.
        if (recordType == "SCOL")
        {
            foreach (var w in encoded.Warnings)
            {
                if (w.Contains("part ONAM", StringComparison.Ordinal)
                    && w.Contains("unreachable", StringComparison.Ordinal))
                {
                    stats.Scols.PartsDroppedTotal++;
                }
            }

            if (encoded.Subrecords.Count == 0)
            {
                stats.Scols.DroppedAllPartsUnreachable++;
            }
        }

        if (recordType == "LTEX")
        {
            encoded = encoded with
            {
                Subrecords = ValidateLtexRefs(encoded.Subrecords, ExtractFormId(model))
            };
        }

        // Drop SCRI subrecords whose script FormID isn't in the master. The DMP often carries
        // SCRI references to FO3-vintage scripts that don't exist in the FNV master and would
        // cause the engine to log "Unable to find script (XXXXXXXX) on owner object" warnings
        // plus null-deref later during script-binding setup.
        var validatedSubrecords = ValidateScriRefs(encoded.Subrecords, recordType, ExtractFormId(model));

        if (validatedSubrecords.Count == 0)
        {
            return false;
        }

        var allocatedFormId = allocator.Allocate();
        var flags = options.CompressRecords ? 0x00040000u : 0u;
        recordBytes = PluginRecordByteBuilder.BuildNewRecordBytes(recordType, allocatedFormId, flags, validatedSubrecords);

        var dmpSourceFormId = ExtractFormId(model);
        if (dmpSourceFormId != 0 && dmpSourceFormId != allocatedFormId)
        {
            _newRecordSourceToAllocated[dmpSourceFormId] = allocatedFormId;
        }

        // Track BOTH the DMP source and the freshly-allocated FormID as "emitted new"
        // so downstream validators (ValidateScriRefs, the new-REFR base check) accept
        // references regardless of whether the upstream model field still carries the
        // DMP source or has been remapped to the allocation.
        _emittedNewFormIds.Add(allocatedFormId);
        if (!_emittedNewFormIdsByType.TryGetValue(recordType, out var allocTypeSet))
        {
            allocTypeSet = [];
            _emittedNewFormIdsByType[recordType] = allocTypeSet;
        }
        allocTypeSet.Add(allocatedFormId);

        if (options.VerboseDecisions)
        {
            _sink.Decision("Merging top-level records",
                $"New {recordType} allocated FormID 0x{allocatedFormId:X8} (DMP source 0x{dmpSourceFormId:X8}).",
                recordType, dmpSourceFormId);
        }

        return true;
    }

    /// <summary>
    ///     Build cell-children bundles. v3 handles three cases:
    ///       1. Cell exists in PC ESM, ref exists in PC ESM → override (v2 path)
    ///       2. Cell exists in PC ESM, ref doesn't → new ref allocated under existing cell
    ///       3. Cell doesn't exist in PC ESM → new CELL with new refs as children
    ///     Plus, for HasTemporary cells with a master, master refs missing from DMP get
    ///     deleted-flag overrides via <see cref="DeletedRefSynthesizer" />.
    /// </summary>
    /// <summary>
    ///     Group <see cref="Models.Records.World.CellRecord" /> captures by their logical
    ///     identity (master FormID for overrides, (worldspace, gridX, gridY) for new
    ///     exterior cells, EditorID for new interior cells) and union their
    ///     <c>PlacedObjects</c> lists deduped by REFR FormID. First-capture-wins for every
    ///     other field. Returns one merged <see cref="Models.Records.World.CellRecord" />
    ///     per logical cell. Single-capture cells pass through unchanged.
    /// </summary>
    private List<Models.Records.World.CellRecord> UnionCellPlacementsAcrossCaptures(
        IReadOnlyList<Models.Records.World.CellRecord> cells,
        PluginBuildOptions options)
    {
        var groups = new Dictionary<string, List<Models.Records.World.CellRecord>>(StringComparer.Ordinal);
        var orderedKeys = new List<string>();
        foreach (var cell in cells)
        {
            var key = ComputeCellIdentityKey(cell);
            if (!groups.TryGetValue(key, out var list))
            {
                list = [];
                groups[key] = list;
                orderedKeys.Add(key);
            }
            list.Add(cell);
        }

        var merged = new List<Models.Records.World.CellRecord>(orderedKeys.Count);
        var totalUnioned = 0;
        var totalGroupsWithMerges = 0;
        foreach (var key in orderedKeys)
        {
            var captures = groups[key];
            if (captures.Count == 1)
            {
                merged.Add(captures[0]);
                continue;
            }

            var primary = captures[0];
            var seen = new HashSet<uint>(capacity: primary.PlacedObjects.Count);
            var unionList = new List<Models.World.PlacedReference>(primary.PlacedObjects.Count);
            foreach (var capture in captures)
            {
                foreach (var placed in capture.PlacedObjects)
                {
                    if (seen.Add(placed.FormId))
                    {
                        unionList.Add(placed);
                    }
                }
            }

            var added = unionList.Count - primary.PlacedObjects.Count;
            if (added > 0)
            {
                totalUnioned += added;
                totalGroupsWithMerges++;
            }

            merged.Add(primary with { PlacedObjects = unionList });

            if (options.VerboseDecisions && added > 0)
            {
                _sink.Decision("Merging cell children",
                    $"Cell {key} unioned {captures.Count} captures: " +
                    $"primary had {primary.PlacedObjects.Count}, union has {unionList.Count} (+{added} from secondary captures).",
                    "CELL", primary.FormId, code: "cell.capture-union");
            }
        }

        if (totalUnioned > 0)
        {
            _sink.Info("Merging cell children",
                $"Multi-capture cell union: gained {totalUnioned:N0} placement(s) across " +
                $"{totalGroupsWithMerges:N0} cell(s) (was first-capture-only).");
        }

        return merged;
    }

    /// <summary>
    ///     Stable identity key per logical cell. Cells with a real master-style FormID
    ///     are keyed by FormID. Virtual cells (parser-synthesized; FormID 0xFE-prefixed)
    ///     fall through to grid coords + worldspace. Interior cells without coords use
    ///     EditorID.
    /// </summary>
    private static string ComputeCellIdentityKey(Models.Records.World.CellRecord cell)
    {
        if (cell.FormId != 0 && (cell.FormId & 0xFF000000u) != 0xFE000000u)
        {
            return $"FID:{cell.FormId:X8}";
        }

        if (cell.IsInterior)
        {
            return $"INT:{cell.EditorId ?? "(none)"}";
        }

        return $"EXT:{cell.WorldspaceFormId ?? 0:X8}:{cell.GridX ?? 0}:{cell.GridY ?? 0}";
    }

    private List<CellOverrideBundle> BuildCellOverrideBundles(
        RecordCollection dmpRecords,
        Dictionary<uint, ParsedMainRecord> pcRecordsByFormId,
        Dictionary<uint, uint> refToCell,
        IReadOnlySet<uint> pcRefFormIds,
        Dictionary<uint, PcEsmCellContext> cellContexts,
        Dictionary<uint, List<uint>> navmsByCell,
        Dictionary<uint, List<uint>> landsByCell,
        FormIdAllocator allocator,
        PluginBuildOptions options,
        ConversionPipelineStats stats,
        CancellationToken ct)
    {
        var bundles = new List<CellOverrideBundle>();
        var policy = SubrecordMergePolicy.Default;

        // Inverse of refToCell — gives us the master refs in each cell so we can compute
        // the set difference for deletion-flag synthesis.
        var cellToRefs = BuildCellToRefsIndex(refToCell);
        var actorBaseRemap = BuildDmpActorBaseMasterRemap(dmpRecords);

        // v22: union placements across all parser snapshots of each logical cell. The DMP
        // memory carver routinely produces multiple CellRecord captures for the same cell
        // (different runtime snapshots / mirrored memory regions), and the per-capture
        // PlacedObjects lists diverge. Without unioning, we'd emit only the FIRST capture's
        // placements and silently drop the rest — observed losing 20+ placements per cell
        // when secondary captures held additional content.
        var mergedCells = UnionCellPlacementsAcrossCaptures(dmpRecords.Cells, options);
        var landOverrideBuilder = new LandOverrideBuilder(_sink, RewriteLandTextureFormIds);

        foreach (var dmpCell in mergedCells)
        {
            ct.ThrowIfCancellationRequested();

            var pcCellExists = pcRecordsByFormId.TryGetValue(dmpCell.FormId, out var pcCellRecord)
                               && pcCellRecord!.Header.Signature == "CELL";

            // Build the cell's anchor record bytes + GRUP context.
            byte[] cellRecordBytes;
            PcEsmCellContext context;
            uint emittedCellFormId;

            if (pcCellExists)
            {
                if (!cellContexts.TryGetValue(dmpCell.FormId, out var existingContext))
                {
                    stats.IncrementSkipped("CELL");
                    _sink.Warn("Merging cell children",
                        "Cell has no master GRUP context — skipped.",
                        "CELL", dmpCell.FormId, code: "skipped:no-context");
                    continue;
                }

                cellRecordBytes = CellGrupBuilder.ReconstructRecordBytes(pcCellRecord!);
                context = existingContext;
                emittedCellFormId = dmpCell.FormId;
            }
            else
            {
                // New cell — allocate a plugin-index FormID and synthesize record bytes via CellEncoder.
                if (_encoderRegistry.Get("CELL") is not { } cellEncoder)
                {
                    stats.IncrementSkipped("CELL");
                    _sink.Warn("Merging cell children",
                        "Registry missing CellEncoder — new cell skipped.",
                        "CELL", dmpCell.FormId, code: "skipped:no-cell-encoder");
                    continue;
                }

                if (dmpCell.IsInterior)
                {
                    emittedCellFormId = allocator.Allocate();
                    context = SyntheticInteriorContext(emittedCellFormId);
                }
                else
                {
                    if (!TryBuildSyntheticExteriorContext(dmpCell, allocator, pcRecordsByFormId, stats,
                            out emittedCellFormId, out var exteriorContext))
                    {
                        continue;
                    }

                    context = exteriorContext!;
                }

                cellRecordBytes = BuildNewCellRecordBytes(dmpCell, emittedCellFormId, cellEncoder, options, stats);
                stats.NewRecordsEmitted++;

                if (options.VerboseDecisions)
                {
                    _sink.Decision("Merging cell children",
                        $"New {(dmpCell.IsInterior ? "interior" : "exterior")} CELL allocated FormID 0x{emittedCellFormId:X8} (DMP source 0x{dmpCell.FormId:X8}).",
                        "CELL", dmpCell.FormId);
                }
            }

            // v22: NEW cells (not in master) can't be "override merges" — there's no
            // master GRUP to retain temporaries from, so the only correct policy is to
            // emit every captured placement. The classifier's PersistentOnly mode would
            // otherwise drop all temporary placements (sidewalks, building geometry,
            // clutter) because *none* of a new cell's placements live in the master.
            // For override cells (cell exists in master) the classifier's logic still
            // applies, because the runtime merges DMP overrides into the master GRUP.
            var mode = pcCellExists
                ? CellMerger.Classify(dmpCell, pcRefFormIds)
                : CellMergeMode.HasTemporary;

            // Diagnostic full-replace mode: when the user opts in AND the master cell exists
            // AND the DMP captured any placements for the cell, force HasTemporary mode so all
            // DMP placements emit (not just persistent ones). Combined with the persistent-only
            // preservation filter passed to the deletion synthesizer below, this wipes master's
            // temporary refs from the view while keeping persistent refs alive.
            var replaceTemporaries = options.ReplaceCellTemporariesOnOverride
                && pcCellExists
                && dmpCell.PlacedObjects.Count > 0;
            if (replaceTemporaries)
            {
                mode = CellMergeMode.HasTemporary;
            }

            // Build set of base-record types the DMP could have captured for this cell.
            // The DMP only snapshots TESObjectREFRs the engine has allocated for runtime
            // state (NPCs, containers, doors, interactive furniture). Pure statics
            // (STAT/ACTI/LIGH/MSTT/TREE/TXST) are never in the heap, so master refs of
            // base types absent from this set must be preserved during wipeout.
            HashSet<string>? dmpBaseTypesInCell = null;
            if (replaceTemporaries)
            {
                dmpBaseTypesInCell = new HashSet<string>(StringComparer.Ordinal);
                foreach (var placed in dmpCell.PlacedObjects)
                {
                    if (placed.BaseFormId == 0)
                    {
                        continue;
                    }

                    if (pcRecordsByFormId.TryGetValue(placed.BaseFormId, out var basePc))
                    {
                        dmpBaseTypesInCell.Add(basePc.Header.Signature);
                    }
                    else if (_newRecordSourceToAllocated.TryGetValue(placed.BaseFormId, out var allocatedBase)
                             && pcRecordsByFormId.TryGetValue(allocatedBase, out var basePc2))
                    {
                        dmpBaseTypesInCell.Add(basePc2.Header.Signature);
                    }
                    else if (TryGetNewRecordType(placed.BaseFormId, out var newType))
                    {
                        dmpBaseTypesInCell.Add(newType);
                    }
                }
            }

            var persistentRecords = new List<byte[]>();
            var temporaryRecords = new List<byte[]>();
            var dmpRefFormIdsInCell = new HashSet<uint>();

            // Walk every DMP placed object in this cell. The CellMerger.SelectOverrideRefs
            // filter is for OVERRIDE-ONLY mode; v3 also handles new refs, so we walk the full
            // list and decide per-ref.
            foreach (var placed in dmpCell.PlacedObjects)
            {
                stats.RecordsConsidered++;
                dmpRefFormIdsInCell.Add(placed.FormId);

                if (RuntimeStateFormIds.Contains(placed.FormId))
                {
                    stats.IncrementSkipped(placed.RecordType);
                    _sink.Decision("Merging cell children",
                        $"Skipping runtime-state ref 0x{placed.FormId:X8} — DMP captures live " +
                        "gameplay state for engine-reserved records.",
                        placed.RecordType, placed.FormId, code: "runtime-state.skip-ref");
                    continue;
                }

                var refIsInMaster = pcRecordsByFormId.ContainsKey(placed.FormId);
                var placedForEmit = !refIsInMaster
                    ? RemapNewPlacedActorBase(placed, actorBaseRemap, options)
                    : placed;

                // v22: when the new REFR's base FormID is a DMP-source ID that we've
                // freshly emitted under a new plugin-local FormID, rewrite the BaseFormId
                // to point at the allocation. Without this the encoded NAME subrecord
                // still references the stale DMP FormID and the runtime can't bind it.
                if (!refIsInMaster
                    && placedForEmit.BaseFormId != 0
                    && _newRecordSourceToAllocated.TryGetValue(
                        placedForEmit.BaseFormId, out var allocatedBase))
                {
                    placedForEmit = placedForEmit with { BaseFormId = allocatedBase };
                }

                // Filter NEW placed refs whose base FormID points at a record that doesn't
                // exist in the master ESM AND isn't being freshly emitted by us. ~26% of
                // new-ref bases in prototype DMPs (FO3-era statics, NPCs, doors that didn't
                // survive to FNV release) are unresolvable and at runtime the engine has to
                // fall back to a phantom record — which looks suspiciously like the consistent
                // "TESObjectSTAT FormID: 0000008B" we've seen in every crash log's frame 27.
                // Override refs are fine (the base is already known to exist by definition).
                //
                // v22: also accept bases we emit ourselves via the new-record path. Without
                // this check, REFRs placing freshly-emitted prototype-only STATs (e.g. the
                // monorailPlatform 0x0100108F in TheStripWorld) were getting dropped along
                // with the FO3-vintage dangling ones, leaving the new worldspace empty.
                if (!refIsInMaster
                    && placedForEmit.BaseFormId != 0
                    && _masterFormIds is not null
                    && !_masterFormIds.Contains(placedForEmit.BaseFormId)
                    && !_emittedNewFormIds.Contains(placedForEmit.BaseFormId))
                {
                    // Phase C: last-chance EditorID-stem remap. The prototype's base FormID
                    // doesn't exist in master and isn't being emitted, but if a master record
                    // of the same kind has a matching normalized EditorID stem (e.g.
                    // SCOLParkingLotChunk03 → master SCOLParkingLotChunk03b), rewrite the
                    // REFR's NAME to point at that master record and keep the placement.
                    if (options.EnableRefrBaseEditorIdRemap
                        && this.TryRemapRefrBaseByEditorIdStem(placed, placedForEmit, stats, out var remapped))
                    {
                        placedForEmit = remapped;
                    }
                    else
                    {
                        stats.IncrementSkipped(placed.RecordType);
                        stats.IncrementDropReason("refr.dangling-base");
                        _sink.Decision("Merging cell children",
                            $"Skipping new ref 0x{placed.FormId:X8} — base 0x{placedForEmit.BaseFormId:X8} " +
                            "not in master ESM and not freshly emitted (FO3-vintage / deleted in released FNV).",
                            placed.RecordType, placed.FormId, code: "refr.dangling-base");
                        continue;
                    }
                }

                // Mode A (PersistentOnly): skip non-persistent overrides — DMP didn't actually
                // load this cell, so we can't trust temporary refs. New persistent refs are still
                // allowed (user spec).
                if (mode == CellMergeMode.PersistentOnly && !placed.IsPersistent)
                {
                    continue;
                }

                if (_encoderRegistry.Get(placed.RecordType) is not { } encoder)
                {
                    stats.IncrementSkipped(placed.RecordType);
                    continue;
                }

                var refExistsInPc = pcRecordsByFormId.TryGetValue(placed.FormId, out var pcRefRecord);

                byte[] recordBytes;
                if (refExistsInPc)
                {
                    // Existing ref — override path (v2 logic).
                    if (!TryEncodeOverrideRef(placedForEmit, encoder, pcRefRecord!, policy, options, stats,
                            out var bytes))
                    {
                        continue;
                    }

                    recordBytes = bytes;
                    stats.OverridesEmitted++;
                }
                else
                {
                    // New ref — full-record path (v3).
                    if (!TryEncodeNewRef(placedForEmit, allocator, options, stats, out var bytes))
                    {
                        continue;
                    }

                    recordBytes = bytes;
                    stats.NewRecordsEmitted++;
                }

                if (placed.IsPersistent)
                {
                    persistentRecords.Add(recordBytes);
                }
                else
                {
                    temporaryRecords.Add(recordBytes);
                }

                stats.IncrementEmitted(placed.RecordType);
            }

            // Mode B (HasTemporary) + cell has a master: synthesize deletion-flag overrides for
            // master refs that weren't in the DMP snapshot.
            if (mode == CellMergeMode.HasTemporary
                && pcCellExists
                && cellToRefs.TryGetValue(dmpCell.FormId, out var masterRefIds))
            {
                var masterRefs = masterRefIds
                    .Select(id => pcRecordsByFormId.GetValueOrDefault(id))
                    .Where(r => r is not null)
                    .ToList()!;

                // In diagnostic full-replace mode, skip the structural preservation copy and
                // use a persistent-only filter for the deletion synthesizer so all temporary
                // master refs (structural or not) get a deletion override while every
                // persistent master ref survives untouched.
                int preservedStructural = 0;
                Func<ParsedMainRecord, bool>? preserveFilter;
                if (replaceTemporaries)
                {
                    // Always preserve persistent refs (quest-bound, scripts reference them).
                    // Also preserve master refs whose base type the DMP could not have
                    // captured — see dmpBaseTypesInCell construction above. Without this
                    // filter, master STAT/ACTI/LIGH refs get deleted whenever the DMP
                    // captures *any* placement in the cell, leaving interiors gutted.
                    var capturedTypes = dmpBaseTypesInCell ?? new HashSet<string>(StringComparer.Ordinal);
                    preserveFilter = masterRef =>
                    {
                        // 0x00000400 = Persistent flag on FNV record headers.
                        if ((masterRef.Header.Flags & 0x00000400u) != 0)
                        {
                            return true;
                        }

                        var baseFormId = ReadNameFormId(masterRef);
                        if (!baseFormId.HasValue
                            || !pcRecordsByFormId.TryGetValue(baseFormId.Value, out var baseRec))
                        {
                            // Can't classify the base — preserve rather than risk over-deletion.
                            return true;
                        }

                        return !capturedTypes.Contains(baseRec.Header.Signature);
                    };
                }
                else
                {
                    preservedStructural = PreserveMissingStructuralCellRefs(
                        masterRefs!, dmpRefFormIdsInCell, pcRecordsByFormId,
                        persistentRecords, temporaryRecords, stats);
                    preserveFilter = masterRef => IsStructuralCellRef(masterRef, pcRecordsByFormId);
                }

                var deleted = DeletedRefSynthesizer.Synthesize(
                    masterRefs!, dmpRefFormIdsInCell, preserveFilter);
                persistentRecords.AddRange(deleted.Persistent);
                temporaryRecords.AddRange(deleted.Temporary);

                if (deleted.Persistent.Count + deleted.Temporary.Count > 0 && options.VerboseDecisions)
                {
                    var label = replaceTemporaries ? "Full-replace" : "Wipeout";
                    var typeSuffix = replaceTemporaries && dmpBaseTypesInCell is { Count: > 0 }
                        ? $" across base types [{string.Join(", ", dmpBaseTypesInCell.OrderBy(s => s, StringComparer.Ordinal))}]"
                        : string.Empty;
                    _sink.Decision("Merging cell children",
                        $"{label}: {deleted.Persistent.Count} persistent + {deleted.Temporary.Count} temporary master refs marked deleted{typeSuffix}.",
                        "CELL", dmpCell.FormId,
                        code: replaceTemporaries ? "cell.full-replace-on-override" : null);
                }

                if (preservedStructural > 0 && options.VerboseDecisions)
                {
                    _sink.Decision("Merging cell children",
                        $"Preserved {preservedStructural} structural marker ref(s) missing from DMP cell snapshot.",
                        "CELL", dmpCell.FormId, code: "cell.structural-preserved");
                }
            }

            var hasCapturedTerrain = !dmpCell.IsInterior &&
                                     (dmpCell.Heightmap != null || dmpCell.RuntimeTerrainMesh != null);

            if (persistentRecords.Count == 0 && temporaryRecords.Count == 0 && pcCellExists && !hasCapturedTerrain)
            {
                // Nothing to emit for this existing cell.
                continue;
            }

            // LAND and NAVM both live in Temporary Children. LAND must be first, then NAVM,
            // then REFR/ACHR/etc. records captured from the DMP.
            var temporaryPrefixRecords = new List<byte[]>();

            if (!dmpCell.IsInterior)
            {
                uint? masterLandFormId = null;
                if (pcCellExists
                    && landsByCell.TryGetValue(dmpCell.FormId, out var masterLandFormIds)
                    && masterLandFormIds.Count > 0)
                {
                    masterLandFormId = masterLandFormIds[0];
                }

                ParsedMainRecord? masterLandRecord = null;
                LandVisualData? masterLandVisualData = null;
                if (masterLandFormId.HasValue)
                {
                    pcRecordsByFormId.TryGetValue(masterLandFormId.Value, out masterLandRecord);
                    if (masterLandRecord is not null)
                    {
                        masterLandVisualData = TryExtractMasterLandVisualData(masterLandRecord);
                    }
                }

                if (hasCapturedTerrain &&
                    landOverrideBuilder.TryEncodeForCell(
                        dmpCell,
                        allocator,
                        options,
                        stats,
                        out var landBytes,
                        masterLandFormId,
                        masterLandVisualData))
                {
                    temporaryPrefixRecords.Add(landBytes);
                }
                else if (pcCellExists
                         && masterLandFormId.HasValue
                         && masterLandRecord is not null)
                {
                    // Existing-cell override with no usable DMP terrain: copy the master LAND
                    // verbatim so full-GRUP replacement does not drop terrain from the cell.
                    temporaryPrefixRecords.Add(CellGrupBuilder.ReconstructRecordBytes(masterLandRecord));
                    stats.OverridesEmitted++;
                    stats.IncrementEmitted("LAND");

                    if (options.VerboseDecisions)
                    {
                        _sink.Decision("Merging cell children",
                            $"Preserved vanilla LAND 0x{masterLandFormId.Value:X8} in override cell 0x{dmpCell.FormId:X8}.",
                            "LAND", masterLandFormId.Value, code: "land.preserved");
                    }
                }
            }

            // For override cells, copy vanilla's NAVM (NavMesh) records verbatim into our
            // Temporary Children. FNV's plugin format replaces a master cell's Temporary
            // Children GRUP wholesale when a plugin overrides it — so without this step,
            // every override cell loses its navmesh and NPCs in those cells sink under
            // physics when standing still (idle actors aren't snapped to terrain without
            // a navmesh to anchor them; they pathfind fine mid-walk).
            if (pcCellExists
                && navmsByCell.TryGetValue(dmpCell.FormId, out var masterNavmFormIds))
            {
                var prependedCount = 0;
                foreach (var navmFormId in masterNavmFormIds)
                {
                    if (!pcRecordsByFormId.TryGetValue(navmFormId, out var navmRecord))
                    {
                        continue;
                    }

                    // Reuse the cell-record reconstruction path; it just emits a
                    // ParsedMainRecord back to its on-disk bytes (header + subrecord
                    // stream). NAVM records are opaque payloads as far as we're concerned.
                    var navmBytes = CellGrupBuilder.ReconstructRecordBytes(navmRecord);
                    temporaryPrefixRecords.Add(navmBytes);
                    prependedCount++;
                }

                if (prependedCount > 0)
                {
                    stats.IncrementEmitted("NAVM");
                    if (options.VerboseDecisions)
                    {
                        _sink.Decision("Merging cell children",
                            $"Preserved {prependedCount} vanilla NAVM record(s) in override cell " +
                            $"0x{dmpCell.FormId:X8} so idle NPCs stay anchored.",
                            "CELL", dmpCell.FormId, code: "navm.preserved");
                    }
                }
            }

            if (temporaryPrefixRecords.Count > 0)
            {
                temporaryRecords.InsertRange(0, temporaryPrefixRecords);
            }

            if (persistentRecords.Count == 0 && temporaryRecords.Count == 0)
            {
                // Terrain encoding failed and there were no child records to justify emitting this cell.
                continue;
            }

            bundles.Add(new CellOverrideBundle
            {
                CellFormId = emittedCellFormId,
                Context = context,
                CellRecordBytes = cellRecordBytes,
                PersistentChildRecords = persistentRecords,
                TemporaryChildRecords = temporaryRecords
            });

            stats.CellsMerged++;
        }

        return bundles;
    }

    private LandVisualData? RewriteLandTextureFormIds(LandVisualData? visualData)
    {
        if (visualData == null)
        {
            return null;
        }

        var changed = false;
        var textureIndices = visualData.TextureIndices;
        if (textureIndices is { Length: > 0 })
        {
            var rewritten = new uint[textureIndices.Length];
            for (var i = 0; i < textureIndices.Length; i++)
            {
                var value = textureIndices[i];
                if (_newRecordSourceToAllocated.TryGetValue(value, out var allocated))
                {
                    rewritten[i] = allocated;
                    changed = true;
                }
                else
                {
                    rewritten[i] = value;
                }
            }

            if (changed)
            {
                textureIndices = rewritten;
            }
        }

        List<LandTextureLayer>? textureLayers = null;
        for (var i = 0; i < visualData.TextureLayers.Count; i++)
        {
            var layer = visualData.TextureLayers[i];
            if (_newRecordSourceToAllocated.TryGetValue(layer.TextureFormId, out var allocatedTexture))
            {
                textureLayers ??= new List<LandTextureLayer>(visualData.TextureLayers);
                textureLayers[i] = layer with { TextureFormId = allocatedTexture };
                changed = true;
            }
        }

        return changed
            ? visualData with
            {
                TextureIndices = textureIndices,
                TextureLayers = textureLayers ?? visualData.TextureLayers
            }
            : visualData;
    }

    private static LandVisualData? TryExtractMasterLandVisualData(ParsedMainRecord masterLandRecord)
    {
        if (masterLandRecord.Header.Signature != "LAND")
        {
            return null;
        }

        var recordBytes = CellGrupBuilder.ReconstructRecordBytes(masterLandRecord);
        if (recordBytes.Length <= 24)
        {
            return null;
        }

        var dataSize = recordBytes.Length - 24;
        var data = new byte[dataSize];
        Buffer.BlockCopy(recordBytes, 24, data, 0, dataSize);
        var header = new DetectedMainRecord(
            "LAND",
            (uint)dataSize,
            masterLandRecord.Header.Flags,
            masterLandRecord.Header.FormId,
            masterLandRecord.Offset,
            false);

        return EsmWorldExtractor.ExtractLandFromBuffer(data, dataSize, header)?.VisualData;
    }

    /// <summary>Encode an override ref (FormID matches PC ESM master).</summary>
    private bool TryEncodeOverrideRef(
        Models.World.PlacedReference placed,
        Writers.IRecordEncoder encoder,
        ParsedMainRecord pcRefRecord,
        SubrecordMergePolicy policy,
        PluginBuildOptions options,
        ConversionPipelineStats stats,
        out byte[] bytes)
    {
        bytes = [];

        EncodedRecord encoded;
        try
        {
            encoded = encoder.Encode(placed);
        }
        catch (Exception ex)
        {
            stats.RecordsFailed++;
            stats.Errors++;
            _sink.Error("Merging cell children",
                $"Override encoder threw {ex.GetType().Name}: {ex.Message}",
                placed.RecordType, placed.FormId);
            return false;
        }

        if (encoded.Subrecords.Count == 0)
        {
            stats.IncrementSkipped(placed.RecordType);
            return false;
        }

        var merge = RecordMergeEngine.Merge(pcRefRecord, encoded, policy);
        foreach (var w in merge.Warnings)
        {
            stats.Warnings++;
            _sink.Warn("Merging cell children", w, placed.RecordType, placed.FormId);
        }

        bytes = BuildRecordBytes(pcRefRecord, merge.SubrecordBytes, options);
        return true;
    }

    /// <summary>Encode a new ref (FormID not in PC ESM — v3 new-record path).</summary>
    private bool TryEncodeNewRef(
        Models.World.PlacedReference placed,
        FormIdAllocator allocator,
        PluginBuildOptions options,
        ConversionPipelineStats stats,
        out byte[] bytes)
    {
        bytes = [];

        EncodedRecord encoded;
        try
        {
            encoded = Writers.Encoders.RefrEncoder.EncodeNewPlacedReference(placed);
        }
        catch (Exception ex)
        {
            stats.RecordsFailed++;
            stats.Errors++;
            _sink.Error("Merging cell children",
                $"New-record encoder threw {ex.GetType().Name}: {ex.Message}",
                placed.RecordType, placed.FormId);
            return false;
        }

        foreach (var w in encoded.Warnings)
        {
            stats.Warnings++;
            _sink.Warn("Merging cell children", w, placed.RecordType, placed.FormId);
        }

        var allocatedFormId = allocator.Allocate();
        var flags = ComputeNewRefFlags(placed, options);
        bytes = PluginRecordByteBuilder.BuildNewRecordBytes(placed.RecordType, allocatedFormId, flags, encoded.Subrecords);

        if (options.VerboseDecisions)
        {
            _sink.Decision("Merging cell children",
                $"New {placed.RecordType} allocated FormID 0x{allocatedFormId:X8} (DMP source 0x{placed.FormId:X8}).",
                placed.RecordType, placed.FormId);
        }

        return true;
    }

    private static byte[] BuildNewCellRecordBytes(
        Models.Records.World.CellRecord dmpCell,
        uint emittedFormId,
        Writers.IRecordEncoder cellEncoder,
        PluginBuildOptions options,
        ConversionPipelineStats stats)
    {
        var encoded = cellEncoder.Encode(dmpCell);
        foreach (var w in encoded.Warnings)
        {
            stats.Warnings++;
        }

        // CELL flags don't carry persistent/initially-disabled bits like REFR — start at 0.
        var flags = options.CompressRecords ? 0x00040000u : 0u;
        return PluginRecordByteBuilder.BuildNewRecordBytes("CELL", emittedFormId, flags, encoded.Subrecords);
    }

    /// <summary>
    ///     v22: run the asset-path rename pass when the user has configured secondary data
    ///     folders + a baseline folder. Mutates record string fields in-place when fuzzy
    ///     resolution matches a differently-named asset.
    /// </summary>
    private void TryApplyAssetRenames(
        Models.RecordCollection dmpRecords,
        PluginBuildOptions options,
        CancellationToken ct)
    {
        var renameService = new AssetPacking.AssetRenameService(_sink);
        renameService.Apply(dmpRecords, options, ct);
    }

    /// <summary>
    ///     Returns true when a new-record model's EditorID names an existing master record
    ///     of the same type. The DMP routinely captures duplicate runtime copies of master
    ///     NPCs/creatures with new FormIDs; we want the override path to handle those, not
    ///     a second emitted record. Model EditorID is read via reflection so the check
    ///     works generically for any record type whose model exposes an EditorId property.
    /// </summary>
    private bool TryFindMasterByEditorId(string recordType, object model, out uint masterFormId)
    {
        masterFormId = 0;
        if (_masterEditorIdToFormIdByType is null
            || !_masterEditorIdToFormIdByType.TryGetValue(recordType, out var byEdid))
        {
            return false;
        }

        var editorIdProperty = model.GetType().GetProperty("EditorId");
        if (editorIdProperty?.GetValue(model) is not string editorId
            || string.IsNullOrEmpty(editorId))
        {
            return false;
        }

        return byEdid.TryGetValue(editorId, out masterFormId);
    }

    /// <summary>
    ///     Record types eligible to be referenced as the "base" of a REFR/ACHR/ACRE placed
    ///     reference. Shared with <see cref="ReferenceBaseRemapper"/> so the master index
    ///     and runtime REFR rescue policy use identical type gates.
    /// </summary>
    private static readonly HashSet<string> RefrBaseEligibleTypes = ReferenceBaseRemapper.RefrBaseEligibleTypes;

    /// <summary>
    ///     Phase C: locate a single master record of <paramref name="expectedBaseType"/>
    ///     whose normalized EditorID stem matches <paramref name="prototypeBaseEditorId"/>.
    ///     Returns the master FormID on a unique hit; null on no hit, empty stem, or
    ///     mismatched type; null + <paramref name="ambiguous"/>=true on multiple-candidate
    ///     hits so the caller can log a refusal decision. Extracted as a static helper
    ///     for unit testability — the instance overload wraps it with the builder's
    ///     pre-built stem lookup.
    /// </summary>
    internal static uint? TryFindMasterBaseByEditorIdStem(
        Dictionary<string, Dictionary<string, List<uint>>> stemLookup,
        string? prototypeBaseEditorId,
        string expectedBaseType,
        out bool ambiguous,
        out List<uint>? candidates)
    {
        return ReferenceBaseRemapper.TryFindMasterBaseByEditorIdStem(
            stemLookup,
            prototypeBaseEditorId,
            expectedBaseType,
            out ambiguous,
            out candidates);
    }

    private uint? TryFindMasterBaseByEditorIdStem(
        string? prototypeBaseEditorId,
        string expectedBaseType,
        out bool ambiguous,
        out List<uint>? candidates)
    {
        ambiguous = false;
        candidates = null;
        return _masterStemToFormIdsByType is null
            ? null
            : TryFindMasterBaseByEditorIdStem(
                _masterStemToFormIdsByType, prototypeBaseEditorId, expectedBaseType,
                out ambiguous, out candidates);
    }

    /// <summary>
    ///     Builds DMP actor-base FormID → master actor-base FormID remaps for runtime
    ///     duplicate NPC_/CREA records. New placed ACHR/ACRE refs that point at these DMP
    ///     duplicate bases can then use the complete master actor base instead of emitting
    ///     a partial reconstructed NPC/CREA that may lack animation-critical data.
    /// </summary>
    private Dictionary<uint, uint> BuildDmpActorBaseMasterRemap(RecordCollection dmpRecords)
    {
        var remap = new Dictionary<uint, uint>();

        if (_masterEditorIdToFormIdByType is null)
        {
            return remap;
        }

        if (_masterEditorIdToFormIdByType.TryGetValue("NPC_", out var npcByEditorId))
        {
            foreach (var npc in dmpRecords.Npcs)
            {
                TryAddActorBaseRemap(remap, npc.FormId, npc.EditorId, npcByEditorId);
            }
        }

        if (_masterEditorIdToFormIdByType.TryGetValue("CREA", out var creatureByEditorId))
        {
            foreach (var creature in dmpRecords.Creatures)
            {
                TryAddActorBaseRemap(remap, creature.FormId, creature.EditorId, creatureByEditorId);
            }
        }

        return remap;
    }

    private static void TryAddActorBaseRemap(
        Dictionary<uint, uint> remap,
        uint dmpFormId,
        string? editorId,
        IReadOnlyDictionary<string, uint> masterByEditorId)
    {
        if (dmpFormId == 0
            || string.IsNullOrEmpty(editorId)
            || !masterByEditorId.TryGetValue(editorId, out var masterFormId)
            || masterFormId == dmpFormId)
        {
            return;
        }

        remap.TryAdd(dmpFormId, masterFormId);
    }

    /// <summary>
    ///     Phase C: attempt to rescue an otherwise-orphaned REFR by remapping its
    ///     base FormID to a master record with a matching normalized EditorID stem.
    ///     Returns true (and produces a <paramref name="remapped"/> reference with the
    ///     updated <c>BaseFormId</c>) when a unique same-type stem match exists;
    ///     returns false on no match, ambiguity, or missing prototype EditorID. Counters
    ///     and decision logs are emitted as a side-effect so callers don't need to
    ///     duplicate that bookkeeping.
    /// </summary>
    private bool TryRemapRefrBaseByEditorIdStem(
        Models.World.PlacedReference placed,
        Models.World.PlacedReference placedForEmit,
        ConversionPipelineStats stats,
        out Models.World.PlacedReference remapped)
    {
        remapped = placedForEmit;

        if (string.IsNullOrEmpty(placed.BaseEditorId))
        {
            return false;
        }

        // Determine which master base-record types are valid candidates. ACHR/ACRE are
        // strict (NPC_/CREA only). REFR is more permissive: the prototype base FormID is
        // often not preserved in the DMP as a typed record (e.g., SCOL definitions don't
        // survive the runtime-memory round-trip), so we cannot rely on the typed DMP
        // collections to tell us "this REFR points at a SCOL." Fall back to scanning every
        // REFR-eligible base type's stem index and accept a hit only when exactly one
        // type produces exactly one candidate.
        var candidateTypes = placed.RecordType switch
        {
            "ACHR" => (IReadOnlyList<string>)["NPC_"],
            "ACRE" => ["CREA"],
            "REFR" => _dmpBaseFormIdToRecordType is not null
                      && _dmpBaseFormIdToRecordType.TryGetValue(placedForEmit.BaseFormId, out var typedHit)
                ? [typedHit]
                : RefrBaseEligibleTypes.Except(["NPC_", "CREA"]).ToList(),
            _ => Array.Empty<string>()
        };

        if (candidateTypes.Count == 0)
        {
            return false;
        }

        var hits = new List<(string Type, uint FormId)>();
        var anyAmbiguous = false;
        var ambiguousType = string.Empty;
        var ambiguousCount = 0;

        foreach (var t in candidateTypes)
        {
            var match = TryFindMasterBaseByEditorIdStem(
                placed.BaseEditorId, t, out var ambiguous, out var candidates);
            if (ambiguous)
            {
                anyAmbiguous = true;
                ambiguousType = t;
                ambiguousCount = candidates!.Count;
                break;
            }

            if (match is not null)
            {
                hits.Add((t, match.Value));
            }
        }

        if (anyAmbiguous)
        {
            stats.IncrementDropReason("refr.editorid-remap-ambiguous");
            _sink.Decision("Merging cell children",
                $"Refusing EditorID-stem remap for new ref 0x{placed.FormId:X8} base " +
                $"0x{placedForEmit.BaseFormId:X8} \"{placed.BaseEditorId}\" — stem matches " +
                $"{ambiguousCount} master {ambiguousType} records, ambiguous.",
                placed.RecordType, placed.FormId, code: "refr.editorid-remap-ambiguous");
            return false;
        }

        if (hits.Count == 0)
        {
            return false;
        }

        if (hits.Count > 1)
        {
            stats.IncrementDropReason("refr.editorid-remap-ambiguous");
            _sink.Decision("Merging cell children",
                $"Refusing EditorID-stem remap for new ref 0x{placed.FormId:X8} base " +
                $"0x{placedForEmit.BaseFormId:X8} \"{placed.BaseEditorId}\" — stem matches " +
                $"master records in {hits.Count} different types " +
                $"({string.Join(", ", hits.Select(h => h.Type))}); cross-type ambiguity.",
                placed.RecordType, placed.FormId, code: "refr.editorid-remap-ambiguous");
            return false;
        }

        var (winningType, winningFormId) = hits[0];

        stats.IncrementDropReason("refr.editorid-remap");
        _sink.Decision("Merging cell children",
            $"Remapped new ref 0x{placed.FormId:X8} base 0x{placedForEmit.BaseFormId:X8} " +
            $"\"{placed.BaseEditorId}\" → master {winningType} 0x{winningFormId:X8} by " +
            "EditorID-stem fallback.",
            placed.RecordType, placed.FormId, code: "refr.editorid-remap");

        remapped = placedForEmit with { BaseFormId = winningFormId };
        return true;
    }

    private Models.World.PlacedReference RemapNewPlacedActorBase(
        Models.World.PlacedReference placed,
        IReadOnlyDictionary<uint, uint> actorBaseRemap,
        PluginBuildOptions options)
    {
        if (placed.RecordType is not ("ACHR" or "ACRE")
            || placed.BaseFormId == 0
            || _masterFormIds?.Contains(placed.BaseFormId) == true)
        {
            return placed;
        }

        uint? masterBaseFormId = actorBaseRemap.TryGetValue(placed.BaseFormId, out var mappedBase)
            ? mappedBase
            : TryFindMasterActorBaseByEditorId(placed);

        if (masterBaseFormId is not > 0)
        {
            return placed;
        }

        if (options.VerboseDecisions)
        {
            _sink.Decision("Merging cell children",
                $"New {placed.RecordType} 0x{placed.FormId:X8} base remapped " +
                $"0x{placed.BaseFormId:X8} → master 0x{masterBaseFormId.Value:X8} " +
                "to avoid partial runtime actor-base emission.",
                placed.RecordType, placed.FormId, code: "refr.actor-base-remap");
        }

        return placed with { BaseFormId = masterBaseFormId.Value };
    }

    private uint? TryFindMasterActorBaseByEditorId(Models.World.PlacedReference placed)
    {
        if (_masterEditorIdToFormIdByType is null || string.IsNullOrEmpty(placed.BaseEditorId))
        {
            return null;
        }

        var baseType = placed.RecordType switch
        {
            "ACHR" => "NPC_",
            "ACRE" => "CREA",
            _ => null
        };

        if (baseType is null
            || !_masterEditorIdToFormIdByType.TryGetValue(baseType, out var byEditorId)
            || !byEditorId.TryGetValue(placed.BaseEditorId, out var masterFormId))
        {
            return null;
        }

        return masterFormId;
    }

    /// <summary>
    ///     Phase B SCOL census helper: walks a master SCOL's parsed subrecords and compares
    ///     it to a DMP SCOL to detect override-emission-worthy deltas. Returns true on
    ///     first difference (with a human-readable reason); false when content matches
    ///     within tolerance. Sub-epsilon float drift (0.01 abs) is treated as equal to
    ///     avoid flapping from float-precision noise.
    /// </summary>
    internal static bool TryDetectScolOverrideDelta(
        Models.Records.World.StaticCollectionRecord dmp,
        ParsedMainRecord master,
        out string reason)
    {
        const float epsilon = 0.01f;
        var masterParts = new List<(uint OnamFormId, List<Models.Records.World.StaticCollectionPlacement> Placements)>();
        var currentPart = (-1);

        foreach (var sub in master.Subrecords)
        {
            switch (sub.Signature)
            {
                case "ONAM" when sub.Data.Length == 4:
                    masterParts.Add((BinaryPrimitives.ReadUInt32LittleEndian(sub.Data), new()));
                    currentPart = masterParts.Count - 1;
                    break;
                case "DATA" when sub.Data.Length > 0 && sub.Data.Length % 28 == 0 && currentPart >= 0:
                    var placements = masterParts[currentPart].Placements;
                    var count = sub.Data.Length / 28;
                    for (var i = 0; i < count; i++)
                    {
                        var span = sub.Data.AsSpan(i * 28, 28);
                        placements.Add(new Models.Records.World.StaticCollectionPlacement(
                            BinaryPrimitives.ReadSingleLittleEndian(span[..4]),
                            BinaryPrimitives.ReadSingleLittleEndian(span[4..8]),
                            BinaryPrimitives.ReadSingleLittleEndian(span[8..12]),
                            BinaryPrimitives.ReadSingleLittleEndian(span[12..16]),
                            BinaryPrimitives.ReadSingleLittleEndian(span[16..20]),
                            BinaryPrimitives.ReadSingleLittleEndian(span[20..24]),
                            BinaryPrimitives.ReadSingleLittleEndian(span[24..28])));
                    }

                    break;
            }
        }

        if (dmp.Parts.Count != masterParts.Count)
        {
            reason = $"part count dmp={dmp.Parts.Count} vs master={masterParts.Count}";
            return true;
        }

        for (var i = 0; i < masterParts.Count; i++)
        {
            if (dmp.Parts[i].OnamFormId != masterParts[i].OnamFormId)
            {
                reason = $"part #{i} ONAM dmp=0x{dmp.Parts[i].OnamFormId:X8} vs master=0x{masterParts[i].OnamFormId:X8}";
                return true;
            }

            if (dmp.Parts[i].Placements.Count != masterParts[i].Placements.Count)
            {
                reason = $"part #{i} placement count dmp={dmp.Parts[i].Placements.Count} vs master={masterParts[i].Placements.Count}";
                return true;
            }

            for (var p = 0; p < masterParts[i].Placements.Count; p++)
            {
                var dp = dmp.Parts[i].Placements[p];
                var mp = masterParts[i].Placements[p];
                if (MathF.Abs(dp.X - mp.X) > epsilon || MathF.Abs(dp.Y - mp.Y) > epsilon
                    || MathF.Abs(dp.Z - mp.Z) > epsilon || MathF.Abs(dp.RotX - mp.RotX) > epsilon
                    || MathF.Abs(dp.RotY - mp.RotY) > epsilon || MathF.Abs(dp.RotZ - mp.RotZ) > epsilon
                    || MathF.Abs(dp.Scale - mp.Scale) > epsilon)
                {
                    reason = $"part #{i} placement #{p} floats differ beyond {epsilon} tolerance";
                    return true;
                }
            }
        }

        reason = string.Empty;
        return false;
    }

    /// <summary>
    ///     Returns true when an SCRI subrecord's target FormID would resolve at runtime:
    ///     the sentinel null FormIDs (0 / 0xFFFFFFFF), anything in the master ESM, and
    ///     anything being freshly emitted via the new-record path in the current Build.
    ///     v22 added the new-emit case so reintroduced prototype NPCs can bind to their
    ///     reintroduced scripts. Extracted as a static helper for unit testability.
    /// </summary>
    internal static bool IsValidScriTarget(
        uint formId,
        IReadOnlySet<uint>? masterFormIds,
        IReadOnlySet<uint>? emittedNewFormIds)
    {
        if (formId == 0 || formId == 0xFFFFFFFFu)
        {
            return true;
        }

        if (masterFormIds is not null && masterFormIds.Contains(formId))
        {
            return true;
        }

        if (emittedNewFormIds is not null && emittedNewFormIds.Contains(formId))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Drop SCRI subrecords whose script FormID isn't in the master ESM. Returns the
    ///     original list when nothing was dropped to avoid unnecessary allocation. Logs one
    ///     warning per drop so the user can see what was nulled and which records lose
    ///     scripts.
    /// </summary>
    private IReadOnlyList<Writers.EncodedSubrecord> ValidateScriRefs(
        IReadOnlyList<Writers.EncodedSubrecord> subrecords,
        string recordType,
        uint? sourceFormId)
    {
        if (_masterFormIds is null || _masterFormIds.Count == 0)
        {
            return subrecords;
        }

        List<Writers.EncodedSubrecord>? filtered = null;
        for (var i = 0; i < subrecords.Count; i++)
        {
            var sub = subrecords[i];
            if (sub.Signature != "SCRI" || sub.Bytes.Length != 4)
            {
                filtered?.Add(sub);
                continue;
            }

            var formId = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(sub.Bytes);
            // v22.scri.typed: validate against the SCPT-typed subsets, not the generic union.
            // The loose union check let through SCRI FormIDs that pointed at non-SCPT master
            // records (STAT/ACTI/etc.), and the runtime then logged "Unable to find script"
            // at load time. Falls back to the generic union when the typed maps aren't built.
            var masterScpts = _masterFormIdsByType?.GetValueOrDefault("SCPT") ?? _masterFormIds;
            var emittedScpts = _emittedNewFormIdsByType.GetValueOrDefault("SCPT");
            if (IsValidScriTarget(formId, masterScpts, emittedScpts))
            {
                filtered?.Add(sub);
                continue;
            }

            // Dangling script ref — initialize the copy if we haven't already, then skip
            // this subrecord so the engine sees no SCRI on this record.
            filtered ??= new List<Writers.EncodedSubrecord>(subrecords.Count);
            if (filtered.Count == 0)
            {
                for (var j = 0; j < i; j++)
                {
                    filtered.Add(subrecords[j]);
                }
            }

            _sink.Warn("Merging top-level records",
                $"Dropping SCRI 0x{formId:X8} — script FormID not in master ESM or newly emitted set.",
                recordType, sourceFormId, code: "scri.dangling");
        }

        return filtered ?? subrecords;
    }

    private IReadOnlyList<Writers.EncodedSubrecord> ValidateLtexRefs(
        IReadOnlyList<Writers.EncodedSubrecord> subrecords,
        uint sourceFormId)
    {
        List<Writers.EncodedSubrecord>? rewritten = null;
        for (var i = 0; i < subrecords.Count; i++)
        {
            var sub = subrecords[i];
            var targetType = sub.Signature switch
            {
                "TNAM" when sub.Bytes.Length == 4 => "TXST",
                "GNAM" when sub.Bytes.Length == 4 => "GRAS",
                _ => null
            };

            if (targetType == null)
            {
                rewritten?.Add(sub);
                continue;
            }

            var rawFormId = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(sub.Bytes);
            var formId = _newRecordSourceToAllocated.GetValueOrDefault(rawFormId, rawFormId);
            if (IsKnownFormIdForType(formId, targetType))
            {
                var replacement = formId == rawFormId ? sub : RewriteFormIdSubrecord(sub.Signature, formId);
                rewritten?.Add(replacement);
                continue;
            }

            rewritten ??= new List<Writers.EncodedSubrecord>(subrecords.Count);
            if (rewritten.Count == 0)
            {
                for (var j = 0; j < i; j++)
                {
                    rewritten.Add(subrecords[j]);
                }
            }

            _sink.Warn("Merging top-level records",
                $"Dropping LTEX {sub.Signature} 0x{rawFormId:X8} — target {targetType} FormID " +
                "not in master ESM or newly emitted set.",
                "LTEX", sourceFormId, code: "ltex.dangling-ref");
        }

        return rewritten ?? subrecords;
    }

    /// <summary>
    ///     Resolve the record-type signature of a freshly-emitted FormID. The caller
    ///     supplies either the DMP source FormID or the allocated plugin-local FormID;
    ///     both are searched against <see cref="_emittedNewFormIdsByType" />.
    /// </summary>
    private bool TryGetNewRecordType(uint formId, out string recordType)
    {
        // Normalize through the source→allocated remap so callers can pass either ID.
        var allocated = _newRecordSourceToAllocated.GetValueOrDefault(formId, formId);
        foreach (var (type, set) in _emittedNewFormIdsByType)
        {
            if (set.Contains(allocated))
            {
                recordType = type;
                return true;
            }
        }

        recordType = string.Empty;
        return false;
    }

    private bool IsKnownFormIdForType(uint formId, string recordType)
    {
        if (formId == 0 || formId == 0xFFFFFFFFu)
        {
            return true;
        }

        if (_masterFormIdsByType is null)
        {
            return true;
        }

        if (_masterFormIdsByType.TryGetValue(recordType, out var masterIds) && masterIds.Contains(formId))
        {
            return true;
        }

        return _emittedNewFormIdsByType.TryGetValue(recordType, out var emittedIds) && emittedIds.Contains(formId);
    }

    private static Writers.EncodedSubrecord RewriteFormIdSubrecord(string signature, uint formId)
    {
        var bytes = new byte[4];
        Writers.SubrecordEncoder.WriteFormId(bytes, 0, formId);
        return new Writers.EncodedSubrecord(signature, bytes);
    }

    /// <summary>
    ///     Compute the record header flags for a newly-emitted placed ref. Captures persistent
    ///     and initially-disabled flags from the parsed model.
    /// </summary>
    private static uint ComputeNewRefFlags(Models.World.PlacedReference placed, PluginBuildOptions options)
    {
        uint flags = 0;
        if (placed.IsPersistent)
        {
            flags |= 0x00000400;
        }

        if (placed.IsInitiallyDisabled)
        {
            flags |= 0x00000800;
        }

        if (options.CompressRecords)
        {
            flags |= 0x00040000;
        }

        return flags;
    }

    /// <summary>
    ///     Inverse of refToCell — produces a per-cell list of master REFR/ACHR/ACRE FormIDs.
    /// </summary>
    private static Dictionary<uint, List<uint>> BuildCellToRefsIndex(Dictionary<uint, uint> refToCell)
    {
        var index = new Dictionary<uint, List<uint>>();
        foreach (var (refId, cellId) in refToCell)
        {
            if (!index.TryGetValue(cellId, out var list))
            {
                list = [];
                index[cellId] = list;
            }

            list.Add(refId);
        }

        return index;
    }

    /// <summary>
    ///     Preserve master refs that carry room/portal/occlusion marker data when a loaded
    ///     DMP cell is in wipeout mode. These refs are often editor/runtime infrastructure
    ///     rather than visible objects, so they may not appear in the DMP snapshot even
    ///     though dropping them breaks interior culling and navigation portal behavior.
    /// </summary>
    private static int PreserveMissingStructuralCellRefs(
        IReadOnlyList<ParsedMainRecord> masterRefs,
        ISet<uint> dmpFormIdsInCell,
        IReadOnlyDictionary<uint, ParsedMainRecord> pcRecordsByFormId,
        List<byte[]> persistentRecords,
        List<byte[]> temporaryRecords,
        ConversionPipelineStats stats)
    {
        var preserved = 0;
        foreach (var masterRef in masterRefs)
        {
            if (dmpFormIdsInCell.Contains(masterRef.Header.FormId)
                || !IsStructuralCellRef(masterRef, pcRecordsByFormId))
            {
                continue;
            }

            var bytes = CellGrupBuilder.ReconstructRecordBytes(masterRef);
            if ((masterRef.Header.Flags & 0x00000400u) != 0)
            {
                persistentRecords.Add(bytes);
            }
            else
            {
                temporaryRecords.Add(bytes);
            }

            preserved++;
            stats.IncrementEmitted(masterRef.Header.Signature);
        }

        return preserved;
    }

    private static bool IsStructuralCellRef(
        ParsedMainRecord masterRef,
        IReadOnlyDictionary<uint, ParsedMainRecord> pcRecordsByFormId)
    {
        if (masterRef.Header.Signature != "REFR")
        {
            return false;
        }

        if (masterRef.Subrecords.Any(sub => StructuralCellRefSubrecords.Contains(sub.Signature)))
        {
            return true;
        }

        var baseFormId = ReadNameFormId(masterRef);
        if (!baseFormId.HasValue
            || !pcRecordsByFormId.TryGetValue(baseFormId.Value, out var baseRecord)
            || string.IsNullOrEmpty(baseRecord.EditorId))
        {
            return false;
        }

        return StructuralBaseEditorIdNeedles.Any(needle =>
            baseRecord.EditorId!.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private static uint? ReadNameFormId(ParsedMainRecord record)
    {
        var name = record.Subrecords.FirstOrDefault(sub => sub.Signature == "NAME" && sub.Data.Length >= 4);
        return name is null
            ? null
            : System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(name.Data);
    }

    /// <summary>
    ///     Synthetic context for a newly-allocated interior cell. Goes under block 0 / subblock 0
    ///     (no master to mirror). FNVEdit may flag the placement as non-canonical but the engine
    ///     resolves cells by FormID, not GRUP position.
    /// </summary>
    private static PcEsmCellContext SyntheticInteriorContext(uint cellFormId)
    {
        return new PcEsmCellContext
        {
            CellFormId = cellFormId,
            IsInterior = true,
            WorldspaceFormId = null,
            BlockLabel = new byte[4],    // value = 0
            SubblockLabel = new byte[4], // value = 0
            BlockGroupType = 2,
            SubblockGroupType = 3
        };
    }

    /// <summary>
    ///     Synthetic context for a newly-allocated exterior cell. Block/subblock labels are
    ///     derived from the DMP cell's grid coordinates via floor-division (block = grid/32,
    ///     subblock = grid/8) and packed into the canonical Y-low / X-high uint32 label format.
    ///     Returns false (with a warning) if the DMP cell is missing grid coordinates or its
    ///     parent worldspace isn't present in the master ESM.
    /// </summary>
    private bool TryBuildSyntheticExteriorContext(
        Models.Records.World.CellRecord dmpCell,
        FormIdAllocator allocator,
        Dictionary<uint, ParsedMainRecord> pcRecordsByFormId,
        ConversionPipelineStats stats,
        out uint emittedCellFormId,
        out PcEsmCellContext? context)
    {
        emittedCellFormId = 0;
        context = null;

        if (!dmpCell.WorldspaceFormId.HasValue)
        {
            stats.IncrementSkipped("CELL");
            _sink.Warn("Merging cell children",
                $"New exterior CELL 0x{dmpCell.FormId:X8} skipped — missing parent worldspace FormID.",
                "CELL", dmpCell.FormId, code: "skipped:no-master-worldspace");
            return false;
        }

        var parentWrldFormId = dmpCell.WorldspaceFormId.Value;
        var inMaster = pcRecordsByFormId.TryGetValue(parentWrldFormId, out var wrldRecord)
                       && wrldRecord!.Header.Signature == "WRLD";
        var inNewWrldSet = _newWorldspacesForCellPipeline is not null
                           && _newWorldspacesForCellPipeline.ContainsKey(parentWrldFormId);

        if (!inMaster && !inNewWrldSet)
        {
            stats.IncrementSkipped("CELL");
            _sink.Warn("Merging cell children",
                $"New exterior CELL 0x{dmpCell.FormId:X8} skipped — parent worldspace " +
                $"{parentWrldFormId:X8} not in master ESM and not in deferred new-WRLD set.",
                "CELL", dmpCell.FormId, code: "skipped:no-master-worldspace");
            return false;
        }

        if (!dmpCell.GridX.HasValue || !dmpCell.GridY.HasValue)
        {
            stats.IncrementSkipped("CELL");
            _sink.Warn("Merging cell children",
                $"New exterior CELL 0x{dmpCell.FormId:X8} skipped — missing grid coordinates.",
                "CELL", dmpCell.FormId, code: "skipped:no-grid-coords");
            return false;
        }

        var gridX = dmpCell.GridX.Value;
        var gridY = dmpCell.GridY.Value;

        // v22: dedup by (worldspace, gridX, gridY). Two new cells claiming the same coords
        // in one worldspace make the FNV runtime log "Cell ... already exists at coord"
        // and destroy the second cell, which then takes its placed objects down with it.
        // The DMP parser occasionally mis-attributes wilderness cells across worldspaces
        // (cells from TheStripWorld leaking into CampMcCarranWorld) or finds the same
        // logical cell twice in memory; either way, first-wins is the right policy.
        var coordKey = (dmpCell.WorldspaceFormId!.Value, gridX, gridY);
        if (_emittedExteriorCellCoords.TryGetValue(coordKey, out var existingCellFormId))
        {
            stats.IncrementSkipped("CELL");
            _sink.Warn("Merging cell children",
                $"New exterior CELL 0x{dmpCell.FormId:X8} skipped — worldspace " +
                $"0x{coordKey.Item1:X8} already has cell 0x{existingCellFormId:X8} at " +
                $"grid ({gridX}, {gridY}). First-wins dedup.",
                "CELL", dmpCell.FormId, code: "cell.coord-dup");
            return false;
        }

        // Block/subblock partitioning per ExteriorCellWriter convention:
        //   block = floor(grid / 32), subblock = floor(grid / 8)
        var blockX = (int)Math.Floor(gridX / 32.0);
        var blockY = (int)Math.Floor(gridY / 32.0);
        var subblockX = (int)Math.Floor(gridX / 8.0);
        var subblockY = (int)Math.Floor(gridY / 8.0);

        emittedCellFormId = allocator.Allocate();
        _emittedExteriorCellCoords[coordKey] = emittedCellFormId;
        context = new PcEsmCellContext
        {
            CellFormId = emittedCellFormId,
            IsInterior = false,
            WorldspaceFormId = dmpCell.WorldspaceFormId,
            BlockLabel = ComposeGridLabel(blockX, blockY),
            SubblockLabel = ComposeGridLabel(subblockX, subblockY),
            BlockGroupType = 4,
            SubblockGroupType = 5
        };
        return true;
    }

    /// <summary>
    ///     Compose a 4-byte block/subblock GRUP label from grid coordinates. Y goes in the
    ///     low int16, X in the high int16 — matching <c>EsmEndianHelpers.ComposeGridLabel</c>.
    /// </summary>
    private static byte[] ComposeGridLabel(int x, int y)
    {
        var label = new byte[4];
        unchecked
        {
            var packed = (ushort)y | ((uint)(ushort)x << 16);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(label, packed);
        }

        return label;
    }

    /// <summary>
    ///     Builds the bytes for a single override record: 24-byte header + subrecord stream.
    ///     Inherits flags and metadata from the source ESM record, forces version 0x000F,
    ///     clears the compressed flag (overrides are emitted uncompressed for v1/v2).
    /// </summary>
    private static byte[] BuildRecordBytes(ParsedMainRecord esmRecord, byte[] subrecordBytes, PluginBuildOptions options)
    {
        var flags = esmRecord.Header.Flags;
        if (!options.CompressRecords)
        {
            flags &= ~0x00040000u;
        }
        else
        {
            flags |= 0x00040000u;
        }

        var header = esmRecord.Header with
        {
            DataSize = (uint)subrecordBytes.Length,
            Flags = flags,
            Version = Tes4HeaderBuilder.RecordVersion
        };

        using var stream = new MemoryStream();
        RecordHeaderProcessor.WriteRecordHeader(stream, header);
        stream.Write(subrecordBytes);
        return stream.ToArray();
    }

    /// <summary>
    ///     Walks the digested record collection and yields per-type model lists for the v1
    ///     simple-type set. Cell children (REFR/ACHR/ACRE) are NOT yielded here — they have
    ///     their own pipeline (see <see cref="BuildCellOverrideBundles" />).
    /// </summary>
    private static IEnumerable<(string RecordType, IEnumerable<object> Models)> EnumerateModelsByType(
        RecordCollection records)
    {
        yield return ("GMST", records.GameSettings);
        yield return ("GLOB", records.Globals);
        yield return ("WEAP", records.Weapons);
        yield return ("ARMO", records.Armor);
        yield return ("AMMO", records.Ammo);
        yield return ("ALCH", records.Consumables);
        yield return ("BOOK", records.Books);
        yield return ("MISC", records.MiscItems);
        yield return ("KEYM", records.Keys);
        yield return ("CONT", records.Containers);
        yield return ("FACT", records.Factions);
        yield return ("NPC_", records.Npcs);
        yield return ("SCPT", records.Scripts);
        // DIAL and INFO are handled separately by DialogGrupBuilder so INFOs get nested
        // as type-7 Topic Children GRUPs under each DIAL. Emitting them as two flat
        // top-level GRUPs crashes the FNV runtime on dialog tree walks.
        yield return ("QUST", records.Quests);
        yield return ("PACK", records.Packages);
        yield return ("ACTI", records.Activators);
        yield return ("DOOR", records.Doors);
        yield return ("LIGH", records.Lights);
        yield return ("STAT", records.Statics);
        // SCOL must follow STAT so the per-type emitted-new-STAT set is populated before
        // SCOL's ONAM validation runs (parts pointing at brand-new STATs would otherwise drop).
        yield return ("SCOL", records.StaticCollections);
        yield return ("FURN", records.Furniture);
        yield return ("TERM", records.Terminals);
        yield return ("PROJ", records.Projectiles);
        yield return ("EXPL", records.Explosions);
        yield return ("IMOD", records.WeaponMods);
        yield return ("ARMA", records.ArmorAddons);
        yield return ("RCPE", records.Recipes);
        yield return ("RCCT", records.RecipeCategories);
        yield return ("COBJ", records.ConstructibleObjects);
        yield return ("EYES", records.Eyes);
        yield return ("HAIR", records.Hair);
        yield return ("REPU", records.Reputations);
        yield return ("AVIF", records.ActorValueInfos);
        yield return ("MUSC", records.MusicTypes);
        yield return ("MESG", records.Messages);
        yield return ("NOTE", records.Notes);
        yield return ("FLST", records.FormLists);
        // Leveled lists share one model but three signatures — partition at yield time so
        // each emits under the right wire signature. The encoder handles all three the same.
        yield return ("LVLI", records.LeveledLists.Where(l => l.ListType == "LVLI"));
        yield return ("LVLN", records.LeveledLists.Where(l => l.ListType == "LVLN"));
        yield return ("LVLC", records.LeveledLists.Where(l => l.ListType == "LVLC"));
        yield return ("CREA", records.Creatures);
        yield return ("CLAS", records.Classes);
        yield return ("SOUN", records.Sounds);
        yield return ("TXST", records.TextureSets);
        yield return ("LTEX", records.LandTextures);
        yield return ("CHAL", records.Challenges);
        yield return ("BPTD", records.BodyPartData);
        yield return ("ENCH", records.Enchantments);
        yield return ("SPEL", records.Spells);
        yield return ("PERK", records.Perks);
        yield return ("MGEF", records.BaseEffects);
        yield return ("WRLD", records.Worldspaces);
        yield return ("RACE", records.Races);
    }

    private static uint ExtractFormId(object model)
    {
        var prop = model.GetType().GetProperty("FormId")
                   ?? throw new InvalidOperationException(
                       $"Model {model.GetType().Name} has no FormId property.");
        return (uint)prop.GetValue(model)!;
    }

    private PluginBuildResult Fail(string errorMessage, ConversionPipelineStats stats, Stopwatch sw)
    {
        sw.Stop();
        stats.Elapsed = sw.Elapsed;
        stats.Errors++;
        _sink.Error("PluginBuilder", errorMessage);
        _sink.OnComplete(stats);
        return new PluginBuildResult
        {
            Success = false,
            Stats = stats,
            ErrorMessage = errorMessage
        };
    }
}
