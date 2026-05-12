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

            // Build the parent-cell index by walking records in offset order. Children always
            // appear after their parent CELL in the file, so the "most recent CELL" tracker
            // gives correct parentage without needing GRUP context.
            var refToCell = BuildRefToCellIndex(pcRecordsList);

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

            var classifier = new NewVsOverrideClassifier(pcRecordsByFormId.Keys);

            // Single allocator shared across phases — Phase 3 (new top-level records) and
            // Phase 4 (new cells/refs). NextObjectId in TES4 reflects the high-water mark.
            var allocator = new FormIdAllocator(inputs.Options.NewRecordBaseFormId);

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

            _sink.OnPhaseEnd("Merging top-level records", stats);
            ct.ThrowIfCancellationRequested();

            // Phase 4: cell-children merging. Builds CellOverrideBundles for each affected cell.
            // v3 also allocates plugin-index FormIDs for new cells/refs and synthesizes
            // deletion-flag overrides for HasTemporary cells.
            _sink.OnPhaseStart("Merging cell children", null);
            var pcRefFormIds = new HashSet<uint>(refToCell.Keys);
            var bundles = BuildCellOverrideBundles(
                dmpRecords, pcRecordsByFormId, refToCell, pcRefFormIds, cellContexts, allocator,
                inputs.Options, stats, ct);
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

            if (!classifier.IsOverride(formId))
            {
                // v4: route through new-record path if this type has a new-record encoder.
                if (TryEncodeNewTopLevelRecord(recordType, model, allocator, options, stats, out var newBytes))
                {
                    grupBodyStream.Write(newBytes);
                    anyEmitted = true;
                    stats.IncrementEmitted(recordType);
                    stats.NewRecordsEmitted++;
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

        if (encoded.Subrecords.Count == 0)
        {
            return false;
        }

        var allocatedFormId = allocator.Allocate();
        var flags = options.CompressRecords ? 0x00040000u : 0u;
        recordBytes = BuildNewRecordBytes(recordType, allocatedFormId, flags, encoded.Subrecords);

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

        if (!dmpCell.WorldspaceFormId.HasValue
            || !pcRecordsByFormId.TryGetValue(dmpCell.WorldspaceFormId.Value, out var wrldRecord)
            || wrldRecord!.Header.Signature != "WRLD")
        {
            stats.IncrementSkipped("CELL");
            _sink.Warn("Merging cell children",
                $"New exterior CELL 0x{dmpCell.FormId:X8} skipped — parent worldspace {dmpCell.WorldspaceFormId:X8} not in master ESM.",
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

        var cellSectionBytes = CellGrupBuilder.BuildCellSection(bundles, pcRecordsByFormId);

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
        yield return ("DIAL", records.DialogTopics);
        yield return ("INFO", records.Dialogues);
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
