using System.Diagnostics;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema;
using FalloutXbox360Utils.Core.Formats.Esm.Merge;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
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
            var pcRecordsByFormId = pcRecordsList
                .Where(r => r.Header.Signature != "TES4")
                .ToDictionary(r => r.Header.FormId);

            // Populate the validation set used by post-encode FormID checks (e.g. SCRI
            // dangling-ref nullification). Any FormID an emitted subrecord points at that
            // isn't in this set (and isn't sentinel-0 or 0xFFFFFFFF) is unresolvable at
            // runtime and gets dropped to avoid null-deref during master-binding.
            _masterFormIds = new HashSet<uint>(pcRecordsByFormId.Keys);
            _masterFormIdsByType = pcRecordsList
                .Where(r => r.Header.Signature != "TES4")
                .GroupBy(r => r.Header.Signature, StringComparer.Ordinal)
                .ToDictionary(
                    g => g.Key,
                    g => new HashSet<uint>(g.Select(r => r.Header.FormId)),
                    StringComparer.Ordinal);
            _emittedNewFormIds.Clear();
            _emittedNewFormIdsByType.Clear();

            // Build the parent-cell index by walking records in offset order. Children always
            // appear after their parent CELL in the file, so the "most recent CELL" tracker
            // gives correct parentage without needing GRUP context.
            var refToCell = BuildRefToCellIndex(pcRecordsList);

            // NAVM (NavMesh) records live in each cell's Temporary Children GRUP. When a
            // plugin overrides a cell, FNV's full-GRUP-replacement semantics drop the
            // master's NAVMs unless we re-emit them — and a cell with no navmesh leaves
            // idle NPCs unanchored (they sink under physics when standing still while
            // pathfinding still works mid-walk). Build a per-cell NAVM index so override
            // bundles can copy these records verbatim.
            var navmsByCell = BuildNavmByCellIndex(pcRecordsList);

            // Build the cell-context index — maps each CELL FormID to its master GRUP context
            // (block/subblock labels, parent worldspace if exterior). Plugin overrides reuse
            // these labels verbatim so we reproduce the master's exact layout.
            var cellContexts = PcEsmCellContextIndex.Build(pcRecordsList, pcGrupHeaders);

            _sink.Info("Loading PC ESM",
                $"Loaded {pcRecordsByFormId.Count:N0} PC records, {refToCell.Count:N0} child→cell links, {cellContexts.Count:N0} cell contexts.");
            _sink.OnPhaseEnd("Loading PC ESM", stats);
            ct.ThrowIfCancellationRequested();

            // Phase 2: load DMP and parse semantic records.
            _sink.OnPhaseStart("Reading DMP", null);
            using var unified = await SemanticFileLoader.LoadAsync(inputs.DmpPath, cancellationToken: ct);
            var dmpRecords = unified.Records;
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
                dmpRecords, classifier, allocator, inputs.Options, stats);
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
                            recordType, code: $"v1.skipped:{recordType}");
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
                grupBytesByType["QUST"] = AppendOrCreateQustGrup(
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
                navmsByCell, allocator, inputs.Options, stats, ct);
            _sink.Info("Merging cell children",
                $"Built {bundles.Count:N0} cell-override bundle(s); allocated {allocator.NextLocalId - allocator.BaseLocalId:N0} new FormID(s).");
            _sink.OnPhaseEnd("Merging cell children", stats);
            ct.ThrowIfCancellationRequested();

            // Phase 5: assemble TES4 + top-level GRUPs + cell-children GRUP and write output.
            _sink.OnPhaseStart("Writing ESP", null);
            var outputBytes = AssembleEsp(inputs.Options, pcEsmFileInfo.Length, stats, grupBytesByType, bundles, pcRecordsByFormId, allocator);
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
        PluginBuildOptions options,
        ConversionPipelineStats stats)
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
            var recordBytes = BuildNewRecordBytes("WRLD", emittedFormId, flags, encoded.Subrecords);

            result[wrld.FormId] = new NewWorldspaceEntry(emittedFormId, recordBytes);

            if (options.VerboseDecisions)
            {
                _sink.Decision("Merging top-level records",
                    $"Pre-encoded new WRLD 0x{wrld.FormId:X8} → emitted 0x{emittedFormId:X8} " +
                    "(deferred to cell-children pipeline so child cells nest under it).",
                    "WRLD", wrld.FormId, code: "v22.wrld.deferred-with-cells");
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

            var formId = ExtractFormId(model);

            if (RuntimeStateFormIds.Contains(formId))
            {
                stats.IncrementSkipped(recordType);
                _sink.Decision("Merging top-level records",
                    $"Skipping runtime-state record 0x{formId:X8} — DMP captures live gameplay " +
                    "state for engine-reserved records; re-emitting would clobber the engine's " +
                    "runtime setup (player / clock / default weapon).",
                    recordType, formId, code: "v20.runtime-state.skip");
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
                // v4: route through new-record path if this type has a new-record encoder.
                if (TryEncodeNewTopLevelRecord(recordType, model, allocator, options, stats, out var newBytes))
                {
                    grupBodyStream.Write(newBytes);
                    anyEmitted = true;
                    stats.IncrementEmitted(recordType);
                    stats.NewRecordsEmitted++;
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

        return WrapInTopLevelGrup(recordType, grupBodyStream.ToArray());
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
                    recordType, ExtractFormId(model), code: $"v4.skipped:new-{recordType}");
            }

            return false;
        }

        foreach (var w in encoded.Warnings)
        {
            stats.Warnings++;
            _sink.Warn("Merging top-level records", w, recordType, ExtractFormId(model));
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
        recordBytes = BuildNewRecordBytes(recordType, allocatedFormId, flags, validatedSubrecords);

        if (options.VerboseDecisions)
        {
            _sink.Decision("Merging top-level records",
                $"New {recordType} allocated FormID 0x{allocatedFormId:X8} (DMP source 0x{ExtractFormId(model):X8}).",
                recordType, ExtractFormId(model));
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
    private List<CellOverrideBundle> BuildCellOverrideBundles(
        RecordCollection dmpRecords,
        Dictionary<uint, ParsedMainRecord> pcRecordsByFormId,
        Dictionary<uint, uint> refToCell,
        IReadOnlySet<uint> pcRefFormIds,
        Dictionary<uint, PcEsmCellContext> cellContexts,
        Dictionary<uint, List<uint>> navmsByCell,
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

        foreach (var dmpCell in dmpRecords.Cells)
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
                        "CELL", dmpCell.FormId, code: "v3.skipped:no-context");
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
                        "CELL", dmpCell.FormId, code: "v3.skipped:no-cell-encoder");
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

            var mode = CellMerger.Classify(dmpCell, pcRefFormIds);

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
                        placed.RecordType, placed.FormId, code: "v20.runtime-state.skip-ref");
                    continue;
                }

                // Filter NEW placed refs whose base FormID points at a record that doesn't
                // exist in the master ESM. ~26% of new-ref bases in prototype DMPs (FO3-era
                // statics, NPCs, doors that didn't survive to FNV release) are unresolvable
                // and at runtime the engine has to fall back to a phantom record — which
                // looks suspiciously like the consistent "TESObjectSTAT FormID: 0000008B"
                // we've seen in every crash log's frame 27. Override refs are fine (the
                // base is already known to exist by definition); just filter new refs.
                var refIsInMaster = pcRecordsByFormId.ContainsKey(placed.FormId);

                if (!refIsInMaster
                    && placed.BaseFormId != 0
                    && _masterFormIds is not null
                    && !_masterFormIds.Contains(placed.BaseFormId))
                {
                    stats.IncrementSkipped(placed.RecordType);
                    _sink.Decision("Merging cell children",
                        $"Skipping new ref 0x{placed.FormId:X8} — base 0x{placed.BaseFormId:X8} " +
                        "not in master ESM (FO3-vintage / deleted in released FNV).",
                        placed.RecordType, placed.FormId, code: "v20.refr.dangling-base");
                    continue;
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
                    if (!TryEncodeOverrideRef(placed, encoder, pcRefRecord!, policy, options, stats, out var bytes))
                    {
                        continue;
                    }

                    recordBytes = bytes;
                    stats.OverridesEmitted++;
                }
                else
                {
                    // New ref — full-record path (v3).
                    if (!TryEncodeNewRef(placed, allocator, options, stats, out var bytes))
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

                var deleted = DeletedRefSynthesizer.Synthesize(masterRefs!, dmpRefFormIdsInCell);
                persistentRecords.AddRange(deleted.Persistent);
                temporaryRecords.AddRange(deleted.Temporary);

                if (deleted.Persistent.Count + deleted.Temporary.Count > 0 && options.VerboseDecisions)
                {
                    _sink.Decision("Merging cell children",
                        $"Wipeout: {deleted.Persistent.Count} persistent + {deleted.Temporary.Count} temporary master refs marked deleted.",
                        "CELL", dmpCell.FormId);
                }
            }

            if (persistentRecords.Count == 0 && temporaryRecords.Count == 0 && pcCellExists)
            {
                // Nothing to emit for this existing cell.
                continue;
            }

            // For new exterior cells with captured heightmap, emit a LAND record at the
            // start of Temporary Children. Without LAND, the engine renders the cell at
            // worldspace-default land elevation and any DMP-captured XCLW water height
            // floods the cell (cf. "underwater Strip" symptom in v21).
            if (!pcCellExists
                && !dmpCell.IsInterior
                && options.NewRecordBaseFormId != 0u  // sanity (allocator exists)
                && TryEncodeLandForCell(dmpCell, allocator, options, stats, out var landBytes))
            {
                // Prepend: LAND must come before REFR/ACHR records in Temporary Children.
                temporaryRecords.Insert(0, landBytes);
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
                    temporaryRecords.Insert(prependedCount, navmBytes);
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
                            "CELL", dmpCell.FormId, code: "v22.navm.preserved");
                    }
                }
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

    /// <summary>
    ///     Build a LAND record (header + DATA/VNML/VHGT subrecords) for a new exterior
    ///     cell with captured heightmap data. Prefers the runtime mesh's exact heights
    ///     when present (lossless), falling back to the parsed VHGT-style heightmap.
    /// </summary>
    private bool TryEncodeLandForCell(
        Models.Records.World.CellRecord dmpCell,
        FormIdAllocator allocator,
        PluginBuildOptions options,
        ConversionPipelineStats stats,
        out byte[] landBytes)
    {
        landBytes = [];

        Models.World.LandHeightmap? heightmap = dmpCell.Heightmap;
        if (heightmap is null && dmpCell.RuntimeTerrainMesh is not null)
        {
            try
            {
                heightmap = dmpCell.RuntimeTerrainMesh.ToLandHeightmap();
            }
            catch
            {
                heightmap = null;
            }
        }

        if (heightmap is null)
        {
            return false;
        }

        var subs = Writers.Encoders.LandEncoder.Encode(heightmap);
        if (subs is null)
        {
            return false;
        }

        var landFormId = allocator.Allocate();
        var flags = options.CompressRecords ? 0x00040000u : 0u;
        landBytes = BuildNewRecordBytes("LAND", landFormId, flags, subs);
        stats.NewRecordsEmitted++;
        stats.IncrementEmitted("LAND");

        if (options.VerboseDecisions)
        {
            _sink.Decision("Merging cell children",
                $"Emitted LAND 0x{landFormId:X8} for new exterior cell 0x{dmpCell.FormId:X8} " +
                $"(grid {dmpCell.GridX}, {dmpCell.GridY}).",
                "LAND", landFormId, code: "v22.land.new-cell");
        }

        return true;
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
        bytes = BuildNewRecordBytes(placed.RecordType, allocatedFormId, flags, encoded.Subrecords);

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
        return BuildNewRecordBytes("CELL", emittedFormId, flags, encoded.Subrecords);
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
        if (options.AssetRenameBaselineFolder is null
            || options.AssetRenameSecondaryFolders.Count == 0)
        {
            return;
        }

        if (!Directory.Exists(options.AssetRenameBaselineFolder))
        {
            _sink.Warn("AssetRename",
                $"Baseline folder not found, skipping rename pass: {options.AssetRenameBaselineFolder}");
            return;
        }

        _sink.Info("AssetRename",
            $"Indexing baseline: {options.AssetRenameBaselineFolder}");
        using var baseline = new AssetPacking.DataFolderIndex(
            options.AssetRenameBaselineFolder, xbox360FormatHint: false);
        baseline.Build();

        var secondaryIndexes = new List<AssetPacking.DataFolderIndex>();
        try
        {
            foreach (var secondary in options.AssetRenameSecondaryFolders)
            {
                ct.ThrowIfCancellationRequested();
                if (!Directory.Exists(secondary.Path))
                {
                    _sink.Warn("AssetRename",
                        $"Secondary folder not found, skipping: {secondary.Path}");
                    continue;
                }

                _sink.Info("AssetRename",
                    $"Indexing {(secondary.IsXbox360Format ? "Xbox 360" : "PC")} secondary: {secondary.Path}");
                var idx = new AssetPacking.DataFolderIndex(secondary.Path, secondary.IsXbox360Format);
                idx.Build();
                secondaryIndexes.Add(idx);
            }

            if (secondaryIndexes.Count == 0)
            {
                _sink.Warn("AssetRename",
                    "No secondary folders available — rename pass has no candidates.");
                return;
            }

            var resolver = new AssetPacking.DataFolderResolver(baseline, secondaryIndexes);
            var result = AssetPacking.AssetPathRewriter.ApplyRewrites(dmpRecords, resolver, _sink);

            _sink.Info("AssetRename",
                $"Considered={result.Considered:N0}, rewritten={result.Rewritten:N0}, " +
                $"exact={result.SkippedExact:N0}, missing={result.SkippedMissing:N0}");
        }
        finally
        {
            foreach (var idx in secondaryIndexes)
            {
                idx.Dispose();
            }
        }
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
                recordType, sourceFormId, code: "v22.scri.dangling");
        }

        return filtered ?? subrecords;
    }

    private static byte[] BuildNewRecordBytes(
        string signature,
        uint formId,
        uint flags,
        IReadOnlyList<Writers.EncodedSubrecord> subrecords)
    {
        // Concatenate subrecord bytes.
        using var subStream = new MemoryStream();
        using (var writer = new BinaryWriter(subStream, System.Text.Encoding.Latin1, true))
        {
            foreach (var sub in subrecords)
            {
                Writers.SubrecordEncoder.WriteSubrecord(writer, sub.Signature, sub.Bytes);
            }
        }

        var subBytes = subStream.ToArray();

        var header = new MainRecordHeader
        {
            Signature = signature,
            DataSize = (uint)subBytes.Length,
            Flags = flags,
            FormId = formId,
            Timestamp = 0,
            VcsInfo = 0,
            Version = Tes4HeaderBuilder.RecordVersion
        };

        using var stream = new MemoryStream();
        RecordHeaderProcessor.WriteRecordHeader(stream, header);
        stream.Write(subBytes);
        return stream.ToArray();
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
                "CELL", dmpCell.FormId, code: "v4.skipped:no-master-worldspace");
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
                "CELL", dmpCell.FormId, code: "v4.skipped:no-master-worldspace");
            return false;
        }

        if (!dmpCell.GridX.HasValue || !dmpCell.GridY.HasValue)
        {
            stats.IncrementSkipped("CELL");
            _sink.Warn("Merging cell children",
                $"New exterior CELL 0x{dmpCell.FormId:X8} skipped — missing grid coordinates.",
                "CELL", dmpCell.FormId, code: "v4.skipped:no-grid-coords");
            return false;
        }

        var gridX = dmpCell.GridX.Value;
        var gridY = dmpCell.GridY.Value;

        // Block/subblock partitioning per ExteriorCellWriter convention:
        //   block = floor(grid / 32), subblock = floor(grid / 8)
        var blockX = (int)Math.Floor(gridX / 32.0);
        var blockY = (int)Math.Floor(gridY / 32.0);
        var subblockX = (int)Math.Floor(gridX / 8.0);
        var subblockY = (int)Math.Floor(gridY / 8.0);

        emittedCellFormId = allocator.Allocate();
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
    ///     Walk PC ESM records in offset order and build a parent-cell index. For each
    ///     REFR/ACHR/ACRE record, the most recently-seen CELL record is its parent.
    /// </summary>
    private static Dictionary<uint, uint> BuildRefToCellIndex(List<ParsedMainRecord> records)
    {
        var refToCell = new Dictionary<uint, uint>();
        uint? currentCell = null;

        foreach (var record in records.OrderBy(r => r.Offset))
        {
            switch (record.Header.Signature)
            {
                case "CELL":
                    currentCell = record.Header.FormId;
                    break;
                case "REFR" or "ACHR" or "ACRE":
                    if (currentCell.HasValue)
                    {
                        refToCell[record.Header.FormId] = currentCell.Value;
                    }

                    break;
            }
        }

        return refToCell;
    }

    /// <summary>
    ///     Walk PC ESM records in offset order and group NAVM (NavMesh) FormIDs by their
    ///     parent CELL FormID. NAVMs live in a cell's Temporary Children GRUP alongside
    ///     REFRs; when a plugin overrides that cell, FNV's GRUP-replacement semantics drop
    ///     the master's NAVM records unless we re-emit them. Returns a per-cell list (the
    ///     same cell can host multiple NAVM records — one per nav-island).
    /// </summary>
    private static Dictionary<uint, List<uint>> BuildNavmByCellIndex(List<ParsedMainRecord> records)
    {
        var navmsByCell = new Dictionary<uint, List<uint>>();
        uint? currentCell = null;

        foreach (var record in records.OrderBy(r => r.Offset))
        {
            switch (record.Header.Signature)
            {
                case "CELL":
                    currentCell = record.Header.FormId;
                    break;
                case "NAVM":
                    if (currentCell.HasValue)
                    {
                        if (!navmsByCell.TryGetValue(currentCell.Value, out var list))
                        {
                            list = [];
                            navmsByCell[currentCell.Value] = list;
                        }

                        list.Add(record.Header.FormId);
                    }

                    break;
            }
        }

        return navmsByCell;
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
    ///     Wraps a top-level GRUP for the given record type around a record byte stream.
    ///     Top-level GRUPs have group type 0 and label = the 4-character record-type signature.
    /// </summary>
    private static byte[] WrapInTopLevelGrup(string recordType, byte[] recordsBody)
    {
        if (recordType.Length != 4)
        {
            throw new ArgumentException($"Record type must be 4 chars, got '{recordType}'.", nameof(recordType));
        }

        var label = new byte[4];
        label[0] = (byte)recordType[0];
        label[1] = (byte)recordType[1];
        label[2] = (byte)recordType[2];
        label[3] = (byte)recordType[3];

        using var stream = new MemoryStream();
        var grupHeader = new GroupHeader
        {
            GroupSize = 0,
            Label = label,
            GroupType = 0,
            Stamp = 0,
            Unknown = 0
        };
        var headerPos = RecordHeaderProcessor.WriteGrupHeader(stream, grupHeader);
        stream.Write(recordsBody);
        RecordHeaderProcessor.FinalizeGrupSize(stream, headerPos);
        return stream.ToArray();
    }

    /// <summary>
    ///     Append the placeholder QUST record to an existing QUST GRUP body (rewrapping with
    ///     a fresh group header for the new size), or build a brand-new top-level QUST GRUP
    ///     when no QUST records were emitted.
    /// </summary>
    private static byte[] AppendOrCreateQustGrup(byte[]? existingQustGrup, byte[] extraRecord)
    {
        if (existingQustGrup is null || existingQustGrup.Length == 0)
        {
            return WrapInTopLevelGrup("QUST", extraRecord);
        }

        // Strip the existing GRUP's 24-byte header, append the new record, then rewrap so
        // the GroupSize field reflects the new total. RecordHeaderProcessor.FinalizeGrupSize
        // handles the size calculation.
        const int grupHeaderSize = 24;
        var oldBody = existingQustGrup.AsSpan(grupHeaderSize).ToArray();
        var combined = new byte[oldBody.Length + extraRecord.Length];
        oldBody.CopyTo(combined, 0);
        extraRecord.CopyTo(combined, oldBody.Length);
        return WrapInTopLevelGrup("QUST", combined);
    }

    /// <summary>
    ///     Concatenates TES4 + each emitted top-level GRUP + the cell-children section
    ///     (top-level CELL GRUP for interior bundles + one top-level WRLD GRUP per affected
    ///     worldspace). Top-level GRUP order follows the registry's declared order.
    /// </summary>
    private byte[] AssembleEsp(
        PluginBuildOptions options,
        long masterFileSize,
        ConversionPipelineStats stats,
        Dictionary<string, byte[]> grupBytesByType,
        IReadOnlyList<CellOverrideBundle> bundles,
        Dictionary<uint, ParsedMainRecord> pcRecordsByFormId,
        FormIdAllocator allocator)
    {
        var optionsForBuild = options with { MasterFileSize = masterFileSize };

        var orderedGrups = new List<byte[]>();
        foreach (var recordType in _encoderRegistry.SupportedRecordTypes)
        {
            // Cell-child and CELL types live inside the cell hierarchy, not in top-level GRUPs.
            if (RecordEncoderRegistry.IsCellChildRecordType(recordType)
                || RecordEncoderRegistry.IsCellRecordType(recordType))
            {
                continue;
            }

            if (grupBytesByType.TryGetValue(recordType, out var bytes))
            {
                orderedGrups.Add(bytes);
            }
        }

        var cellSectionBytes = CellGrupBuilder.BuildCellSection(
            bundles, pcRecordsByFormId, _newWorldspacesForCellPipeline);

        // NextObjectId tells GECK where to start allocating new FormIDs when the user adds
        // records via the editor. v3 uses the allocator's high-water mark so GECK won't
        // collide with any FormID we already assigned.
        var nextObjectId = allocator.HasAllocations ? allocator.NextObjectId : 0x800u;
        var tes4 = Tes4HeaderBuilder.Build(optionsForBuild, (uint)stats.RecordsEmitted, nextObjectId);

        using var stream = new MemoryStream();
        stream.Write(tes4);
        foreach (var grup in orderedGrups)
        {
            stream.Write(grup);
        }

        if (cellSectionBytes != null)
        {
            stream.Write(cellSectionBytes);
        }

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
