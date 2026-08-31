using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.Formats.Esm.Merge;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Formats.Esm.PlannedWriter;
using BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Cells;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Planner.Cells;
using BethesdaMultitool.Core.Formats.Esm.Planner.Disposition;
using BethesdaMultitool.Core.Formats.Esm.Planner.Disposition.Policies;
using BethesdaMultitool.Core.Formats.Esm.Planner.References;
using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Nav;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Output;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Validation;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Formats.Esm.Reporting;
using BethesdaMultitool.Core.Formats.Esm.Script;
using BethesdaMultitool.Core.Recovery;
using BethesdaMultitool.Core.Semantic;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;

/// <summary>
///     Converts an Xbox 360 DMP into a PC plugin ESM, using a base FalloutNV.esm as the
///     source of subrecord data the DMP doesn't carry. This is the public entrypoint: the
///     thin <c>PluginConversionPipeline</c> wrapper that used to front a separate
///     <c>PluginConversionPipeline</c> was folded in on 2026-08-13, once the class had stopped being a
///     dual-path builder and become the pipeline itself.
///     <para>Pipeline:</para>
///     1. Load PC ESM (raw + semantic) and index FormIDs.
///     2. Load DMP semantically (cells with placed objects, simple-type record lists).
///     3. Plan: <c>EsmPlanner</c> settles every emission decision — dispositions, FormID
///     allocation, reference resolution, cell merge modes, per-placed-ref verdicts and
///     cell-emission gates — before any byte is encoded.
///     4. Serialize: <c>PlanWriter</c> emits the top-level GRUPs and
///     <c>PlanCellSectionBuilder</c> the CELL hierarchy, from the plan alone.
///     5. Assemble ESM: TES4 header (record count counted from the emitted body) + GRUPs.
///     6. Optionally validate by re-parsing.
///     <para>
///         The legacy single-pass emission path this class used to carry alongside the planner
///         was deleted on 2026-08-11 — see the retirement notes in <c>BuildGrupForType</c>,
///         <c>BuildPlannerStateIfEnabled</c> and <see cref="EsmAssembler" />.
///     </para>
/// </summary>
public sealed class PluginConversionPipeline
{
    /// <summary>
    ///     FormIDs being emitted via the new-record path in the current <c>Build</c> run.
    ///     Populated as records dispatch through <c>TryEncodeNewTopLevelRecord</c>. Feeds
    ///     <see cref="IsValidScriTarget" /> so an override-NPC's SCRI pointing at a freshly
    ///     emitted SCPT (which won't be in <see cref="_masterFormIds" />) survives the
    ///     dangling-ref drop. Reset to empty at the start of each Build.
    /// </summary>
    private readonly HashSet<uint> _emittedNewFormIds = [];

    /// <summary>
    ///     Per-record-type subset of <see cref="_emittedNewFormIds" />. Used by SCRI's
    ///     type-aware validator so it accepts new SCPT FormIDs but rejects new STAT/etc.
    /// </summary>
    private readonly Dictionary<string, HashSet<uint>> _emittedNewFormIdsByType = new();

    private readonly RecordEncoderRegistry _encoderRegistry;

    /// <summary>
    ///     Master ESM exterior cells indexed by (worldspace, gridX, gridY). Populated at
    ///     <see cref="BuildAsync" /> entry by walking <c>pcRecordsByFormId</c> + the cell
    ///     contexts. Used to detect grid collisions: when a DMP cell has a fresh FormID but
    ///     master already has a cell at the same grid in the same worldspace, we redirect
    ///     to override master's cell instead of allocating a duplicate (the FNV runtime
    ///     destroys duplicate-grid cells at load time, orphaning every REFR we placed in
    ///     them). Reset at each Build entry.
    /// </summary>
    private readonly Dictionary<(uint Worldspace, int GridX, int GridY), uint> _masterExteriorCellByGrid = new();

    /// <summary>
    ///     Master ESM NPCs indexed by race FormID → first master NPC FormID seen for that
    ///     race. Used by the new-NPC emit path to retarget a renderable template when a
    ///     captured prototype NPC's Template chain dead-ends in another new NPC (which
    ///     would have no FaceGen .NIF / .dds files on disk, so the engine's render walk
    ///     access-violates in NiAlphaProperty / BSFadeNode when the player gets near).
    ///     By pointing Template at a renderable master NPC and setting the Use-Traits flag,
    ///     the engine inherits the master's face/body and skips loading our (missing)
    ///     FaceGen output. Reset at each Build entry.
    /// </summary>
    private readonly Dictionary<uint, uint> _masterNpcByRace = new();

    /// <summary>
    ///     DMP-source FormID → emitted FormID. Values are either freshly-allocated plugin-local
    ///     IDs for true new records or master FormIDs for same-type EditorID aliases (prototype
    ///     records that became final master records under a different FormID). The downstream
    ///     emit paths use it to rewrite each FormID-bearing subrecord before encoding — without
    ///     this remap the output can point at DMP-only source IDs that exist in neither master
    ///     nor the freshly-allocated 0x01xxxxxx plugin range. Reset at each Build entry.
    /// </summary>
    private readonly Dictionary<uint, uint> _newRecordSourceToAllocated = new();

    /// <summary>
    ///     Record type for each DMP-source key in <see cref="_newRecordSourceToAllocated" />.
    ///     Placed-reference base remapping must be type-aware: an ACRE can only point to
    ///     CREA, an ACHR can only point to NPC_, and a REFR must not point to actor bases.
    /// </summary>
    private readonly Dictionary<uint, string> _newRecordSourceToAllocatedType = new();

    private readonly IConversionProgressSink _sink;

    /// <summary>
    ///     Phase C: DMP-prototype FormID → record-type signature. Built once at
    ///     <see cref="BuildAsync" /> entry from the typed <see cref="RecordCollection" />
    ///     lists. The REFR remap predicate uses it to pick the expected base type when
    ///     scanning master candidates (a STAT-typed prototype base remaps to STAT only,
    ///     not to ACTI / SCOL / etc.).
    /// </summary>
    private Dictionary<uint, string>? _dmpBaseFormIdToRecordType;

    /// <summary>
    ///     Per-build planner state. Constructed once in <see cref="BuildAsync" /> after the
    ///     DMP loads for the complete <c>PlannedEncoders</c> catalog, then consumed by
    ///     <see cref="BuildGrupForType" /> and the planner-owned cell section. The retired
    ///     legacy emission path no longer provides a planner-free build mode.
    /// </summary>
    private EmitPlan? _emitPlan;

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
    ///     Exact, case-insensitive SCPT EditorID → master FormID identities. Kept separate
    ///     from placed-reference base and stem indexes because SCPT is never a legal REFR
    ///     base, while duplicate script names still collide in the engine's script registry.
    /// </summary>
    private Dictionary<string, uint>? _masterScriptFormIdByEditorId;

    private PlanWriter? _planWriter;

    /// <summary>
    ///     Append-only master-script locals required by recovered INFO/PACK conditions in
    ///     the DMP currently being converted. The sanitizer owns allocation and condition
    ///     remapping; planner and legacy SCPT writers execute these same directives.
    /// </summary>
    private IReadOnlyList<ScriptVariableAugmentation>
        _scriptVariableAugmentations = [];

    /// <summary>
    ///     Surviving fresh-local mappings used to re-prove writes in INFO result scripts
    ///     after the dialogue section has actually emitted.
    /// </summary>
    private IReadOnlyList<QuestVariableRecoveryMapping> _scriptVariableProducerMappings = [];

    /// <summary>
    ///     Fresh-local producer obligations settled before planning. The final plan must
    ///     retain a listed SCPT/PACK/TERM owner, or final dialogue emission must re-prove
    ///     an exact INFO result-script owner, for every emitted augmentation.
    /// </summary>
    private IReadOnlyList<QuestVariableProducerRequirement> _scriptVariableProducerRequirements = [];

    /// <summary>Creates the builder with the record-encoder registry and an optional progress sink.</summary>
    public PluginConversionPipeline(RecordEncoderRegistry registry, IConversionProgressSink? sink = null)
    {
        _encoderRegistry = registry;
        _sink = sink ?? NullConversionProgressSink.Instance;
    }

    /// <summary>
    ///     Read-only test surface for <see cref="_newRecordSourceToAllocated" /> so Phase 0
    ///     tests can verify the source→allocated remap was registered.
    /// </summary>
    internal IReadOnlyDictionary<uint, uint> NewRecordSourceToAllocatedForTest => _newRecordSourceToAllocated;

    /// <summary>
    ///     Read-only test surface for <see cref="_emittedNewFormIdsByType" /> so Phase 0
    ///     tests can verify the per-type validator set was extended.
    /// </summary>
    internal IReadOnlyDictionary<string, HashSet<uint>> EmittedNewFormIdsByTypeForTest => _emittedNewFormIdsByType;

    /// <summary>
    ///     Every record-type signature <see cref="EnumerateModelsByType" /> yields — i.e. the set
    ///     of types that can reach a top-level GRUP at all.
    ///     <para>
    ///         This is the authoritative reachability oracle, and it is <b>not</b> the same as
    ///         "has a registered encoder". A type absent from here is structurally unemittable no
    ///         matter what encoders exist: the merge loop only iterates what this yields, so such a
    ///         type is dropped before the "No encoder for {type}" warning can even fire, leaving no
    ///         diagnostic. GRAS/IMGS/PWAT/TREE all had registered encoders while being unreachable
    ///         exactly this way. Conversely, membership here is necessary but not sufficient — the
    ///         type still needs a registry entry to be encoded, and a
    ///         <c>NewTopLevelRecordEncoderDispatcher</c> row for its <i>new</i> records.
    ///     </para>
    ///     <para>
    ///         Types emitted outside the top-level loop are deliberately absent: cell children
    ///         (REFR/ACHR/ACRE/LAND, via the cell pipeline), NAVM (byte-rewriter) and NAVI
    ///         (<c>EsmAssembler</c> fallback).
    ///     </para>
    /// </summary>
    public static IReadOnlySet<string> EmittableTopLevelRecordTypes { get; } =
        EnumerateModelsByType(new RecordCollection())
            .Select(entry => entry.RecordType)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    ///     Run the conversion pipeline. The output is a plugin ESM file at
    ///     <see cref="DmpToEsmInputs.OutputEsmPath" /> on success.
    /// </summary>
    public async Task<PluginBuildResult> BuildAsync(DmpToEsmInputs inputs, CancellationToken ct = default)
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
            var masterDialogueIndex = MasterDialogueIndex.Build(pcRecordsList, pcGrupHeaders);
            var pcRecordsByFormId = masterIndex.RecordsByFormId;

            // Populate the validation set used by post-encode FormID checks (e.g. SCRI
            // dangling-ref nullification). Any FormID an emitted subrecord points at that
            // isn't in this set (and isn't sentinel-0 or 0xFFFFFFFF) is unresolvable at
            // runtime and gets dropped to avoid null-deref during master-binding.
            _masterFormIds = masterIndex.FormIds;
            _masterFormIdsByType = masterIndex.FormIdsByType;
            var masterChildFormIds = new HashSet<uint>(masterIndex.ChildLocations.Keys);
            _emittedNewFormIds.Clear();
            _emittedNewFormIdsByType.Clear();
            _masterExteriorCellByGrid.Clear();
            _masterNpcByRace.Clear();
            _newRecordSourceToAllocated.Clear();
            _newRecordSourceToAllocatedType.Clear();
            _scriptVariableAugmentations = [];
            _scriptVariableProducerRequirements = [];
            _scriptVariableProducerMappings = [];
            _masterEditorIdToFormIdByType = masterIndex.EditorIdToFormIdByType;
            _masterScriptFormIdByEditorId = masterIndex.ScriptFormIdByEditorId;

            // Placed-ref overrides must be emitted under their master child GRUP, not under
            // whichever runtime cell snapshot happened to mention them.
            var refToCell = masterIndex.RefToCell;

            // Build the cell-context index — maps each CELL FormID to its master GRUP context
            // (block/subblock labels, parent worldspace if exterior). Plugin overrides reuse
            // these labels verbatim so we reproduce the master's exact layout.
            var cellContexts = masterIndex.CellContexts;

            // Build the (worldspace, gridX, gridY) → master CELL FormID index so the new-cell
            // allocation path can detect grid collisions. The FNV runtime destroys duplicate
            // cells at load time and any REFR placed in them becomes orphaned — which is the
            // root cause of the WastelandNV render crashes we hit when a prototype DMP cell
            // had a fresh FormID but happened to share grid coords with a final-build cell.
            foreach (var (cellFormId, ctx) in cellContexts)
            {
                if (ctx.IsInterior || !ctx.WorldspaceFormId.HasValue)
                {
                    continue;
                }

                if (!pcRecordsByFormId.TryGetValue(cellFormId, out var cellRec))
                {
                    continue;
                }

                if (!TryReadCellGridCoords(cellRec, out var gridX, out var gridY))
                {
                    continue;
                }

                _masterExteriorCellByGrid[(ctx.WorldspaceFormId.Value, gridX, gridY)] = cellFormId;
            }

            // Build the race → master NPC index so the new-NPC emit path can pick a
            // renderable template fallback. Walks master NPC records, reads each one's
            // RNAM (race FormID), keeps the first NPC seen for each race. Templates pointing
            // at master NPCs with valid FaceGen on disk skip the crash class triggered by
            // missing FaceGen files for our newly-emitted NPCs.
            foreach (var (formId, record) in pcRecordsByFormId)
            {
                if (record.Header.Signature != "NPC_")
                {
                    continue;
                }

                if (!TryReadNpcRaceFormId(record, out var raceFormId))
                {
                    continue;
                }

                _masterNpcByRace.TryAdd(raceFormId, formId);
            }

            _sink.Info("Loading PC ESM",
                $"Loaded {pcRecordsByFormId.Count:N0} PC records, {refToCell.Count:N0} child→cell links, " +
                $"{cellContexts.Count:N0} cell contexts, {_masterExteriorCellByGrid.Count:N0} exterior cells indexed by grid, " +
                $"{_masterNpcByRace.Count:N0} races with NPC templates.");
            _sink.OnPhaseEnd("Loading PC ESM", stats);
            ct.ThrowIfCancellationRequested();

            // Phase 2: load DMP and parse semantic records.
            _sink.OnPhaseStart("Reading DMP", null);
            using var unified = await SemanticFileLoader.LoadAsync(
                inputs.DmpPath,
                new SemanticFileLoadOptions
                {
                    FileType = AnalysisFileType.Minidump,
                    ApplyDefaultCellWorldspaceAuthority = false,
                    // Recover master refs resident in the dump that the engine's form-table walk
                    // missed (partial-dump broken hash buckets). The cell merge would otherwise
                    // delete them as "uncaptured"; the master child→cell map tells us which heap
                    // refs to look for and where they belong. Pure proto data, master-scoped.
                    // Scoped to REFR (not ACHR/ACRE) so every swept hit is unambiguously a REFR —
                    // actors are persistent and already captured, so excluding them costs nothing.
                    ResidentRecoveryMasterFormIds = new HashSet<uint>(
                        refToCell.Keys.Where(k =>
                            pcRecordsByFormId.TryGetValue(k, out var r) && r.Header.Signature == "REFR")),
                    GapRecovery = inputs.Options.RecoverGaps
                        ? DmpGapRecoveryOptions.PromoteAllValidated
                        : DmpGapRecoveryOptions.Disabled
                },
                ct);
            var dmpRecords = unified.Records;
            ReportGapRecovery(unified.RawResult, stats);
            ApplyCellWorldspaceAuthority(
                dmpRecords,
                unified.RawResult.EsmRecords,
                inputs.Options.CellWorldspaceAuthority,
                inputs.Options.CellWorldspaceAuthorityWorldspaceNames,
                inputs.Options.CellMetadataAuthority,
                inputs.Options.CellReferenceParentAuthority,
                inputs.Options.CellReferenceParentWindows,
                inputs.Options.InferUnresolvedCellPlacements);
            FilterDmpRecordsByExcludedWorldspaces(dmpRecords, inputs.Options.SkipWorldspaceFormIds);
            _dmpBaseFormIdToRecordType = ReferenceBaseRemapper.BuildDmpBaseFormIdToRecordType(dmpRecords);
            _sink.Info("Reading DMP", "DMP semantic load complete.");
            _sink.OnPhaseEnd("Reading DMP", stats);
            ct.ThrowIfCancellationRequested();

            // Asset-rename pass: rewrite record paths in-place when fuzzy resolution
            // matches a differently-named asset in an indexed Data folder. Runs BEFORE
            // encoding so the output ESM carries the unified paths. No-op when the user
            // didn't configure rename folders.
            TryApplyAssetRenames(dmpRecords, inputs.Options, ct);

            var classifier = new NewVsOverrideClassifier(pcRecordsByFormId.Keys);

            // Single allocator shared across phases — Phase 3 (new top-level records) and
            // Phase 4 (new cells/refs). NextObjectId in TES4 reflects the high-water mark.
            var allocator = new FormIdAllocator(inputs.Options.NewRecordBaseFormId);

            // Treat DMP records whose same-type EditorID names a master record as aliases
            // from prototype/source FormID to final master FormID. This lets carried-over
            // scripts, inventory, packages, cell refs, and appearance pointers resolve to
            // the final ID before any bytes are merged or written.
            RegisterEditorIdMasterAliases(dmpRecords, classifier, inputs.Options);

            // Preserve the slots consumed by the retired pre-encoded WRLD path without
            // publishing aliases or bytes. CellSectionPlanner owns the real WRLD allocation
            // and PlanCellSectionBuilder owns its canonical WRLD + World Children layout.
            // Keeping these reservation-only holes preserves the prior allocator sequence.
            var newWorldspaceReservations = NewWorldspaceFormIdReservationPlanner.Reserve(
                dmpRecords, classifier, allocator, _newRecordSourceToAllocated);
            if (newWorldspaceReservations.Length > 0)
            {
                _sink.Info("Merging top-level records",
                    $"Reserved {newWorldspaceReservations.Length:N0} historical new-WRLD allocator " +
                    "slot(s); the planner will allocate and emit the live WRLD anchors.",
                    code: "allocation.reserve.legacy-wrld");
            }

            // EDID-based SCRI fallback: when an NPC/creature has no captured script binding
            // (its TESScriptableForm::pFormScript slot was null at DMP capture), attach a
            // script whose EditorId is "<formEditorId>Script". This matches FNV vanilla's
            // naming convention (e.g. CassFollowerScript → CassFollower; UlyssesScript →
            // Ulysses) and recovers orphan-script bindings the proto's runtime hadn't yet
            // wired up. This is the SOLE recovery path for null pFormScript on NPCs/creatures
            // — RuntimeActorReader no longer brute-force-scans struct memory for Script*
            // pointers, because the prefix-based gate was unsafe (VMS01PartsSCRIPT falsely
            // matched VMS01DocMitchell, breaking the Doc Mitchell intro and Sunny Smiles
            // quest state).
            AttachOrphanScriptsByEditorId(dmpRecords);

            // One runtime INFO can be recovered through more than one capture path. Collapse
            // those copies before condition sanitation: otherwise a discarded copy can reserve
            // a fresh quest-script local or suppress its shared FormID before dialogue planning
            // chooses the authoritative capture.
            var duplicateDialogueInfos = DialogueCombinePlanner.DeduplicateInPlace(dmpRecords.Dialogues);
            if (duplicateDialogueInfos > 0)
            {
                _sink.Info("DialogueTextBackfill",
                    $"Collapsed {duplicateDialogueInfos:N0} duplicate INFO capture(s) before condition " +
                    "sanitation and CSV selection so direct DMP text remains authoritative.",
                    code: "dialog.info-dedup");
            }

            // User-authorized writer reconstruction must land before condition sanitation so
            // the producer gate can structurally prove the synthesized write like any capture.
            DialogueWriterSynthesizer.Apply(dmpRecords.Dialogues, _sink);

            SanitizeQuestVariableConditions(
                dmpRecords,
                pcRecordsByFormId,
                classifier,
                masterDialogueIndex,
                stats,
                inputs.Options.SkipRecordTypes);
            EnsureScriptVariableAugmentationsCanBeEmitted(
                _scriptVariableAugmentations,
                inputs.Options.SkipRecordTypes);

            // EDID-based VTCK fallback: NPCs may carry a VTCK FormID from the proto runtime
            // that no longer exists in vanilla or our build (FormID 0x0014F3EB on Ulysses, for
            // example — that FormID is a hole between MaleUniqueTheKing (0x...EC) and
            // RobotUniqueRex (0x...EA)). When the engine can't resolve VTCK, it falls back to
            // MaleAdult01Default and audio lookups under the unique voicetype directory all
            // fail. Re-bind the VTCK to a matching VTYP whose EditorId is
            // "MaleUnique<NpcEditorId>" / "FemaleUnique<NpcEditorId>" — vanilla's convention.
            AttachOrphanVoiceTypesByEditorId(dmpRecords);

            // Recover orphan placed refs that share a (worldspace, grid) with a non-virtual cell
            // before the grid-collision dedup drops them. Gated on _masterFormIds so MASTER cells
            // are never keepers — merging orphan refs into a live master persistent cell (e.g.
            // TheStripWorldNew's 0x0013B310) corrupts a real game cell and crashes on GridCellArray
            // attach. Targets cut/proto content only (e.g. TheStripWorld's VStreetFluff markers).
            // Must run before planning so recovered refs are allocated + emitted.
            WorldRecordHandler.MergeColocatedVirtualOrphanCells(dmpRecords.Cells, _masterFormIds);

            // The legacy Phase-0 placed-ref pre-allocation ran here until the 2026-08-11
            // retirement (Stage G). CellChildAllocator inside the planner allocates every new
            // placed ref before any encoder runs, so PACK PLDT/PTDT Union targets resolve from
            // the plan; Phase 0's parallel allocation only burned FormIDs and (until Stage B)
            // leaked never-emitted IDs into the dialogue and asset-packing remap tables.
            //
            // Build the EmitPlan: every record type is planner-owned, so this is where all
            // emission decisions get settled before a single byte is encoded.
            BuildPlannerStateIfEnabled(
                pcRecordsList, dmpRecords, allocator, inputs,
                cellContexts, pcRecordsByFormId, masterIndex, stats);

            if (_emitPlan is not null && newWorldspaceReservations.Length > 0)
            {
                _emitPlan = _emitPlan with
                {
                    FormIdReservations = newWorldspaceReservations
                        .AddRange(_emitPlan.FormIdReservations)
                };
            }

            // PlanWriter bypasses the legacy allocation bookkeeping. Bridge its actual NEW
            // records before any legacy-routed top-level encoder runs, so cross-pipeline refs
            // (notably ACTI/DOOR/FURN/QUST SCRI -> planner SCPT) remap and validate correctly.
            // Diagnostic skips are excluded: a planned record suppressed by --skip-record-type
            // must not become a phantom-valid target in the legacy pipeline.
            if (_emitPlan is not null)
            {
                PlannerLegacyStateBridge.Merge(
                    _emitPlan.SourceToEmittedFormId,
                    emitted => _emitPlan.RecordIndexByEmittedFormId.TryGetValue(emitted, out var recordIndex)
                               && !inputs.Options.SkipRecordTypes.Contains(_emitPlan.Records[recordIndex].Type)
                        ? _emitPlan.Records[recordIndex].Type
                        : null,
                    _newRecordSourceToAllocated,
                    _newRecordSourceToAllocatedType);
                PlannerLegacyStateBridge.RegisterEmittedNewRecords(
                    _emitPlan.Records,
                    inputs.Options.SkipRecordTypes,
                    TrackEmittedNewFormId);
            }

            // Phase 3: top-level record merging (GMST, GLOB, WEAP, …).
            _sink.OnPhaseStart("Merging top-level records", null);
            var grupBytesByType = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var (recordType, models) in EnumerateModelsByType(dmpRecords))
            {
                ct.ThrowIfCancellationRequested();
                if (inputs.Options.SkipRecordTypes.Contains(recordType))
                {
                    var dropped = 0;
                    foreach (var _ in models)
                    {
                        dropped++;
                        stats.IncrementSkipped(recordType);
                    }

                    if (dropped > 0)
                    {
                        _sink.Info("Merging top-level records",
                            $"Diagnostic --skip-record-type: dropped {dropped:N0} {recordType} record(s) " +
                            "from emission.");
                    }

                    continue;
                }

                // Routing precondition: a type with no planned encoder would emit nothing at
                // all now that the legacy branch is gone, so skip it loudly rather than
                // silently. PlannerRoutingConsistencyTests keeps the tables in agreement.
                if (!_planWriter?.Handles(recordType) ?? true)
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
                            $"No planned encoder for {recordType} — {skipped} record(s) skipped.",
                            recordType, code: $"skipped:{recordType}");
                    }

                    continue;
                }

                var grupBytes = BuildGrupForType(recordType, inputs.Options);
                if (grupBytes.Length > 0)
                {
                    grupBytesByType[recordType] = grupBytes;
                }
            }

            // Dialogue-text backfill from --dialogue-audio-csv: when the DMP capture left a
            // response blank or marked it "(NOT FOUND IN CRASH DUMP)", the audio CSV's Text
            // column (one row per voice file = one response number) supplies the missing line
            // so the engine emits real dialog instead of the placeholder sentinel. Applied
            // in-place to the already-deduplicated dmpRecords.Dialogues before encoding.
            if (inputs.Options.DialogueTextOverridesCsvPaths.Count > 0)
            {
                DialogueTextBackfill.ApplyFromCsvs(
                    dmpRecords.Dialogues,
                    inputs.Options.DialogueTextOverridesCsvPaths,
                    _sink);
            }

            // DIAL+INFO are not in EnumerateModelsByType — emit them as a single nested
            // section so each DIAL is followed by its type-7 Topic Children GRUP of INFOs.
            // Master FormIDs feed the FormID validator inside the builder: any FormID
            // reference (QSTI/ANAM/NAME/TCLT/TCLF/SCRO/CTDA-FormID-params) that isn't a
            // known master record AND isn't one of our newly-allocated FormIDs is replaced
            // with 0 to keep the runtime from null-deref'ing on dangling cross-refs.
            // CTDA Parameter1/Parameter2 sanitizer needs the runtime→emitted alias table
            // (remap step) and the set of FormIDs we've already emitted for other top-level
            // record types (so a CTDA referencing a new QUST/NPC/SPEL FormID stays valid).
            // _newRecordSourceToAllocated.Values is the full set of allocated new FormIDs at
            // this point (top-level encoders ran above; DIAL/INFO allocate inside the call).
            // Include master child records too so INFO result-script SCRO references to
            // placed refs / actors survive validation instead of being zeroed.
            // Build voice-type EditorId + NPC→voice-type lookups so DialogGrupBuilder can
            // emit dialogue-audio bindings keyed on the (voicetype_edid, dial_edid, resp_num)
            // triple — the asset packer uses these to bridge build-era FormID drift in the
            // dialogue-audio CSV.

            // Feed the planner's new-record allocations into the dialogue remap. Under
            // Under planner emission, new proto QUST/NPC_/CREA/… records come from PlanWriter
            // (BuildGrupForType), which returns BEFORE the legacy TrackNewRecordSourceAlias call —
            // so their source→emitted mappings live only in _emitPlan.SourceToEmittedFormId, and
            // DialogGrupBuilder can't resolve an INFO's QSTI/ANAM to a proto quest/NPC → the
            // reference is nulled → the whole INFO is dropped (droppedNoQstiInfos), which in-game
            // leaves the affected actors force-greeting with no dialogue.
            // PlannerLegacyStateBridge merges only records the planner actually
            // emits (skipping allocated-but-unemitted orphans) and leaves DIAL/INFO to
            // DialogGrupBuilder, which allocates their real FormIDs itself.
            if (_emitPlan is not null)
            {
                PlannerLegacyStateBridge.Merge(
                    _emitPlan.SourceToEmittedFormId,
                    emitted => _emitPlan.RecordIndexByEmittedFormId.TryGetValue(emitted, out var recordIndex)
                        ? _emitPlan.Records[recordIndex].Type
                        : null,
                    _newRecordSourceToAllocated,
                    _newRecordSourceToAllocatedType);
            }

            var voiceTypeEditorIdsByFormId = BuildVoiceTypeEditorIdLookup(pcRecordsByFormId, dmpRecords);
            var npcVoiceTypeByNpcFormId = BuildNpcVoiceTypeLookup(dmpRecords, _newRecordSourceToAllocated);
            // Quest source-FormID → EDID lookup, unifying master + DMP quests. Needed by
            // CollectAudioBindings to record QuestEditorId on each emitted binding (so the
            // packer can reconstruct the engine-shaped voice filename when CSV truncation
            // differs from what the runtime constructs).
            var questEditorIdsByFormId = BuildQuestEditorIdLookup(
                pcRecordsByFormId, dmpRecords, _newRecordSourceToAllocated);
            var dialogAdditionalValidFormIds =
                _newRecordSourceToAllocated.Values.Concat(masterChildFormIds);

            // Every planner-emitted FormID (new plugin records + retained master records) is live —
            // union it so a dialogue reference to a planner-owned record isn't zeroed by the
            // validator. Complements the remap merge above (which fixes source→emitted translation).
            if (_emitPlan is not null)
            {
                dialogAdditionalValidFormIds = dialogAdditionalValidFormIds.Concat(_emitPlan.EmittedFormIds);
            }

            // Dialogue must keep its historic allocation range, but GetScriptVariable
            // owner liveness is not authoritative until CELL children have been written.
            // Run a side-effect-free reservation build now, then encode for real after
            // Phase 4 with the same allocator start and actual placed-owner set.
            var dialogReservationStartLocalId = allocator.NextLocalId;
            DialogSectionResult dialogResult;
            if (inputs.Options.SkipRecordTypes.Contains("DIAL"))
            {
                foreach (var _ in dmpRecords.DialogTopics)
                {
                    stats.IncrementSkipped("DIAL");
                }

                foreach (var _ in dmpRecords.Dialogues)
                {
                    stats.IncrementSkipped("INFO");
                }

                dialogResult = new DialogSectionResult([], null);
                _sink.Info("Building dialog section",
                    "Diagnostic --skip-record-type DIAL suppressed the complete nested DIAL/INFO section.",
                    code: "dialog.skip-type");
            }
            else
            {
                _ = DialogGrupBuilder.BuildDialogSection(
                    dmpRecords.DialogTopics, dmpRecords.Dialogues, classifier, allocator,
                    pcRecordsByFormId.Keys, pcRecordsByFormId,
                    new ConversionPipelineStats(), NullConversionProgressSink.Instance,
                    _newRecordSourceToAllocated,
                    dialogAdditionalValidFormIds,
                    voiceTypeEditorIdsByFormId,
                    npcVoiceTypeByNpcFormId,
                    questEditorIdsByFormId,
                    masterDialogueIndex,
                    inputs.Options.DiagnosticKeepMasterFormIds,
                    inputs.Options.DiagnosticRetainMasterSubrecords,
                    _emitPlan?.SourceToEmittedFormId,
                    _scriptVariableProducerMappings);
                dialogResult = new DialogSectionResult([], null);
            }

            var dialogReservationEndLocalId = allocator.NextLocalId;

            _sink.OnPhaseEnd("Merging top-level records", stats);
            ct.ThrowIfCancellationRequested();

            // Phase 4: cell-children merging. Builds CellOverrideBundles for each affected cell.
            // Also allocates plugin-index FormIDs for new cells/refs and synthesizes
            // deletion-flag overrides for LoadedReplacement cells.
            _sink.OnPhaseStart("Merging cell children", null);

            // NAVI used to have to trail this pass so its rows could come from NAVMs that
            // were actually written — a planned NAVM whose bundle got suppressed must not get
            // an NVMI row (dangling entries null-deref NavMeshInfoMap at load). Retirement
            // Stage H6 moved both halves of that answer into the plan (PlanNavmEmission for
            // the written set, PlanNavmConnectivity for the NVCI graph), so the ordering is no
            // longer load-bearing and NAVI below reads the plan directly.
            //
            // The legacy BuildCellOverrideBundles pass that used to run alongside this was
            // deleted in the 2026-08-11 retirement (Stage F). It had been building bundles
            // that EsmAssembler discarded, while still consuming allocator slots and
            // appending orphan synthetic DOOR bases into the live top-level GRUP.
            if (_emitPlan is null)
            {
                throw new InvalidOperationException(
                    "Cell emission requires a plan; BuildPlannerStateIfEnabled produced none.");
            }

            var plannerCellSection = PlanCellSectionBuilder.BuildCellSectionCore(
                _emitPlan, pcRecordsByFormId, inputs.Options, stats, masterIndex,
                _dmpBaseFormIdToRecordType);
            // The written-NAVM set is a plan fact (PlanNavmEmission), so NAVI row selection no
            // longer depends on having built the cell section first. Only NVCI connectivity
            // still reads the emitted bytes.
            var plannedNavms = _emitPlan.NavmEntries.Length;
            var writtenNavms = _emitPlan.NavmEntries.Count(e => _emitPlan.EmittedNavmFormIds.Contains(e.NavmFormId));
            if (writtenNavms != plannedNavms)
            {
                _sink.Warn("Merging cell children",
                    $"{plannedNavms - writtenNavms:N0} planned NAVM(s) were not written (suppressed cells); " +
                    "their NAVI rows are filtered.",
                    code: "navi.plan-emit-divergence");
            }

            var naviSource = _emitPlan.NavmEntries
                .Where(e => _emitPlan.EmittedNavmFormIds.Contains(e.NavmFormId))
                .Select(e => new NewNavmEntry(e.NavmFormId, e.LocationFormId, e.IsInterior,
                    (short)e.GridX, (short)e.GridY, e.NvvxBytes.Length > 0 ? e.NvvxBytes : null))
                .ToList();
            if (naviSource.Count > 0
                && pcRecordsByFormId.TryGetValue(NavInfoMapBuilder.MasterNaviFormId, out var masterNavi)
                && masterNavi.Header.Signature == "NAVI")
            {
                var naviOverrideBytes = NavInfoMapBuilder.BuildNaviOverride(
                    masterNavi, naviSource, inputs.Options, _emitPlan.NavmConnectivityByFormId);
                AppendOrCreateTopLevelRecord(grupBytesByType, "NAVI", naviOverrideBytes);
                _sink.Info("Merging cell children",
                    $"Emitted NAVI override with {naviSource.Count:N0} new NVMI+NVCI entry pair(s) " +
                    $"(extends master 0x{NavInfoMapBuilder.MasterNaviFormId:X8}). " +
                    "NavMeshInfoMap can now resolve our new NAVM FormIDs at plugin load.",
                    code: "navi.override-emitted");
            }
            else if (naviSource.Count > 0)
            {
                _sink.Warn("Merging cell children",
                    $"Emitted {naviSource.Count:N0} new NAVM(s) but master NAVI " +
                    $"(0x{NavInfoMapBuilder.MasterNaviFormId:X8}) was not in the PC ESM index — skipping NAVI override.",
                    code: "navi.master-missing");
            }

            var liveScriptVariableOwnerFormIds = new HashSet<uint>(refToCell.Keys);
            var actuallyEmittedPlacedRefs = plannerCellSection.EmittedPlacedReferenceFormIds;
            liveScriptVariableOwnerFormIds.UnionWith(actuallyEmittedPlacedRefs);

            if (!inputs.Options.SkipRecordTypes.Contains("DIAL"))
            {
                var finalDialogueAllocator = new FormIdAllocator(dialogReservationStartLocalId);
                dialogResult = DialogGrupBuilder.BuildDialogSection(
                    dmpRecords.DialogTopics, dmpRecords.Dialogues, classifier, finalDialogueAllocator,
                    pcRecordsByFormId.Keys, pcRecordsByFormId, stats, _sink,
                    _newRecordSourceToAllocated,
                    dialogAdditionalValidFormIds,
                    voiceTypeEditorIdsByFormId,
                    npcVoiceTypeByNpcFormId,
                    questEditorIdsByFormId,
                    masterDialogueIndex,
                    inputs.Options.DiagnosticKeepMasterFormIds,
                    inputs.Options.DiagnosticRetainMasterSubrecords,
                    _emitPlan?.SourceToEmittedFormId,
                    _scriptVariableProducerMappings,
                    liveScriptVariableOwnerFormIds);
                if (finalDialogueAllocator.NextLocalId != dialogReservationEndLocalId)
                {
                    throw new InvalidOperationException(
                        "Dialogue reservation/encode allocation drifted after cell-owner liveness " +
                        $"filtering (reserved next=0x{dialogReservationEndLocalId:X6}, final " +
                        $"next=0x{finalDialogueAllocator.NextLocalId:X6}).");
                }
            }

            QuestVariableProducerGate.VerifyFinalOutput(
                _scriptVariableProducerRequirements,
                _planWriter?.ProducerLedger ?? PlanProducerEmissionLedger.Empty,
                dialogResult.ProducerLedger);
            if (dialogResult.DialogSection.Length > 0)
            {
                grupBytesByType["DIAL"] = dialogResult.DialogSection;
            }

            // Record only INFO identities that survived the post-cell owner-liveness gate.
            foreach (var (sourceId, allocatedId) in dialogResult.NewInfoSourceToAllocated)
            {
                _newRecordSourceToAllocated.TryAdd(sourceId, allocatedId);
            }

            _sink.Info("Building dialog section",
                $"Recorded {dialogResult.NewInfoSourceToAllocated.Count:N0} surviving INFO " +
                $"source→allocated mapping(s) after checking {liveScriptVariableOwnerFormIds.Count:N0} " +
                "master/actually-emitted script-variable owner(s).",
                code: "dialog.remap.recorded");

            if (dialogResult.PlaceholderQustRecord is { Length: > 0 } placeholderQust)
            {
                grupBytesByType["QUST"] = TopLevelRecordEmitter.AppendOrCreateQustGrup(
                    grupBytesByType.GetValueOrDefault("QUST"), placeholderQust);
            }

            _sink.OnPhaseEnd("Merging cell children", stats);
            ct.ThrowIfCancellationRequested();

            // Phase 5: assemble TES4 + top-level GRUPs + cell-children GRUP and write output.
            _sink.OnPhaseStart("Writing ESM", null);
            var outputBytes = new EsmAssembler(_encoderRegistry).Assemble(
                inputs.Options,
                pcEsmFileInfo.Length,
                stats,
                grupBytesByType,
                pcRecordsByFormId,
                allocator,
                _emitPlan,
                masterIndex,
                plannerCellSection);
            await File.WriteAllBytesAsync(inputs.OutputEsmPath, outputBytes, ct);
            stats.OutputBytes = outputBytes.LongLength;
            _sink.OnPhaseEnd("Writing ESM", stats);

            // Phase 6 (optional): validate by re-parsing + semantic check.
            string? validationReport = null;
            if (inputs.Options.ValidateOutput)
            {
                _sink.OnPhaseStart("Validating output", null);
                var roundTrip = PluginRoundTripValidator.Validate(outputBytes, stats.RecordsEmitted);
                _sink.Info("Validating output", roundTrip);

                // Semantic check catches the structural issues that round-trip parsing alone
                // misses — duplicate FormIDs, persistent-flag/parent-GRUP-type mismatches,
                // dangling base FormIDs in NAME subrecords, and unresolved SCRI targets.
                // These are runtime-load failures, so --validate rejects rather than merely
                // annotating an artifact that cannot behave correctly in game.
                var semantic = PluginSemanticValidator.Validate(
                    outputBytes, _masterFormIds, _masterFormIdsByType, masterChildFormIds,
                    masterIndex.ScriptFormIdByEditorId);
                _sink.Info("Validating output", semantic.Report);
                if (semantic.ErrorCount > 0)
                {
                    _sink.Warn("Validating output",
                        $"Semantic validation surfaced {semantic.ErrorCount:N0} error(s); refusing the output.",
                        "ESM", 0, "semantic.errors");
                    throw new InvalidDataException(
                        $"Semantic validation failed with {semantic.ErrorCount:N0} error(s).\n{semantic.Report}");
                }

                validationReport = $"{roundTrip}\n\n{semantic.Report}";
                _sink.OnPhaseEnd("Validating output", stats);
            }

            // DIAG: reconcile allocated placed refs against what the writer actually emitted.
            // A ref the planner allocated a FormID for (so PACK PLDT/PTDT could resolve to it)
            // but never wrote is a phantom package destination → the engine warps the owning
            // actor ("teleport along a path"). Group phantoms by parent cell so the
            // unemitted-cell cause is visible. Sourced from the plan since retirement Stage G;
            // the legacy Phase-0 tracking dictionaries it used to read are gone.
            var phantomByCell = new Dictionary<uint, int>();

            // Local capture: the null check above is a field test, and the compiler discards
            // field null-state across the awaits between there and here.
            var emitPlan = _emitPlan
                           ?? throw new InvalidOperationException("Plan disappeared mid-build.");
            foreach (var (cellFormId, cellPlan) in emitPlan.CellsByFormId)
            {
                foreach (var bucket in new[]
                         {
                             cellPlan.PersistentChildren, cellPlan.VwdChildren, cellPlan.TemporaryChildren
                         })
                {
                    foreach (var child in bucket)
                    {
                        if (child.Type is not ("REFR" or "ACHR" or "ACRE" or "PGRE")
                            || child.Disposition != RecordDisposition.New
                            || actuallyEmittedPlacedRefs.Contains(child.FormId))
                        {
                            continue;
                        }

                        phantomByCell[cellFormId] = phantomByCell.GetValueOrDefault(cellFormId) + 1;
                    }
                }
            }

            if (phantomByCell.Count > 0)
            {
                var totalPhantom = phantomByCell.Values.Sum();
                _sink.Warn("Reconciling placed refs",
                    $"{totalPhantom:N0} allocated placed ref(s) across {phantomByCell.Count:N0} " +
                    "cell(s) were NOT emitted — packages referencing them dangle (actor warps to a " +
                    "missing destination).",
                    "ESM", 0, "phantom.preallocated-unemitted");
                foreach (var (cellFid, count) in phantomByCell.OrderByDescending(kv => kv.Value).Take(40))
                {
                    var inMaster = _masterFormIds?.Contains(cellFid) == true;
                    _sink.Decision("Reconciling placed refs",
                        $"Cell 0x{cellFid:X8} (in master: {inMaster}): {count} allocated ref(s) unemitted.",
                        "CELL", cellFid, "phantom.cell");
                }
            }

            sw.Stop();
            stats.Elapsed = sw.Elapsed;
            _sink.OnComplete(stats);

            return new PluginBuildResult
            {
                Success = true,
                Stats = stats,
                OutputPath = inputs.OutputEsmPath,
                ValidationReport = validationReport,
                NewRecordSourceToAllocated = new Dictionary<uint, uint>(_newRecordSourceToAllocated),
                EmittedDialogueAudioBindings = dialogResult.AudioBindings
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
                ErrorMessage = "Canceled."
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            stats.Elapsed = sw.Elapsed;
            stats.Errors++;
            _sink.Error("PluginConversionPipeline", $"Conversion failed: {ex.Message}");
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
    ///     Pre-register source FormIDs whose same-type EditorID resolves to a master record.
    ///     This is required before top-level and cell-child emission so aliases are visible
    ///     to subrecord remapping regardless of record order.
    /// </summary>
    private void RegisterEditorIdMasterAliases(
        RecordCollection dmpRecords,
        NewVsOverrideClassifier classifier,
        PluginBuildOptions options)
    {
        foreach (var (recordType, models) in EnumerateModelsByType(dmpRecords))
        {
            foreach (var model in models)
            {
                var sourceFormId = ExtractFormId(model);
                if (sourceFormId == 0 || classifier.IsOverride(sourceFormId))
                {
                    continue;
                }

                if (!TryFindMasterByEditorId(recordType, model, out var masterFormId)
                    || masterFormId == sourceFormId)
                {
                    continue;
                }

                TrackNewRecordSourceAlias(recordType, sourceFormId, masterFormId);
                if (options.VerboseDecisions)
                {
                    _sink.Decision("Merging top-level records",
                        $"Aliased DMP {recordType} 0x{sourceFormId:X8} to master 0x{masterFormId:X8} " +
                        "by same-type EditorID match.",
                        recordType, sourceFormId, "editor-id.alias-master");
                }
            }
        }
    }

    private void ReportGapRecovery(AnalysisResult rawResult, ConversionPipelineStats stats)
    {
        var summary = rawResult.RecoverableGapSummary;
        var promotion = rawResult.RecoverableGapPromotion;
        if (summary == null && promotion == null)
        {
            return;
        }

        if (summary != null)
        {
            stats.RecoverableGapCandidates = summary.Candidates;
            _sink.Info("Reading DMP",
                $"Recoverable gaps: candidates={summary.Candidates:N0}, " +
                $"raw={summary.RawRecordCandidates:N0}, runtimeTESForms={summary.RuntimeTesFormCandidates:N0}, " +
                $"dialogue={summary.RuntimeDialogueCandidates:N0}, placedRefs={summary.RuntimePlacedReferenceCandidates:N0}.");
        }

        if (promotion != null)
        {
            stats.PromotedGapRawRecords = promotion.RawRecordsPromoted;
            stats.PromotedGapRuntimeDialogue = promotion.RuntimeDialogueEntriesPromoted;
            stats.PromotedGapPlacedRefs = promotion.RuntimePlacedReferenceEntriesPromoted;
            stats.SkippedGapCandidates = promotion.SkippedCandidates;
            _sink.Info("Reading DMP",
                $"Gap promotion: raw={promotion.RawRecordsPromoted:N0}, " +
                $"runtimeDialogue={promotion.RuntimeDialogueEntriesPromoted:N0}, " +
                $"placedRefs={promotion.RuntimePlacedReferenceEntriesPromoted:N0}, " +
                $"skipped/audit={promotion.SkippedCandidates:N0}.");
        }
    }

    /// <summary>
    ///     Validate GetQuestVariable's numeric local IDs against the SCPT table that will
    ///     actually load. Exact name/type matches remap to an existing retail local. When
    ///     no exact local exists, the sanitizer reserves an append-only ID without changing
    ///     any retail index. Records are suppressed only when the source identity is missing
    ///     or ambiguous; dropping just CTDA would make conditional content unconditional.
    /// </summary>
    private void SanitizeQuestVariableConditions(
        RecordCollection dmpRecords,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterRecordsByFormId,
        NewVsOverrideClassifier classifier,
        MasterDialogueIndex masterDialogueIndex,
        ConversionPipelineStats stats,
        IReadOnlySet<string> skipRecordTypes)
    {
        var formIdAliases = _newRecordSourceToAllocated.Count > 0
            ? _newRecordSourceToAllocated
            : null;
        IReadOnlySet<uint> masterAnchoredDerivedInfoSources = new HashSet<uint>();
        if (!skipRecordTypes.Contains("DIAL") && !skipRecordTypes.Contains("INFO"))
        {
            var preliminaryDialoguePlan = DialogueCombinePlanner.Build(
                dmpRecords.DialogTopics,
                dmpRecords.Dialogues,
                classifier,
                masterDialogueIndex,
                masterRecordsByFormId.Keys,
                remapTable: formIdAliases);
            masterAnchoredDerivedInfoSources = preliminaryDialoguePlan.NewInfos
                .Where(static info => info.IsRehomedCutDialogue)
                .Select(static info => info.AudioSourceInfoFormId.GetValueOrDefault())
                .Where(sourceInfoFormId => sourceInfoFormId != 0
                                           && classifier.IsOverride(sourceInfoFormId))
                .ToHashSet();
        }

        var result = QuestVariableConditionSanitizer.Apply(
            dmpRecords,
            masterRecordsByFormId,
            formIdAliases,
            !skipRecordTypes.Contains("DIAL"),
            !skipRecordTypes.Contains("PACK"),
            !skipRecordTypes.Contains("TERM"),
            masterAnchoredDerivedInfoSources);

        // A new appended local is only useful when an emitted script is proven to write it.
        // Analyze planner-owned SCPT/PACK/TERM bundles before mutating any bytecode, suppress
        // dependent INFO/PACK records to a fixed point, and retain exact retail-local remaps
        // without imposing this fresh-local producer requirement.
        var freshMappings = QuestVariableProducerGate.SelectFreshMappings(
            result.VariableRecoveryMappings,
            result.ScriptVariableAugmentations);
        var producerEvidence = QuestVariableBytecodeRemapper.FindEmissionEligibleProducerWrites(
            dmpRecords,
            freshMappings,
            masterRecordsByFormId,
            formIdAliases,
            skipRecordTypes).ToList();

        // Dialogue lives outside EmitPlan. Admit INFO producers only from the same
        // combined/deduplicated model that DialogGrupBuilder consumes; raw captured INFO
        // presence is not evidence. This is provisional until the writer returns its
        // actual-emission ledger below.
        DialogueCombinePlan? dialogueCombinePlan = null;
        List<DialogueRecord>? preGateDialogues = null;
        if (!skipRecordTypes.Contains("DIAL") && !skipRecordTypes.Contains("INFO"))
        {
            dialogueCombinePlan = DialogueCombinePlanner.Build(
                dmpRecords.DialogTopics,
                dmpRecords.Dialogues,
                classifier,
                masterDialogueIndex,
                masterRecordsByFormId.Keys,
                remapTable: formIdAliases);
            var dialogueCandidates = DialogueProducerLedger.FromCombinedOutput(
                dialogueCombinePlan,
                freshMappings,
                formIdAliases);
            producerEvidence.AddRange(dialogueCandidates.Evidence);
            // The gate removes suppressed consumer INFOs in place, and a writer can also be
            // a consumer — snapshot the candidate list for the writer-side diagnostic.
            preGateDialogues = [.. dmpRecords.Dialogues];
        }

        var producerGate = QuestVariableProducerGate.Apply(
            dmpRecords,
            result.ScriptVariableAugmentations,
            result.VariableRecoveryMappings,
            producerEvidence,
            masterRecordsByFormId,
            formIdAliases);
        if (dialogueCombinePlan is not null
            && preGateDialogues is not null
            && producerGate.SuppressedInfoCount + producerGate.SuppressedPackageCount
                                                + producerGate.SuppressedTerminalMenuItemCount > 0)
        {
            QuestVariableWriterDiagnostics.Report(
                preGateDialogues,
                dialogueCombinePlan,
                freshMappings,
                producerEvidence,
                QuestVariableProducerGate.SelectFreshMappings(
                    producerGate.VariableRecoveryMappings,
                    producerGate.ScriptVariableAugmentations),
                producerGate.Diagnostics,
                formIdAliases,
                classifier,
                masterDialogueIndex,
                _sink);
        }

        _scriptVariableAugmentations = producerGate.ScriptVariableAugmentations;
        _scriptVariableProducerRequirements = producerGate.ProducerRequirements;
        _scriptVariableProducerMappings = QuestVariableProducerGate.SelectFreshMappings(
            producerGate.VariableRecoveryMappings,
            producerGate.ScriptVariableAugmentations);

        // A CTDA remap is only sound when every newly-emitted producer/consumer SCDA uses
        // that same exact quest-local identity. Patch the structurally decoded UInt16
        // operands before planning; retained master SCPTs and shared master INFO overlays
        // are excluded by the remapper. Any incomplete/unknown relevant bytecode aborts the
        // build instead of shipping a condition that reads a different state channel.
        var bytecodeResult = QuestVariableBytecodeRemapper.Apply(
            dmpRecords,
            producerGate.VariableRecoveryMappings,
            masterRecordsByFormId,
            formIdAliases,
            skipRecordTypes);

        for (var i = 0; i < result.SuppressedInfoCount; i++)
        {
            stats.IncrementSkipped("INFO");
        }

        for (var i = 0; i < result.SuppressedPrototypeDerivedInfoCount; i++)
        {
            stats.IncrementSkipped("INFO");
        }

        for (var i = 0; i < result.SuppressedPackageCount; i++)
        {
            stats.IncrementSkipped("PACK");
        }

        for (var i = 0; i < producerGate.SuppressedInfoCount; i++)
        {
            stats.IncrementSkipped("INFO");
        }

        for (var i = 0; i < producerGate.SuppressedPackageCount; i++)
        {
            stats.IncrementSkipped("PACK");
        }

        foreach (var diagnostic in result.Diagnostics)
        {
            var variable = string.IsNullOrWhiteSpace(diagnostic.VariableName)
                ? $"ID {diagnostic.VariableIndex}"
                : $"{diagnostic.VariableName} (ID {diagnostic.VariableIndex})";
            var targetScript = diagnostic.TargetScriptFormId.HasValue
                ? $"; target SCPT=0x{diagnostic.TargetScriptFormId.Value:X8}"
                : string.Empty;
            var detail = $"{diagnostic.Message} Target=0x{diagnostic.TargetFormId:X8}; " +
                         $"variable={variable}{targetScript}.";
            var reportedCode = diagnostic.Code;
            if (diagnostic.RecordSuppressed)
            {
                var isScriptVariable =
                    diagnostic.Code.StartsWith("script-variable.", StringComparison.Ordinal);
                if (diagnostic.RecordType == "TERM")
                {
                    reportedCode = isScriptVariable
                        ? "script-variable.menu-item-suppressed"
                        : "quest-variable.menu-item-suppressed";
                }
                else if (isScriptVariable)
                {
                    reportedCode = "script-variable.record-suppressed";
                }
            }

            var metadata = diagnostic.Metadata;
            if (!string.Equals(reportedCode, diagnostic.Code, StringComparison.Ordinal))
            {
                metadata = new Dictionary<string, string?>(diagnostic.Metadata, StringComparer.Ordinal)
                {
                    ["suppression-reason-code"] = diagnostic.Code
                };
            }

            if (diagnostic.Code is "quest-variable.remapped" or "quest-variable.augmented-and-remapped")
            {
                _sink.Decision(
                    "Sanitizing script-variable conditions",
                    detail,
                    diagnostic.RecordType,
                    diagnostic.RecordFormId,
                    reportedCode,
                    metadata);
            }
            else if (diagnostic.Code != "quest-variable.target-unresolved")
            {
                stats.Warnings++;
                _sink.Warn(
                    "Sanitizing script-variable conditions",
                    detail,
                    diagnostic.RecordType,
                    diagnostic.RecordFormId,
                    reportedCode,
                    metadata);
            }
        }

        foreach (var diagnostic in bytecodeResult.Diagnostics)
        {
            _sink.Decision(
                "Sanitizing script-variable bytecode",
                diagnostic.Message,
                diagnostic.RecordType,
                diagnostic.RecordFormId,
                "quest-variable.bytecode-remapped",
                new Dictionary<string, string?>
                {
                    ["script-path"] = diagnostic.ScriptPath,
                    ["read-operands-patched"] = diagnostic.ReadOperandsPatched.ToString(
                        CultureInfo.InvariantCulture),
                    ["write-operands-patched"] = diagnostic.WriteOperandsPatched.ToString(
                        CultureInfo.InvariantCulture),
                    ["source-text-omitted"] = diagnostic.SourceTextOmitted.ToString()
                });
        }

        foreach (var diagnostic in producerGate.Diagnostics)
        {
            stats.Warnings++;
            _sink.Warn(
                "Sanitizing script-variable producers",
                diagnostic.Message,
                diagnostic.RecordType,
                diagnostic.RecordFormId,
                diagnostic.Code,
                diagnostic.Metadata);
        }

        if (result.UnresolvedTargetCount > 0)
        {
            _sink.Info(
                "Sanitizing script-variable conditions",
                $"Retained {result.UnresolvedTargetCount:N0} GetQuestVariable condition(s) because " +
                "their effective emitted quest/script binding could not be proven; no condition was widened.",
                code: "quest-variable.target-unresolved-summary");
        }

        if (result.SuppressedInfoCount + result.SuppressedPackageCount +
            result.SuppressedTerminalMenuItemCount +
            result.SuppressedPrototypeDerivedInfoCount +
            producerGate.SuppressedInfoCount + producerGate.SuppressedPackageCount +
            producerGate.SuppressedTerminalMenuItemCount +
            result.RemappedConditionCount + result.RetainedGetScriptVariableCount > 0)
        {
            _sink.Info(
                "Sanitizing script-variable conditions",
                $"Suppressed {result.SuppressedInfoCount + producerGate.SuppressedInfoCount:N0} INFO and " +
                $"{result.SuppressedPackageCount + producerGate.SuppressedPackageCount:N0} PACK record(s), plus " +
                $"{result.SuppressedTerminalMenuItemCount + producerGate.SuppressedTerminalMenuItemCount:N0} " +
                $"TERM menu item(s) and {result.SuppressedPrototypeDerivedInfoCount:N0} prototype-derived " +
                "shared-INFO branch(es) while retaining their retail overlays; remapped " +
                $"{result.RemappedConditionCount:N0} GetQuestVariable condition(s), with " +
                $"{producerGate.ScriptVariableAugmentations.Length:N0} producer-backed append-only SCPT local(s); retained and " +
                $"diagnosed {result.RetainedGetScriptVariableCount:N0} GetScriptVariable INFO/PACK/TERM condition(s).",
                code: "script-variable.sanitization-summary");
        }

        if (bytecodeResult.ScriptsPatched > 0)
        {
            _sink.Info(
                "Sanitizing script-variable bytecode",
                $"Patched {bytecodeResult.ScriptsPatched:N0} emission-eligible DMP script bundle(s): " +
                $"{bytecodeResult.WriteOperandsPatched:N0} write and " +
                $"{bytecodeResult.ReadOperandsPatched:N0} read operand(s) now use the exact " +
                "quest-local IDs selected for emitted conditions.",
                code: "quest-variable.bytecode-remap-summary");
        }
    }

    internal static void EnsureScriptVariableAugmentationsCanBeEmitted(
        IReadOnlyList<ScriptVariableAugmentation> augmentations,
        IReadOnlySet<string> skipRecordTypes)
    {
        ArgumentNullException.ThrowIfNull(augmentations);
        ArgumentNullException.ThrowIfNull(skipRecordTypes);

        if (augmentations.Count > 0 && skipRecordTypes.Contains("SCPT"))
        {
            throw new InvalidOperationException(
                "Recovered INFO/PACK conditions require append-only SCPT variables, but SCPT " +
                "emission was disabled by --skip-record-type SCPT.");
        }
    }

    /// <summary>
    ///     Pre-encode every new (non-master) worldspace that has at least one captured child
    ///     cell. The cell-children pipeline emits these alongside their World Children GRUP
    ///     so the WRLD record sits directly above its cells (canonical ESM layout). New WRLDs
    ///     with no child cells stay in the standard top-level emit path.
    /// </summary>
    /// <summary>
    ///     Attach orphan scripts to NPCs / quests / creatures by EditorId convention. Looks for
    ///     <c>&lt;FormEditorId&gt;Script</c> as the script's EDID and binds it. Runs after
    ///     all DMP records are parsed, before encoding. Bethesda's naming pattern
    ///     (CassFollowerScript, BooneFollowerScript, UlyssesScript, etc.) makes this reliable
    ///     for the common case where the proto's runtime hadn't yet linked the script pointer
    ///     on TESForm.pFormScript.
    /// </summary>
    private void AttachOrphanScriptsByEditorId(RecordCollection dmpRecords)
    {
        // Build EditorId → SCPT FormID lookup, partitioned by script type. NPCs/creatures
        // need Object-type scripts (IsQuestScript=false), quests need Quest-type scripts
        // (IsQuestScript=true). Attaching the wrong type causes the engine to log
        // "Unable to find script (X) on owner object (Y)" and silently null the binding
        // — confirmed via UlyssesScript (Object, attaches to NPC) being incorrectly bound
        // to VDialogueUlysses QUEST by the prefix-strip path.
        var objectScriptsByEditorId = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        var questScriptsByEditorId = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        var validScriptFormIds = new HashSet<uint>();
        foreach (var script in dmpRecords.Scripts)
        {
            if (script.FormId != 0)
            {
                validScriptFormIds.Add(script.FormId);
            }

            if (string.IsNullOrEmpty(script.EditorId) || script.FormId == 0)
            {
                continue;
            }

            // Magic effect scripts aren't attachable via SCRI on NPC/CREA/QUST — skip them.
            if (script.IsMagicEffectScript)
            {
                continue;
            }

            if (script.IsQuestScript)
            {
                questScriptsByEditorId.TryAdd(script.EditorId, script.FormId);
            }
            else
            {
                objectScriptsByEditorId.TryAdd(script.EditorId, script.FormId);
            }
        }

        if (objectScriptsByEditorId.Count == 0 && questScriptsByEditorId.Count == 0)
        {
            return;
        }

        // Treat an NPC/quest/creature as "needing a script attachment" when:
        //   (a) Script field is null/zero, OR
        //   (b) Script field points at a FormID we DON'T have a SCPT record for. (b) is the
        //       Ulysses case: his TESNPC.pFormScript captured a proto FormID 0x00133FD7 that
        //       was never instanced as a SCPT in our records — the encoder would log
        //       "SCRI dangles" and skip the subrecord, leaving the NPC scriptless.
        bool NeedsHeuristic(uint? script)
        {
            return !script.HasValue || script.Value == 0 || !validScriptFormIds.Contains(script.Value);
        }

        var attachedNpcs = 0;
        var attachedQuests = 0;
        var attachedCreatures = 0;

        // NPCs: try EditorId + "Script", then EditorId itself (some scripts share the NPC name).
        // Only consider Object-type scripts — quest-type would be rejected by the engine.
        for (var i = 0; i < dmpRecords.Npcs.Count; i++)
        {
            var npc = dmpRecords.Npcs[i];
            if (!NeedsHeuristic(npc.Script)) continue;
            if (string.IsNullOrEmpty(npc.EditorId)) continue;

            if (objectScriptsByEditorId.TryGetValue(npc.EditorId + "Script", out var sid)
                || objectScriptsByEditorId.TryGetValue(npc.EditorId + "SCRIPT", out sid))
            {
                dmpRecords.Npcs[i] = npc with { Script = sid };
                attachedNpcs++;
            }
        }

        // Quests: try EditorId + "Script". Only consider Quest-type scripts — Object-type
        // would be rejected. The previous prefix-strip path (VDialogueUlysses → UlyssesScript)
        // is removed: it falsely matched the NPC's Object script for a quest, which is the
        // type the engine rejects. Quest scripts in vanilla follow EditorId + "Script" with
        // no stripping, so the direct lookup is sufficient.
        for (var i = 0; i < dmpRecords.Quests.Count; i++)
        {
            var quest = dmpRecords.Quests[i];
            if (!NeedsHeuristic(quest.Script)) continue;
            if (string.IsNullOrEmpty(quest.EditorId)) continue;

            if (questScriptsByEditorId.TryGetValue(quest.EditorId + "Script", out var sid)
                || questScriptsByEditorId.TryGetValue(quest.EditorId + "SCRIPT", out sid))
            {
                dmpRecords.Quests[i] = quest with { Script = sid };
                attachedQuests++;
            }
        }

        // Creatures: same pattern as NPCs — Object-type only.
        for (var i = 0; i < dmpRecords.Creatures.Count; i++)
        {
            var creature = dmpRecords.Creatures[i];
            if (!NeedsHeuristic(creature.Script)) continue;
            if (string.IsNullOrEmpty(creature.EditorId)) continue;

            if (objectScriptsByEditorId.TryGetValue(creature.EditorId + "Script", out var sid)
                || objectScriptsByEditorId.TryGetValue(creature.EditorId + "SCRIPT", out sid))
            {
                dmpRecords.Creatures[i] = creature with { Script = sid };
                attachedCreatures++;
            }
        }

        if (attachedNpcs + attachedQuests + attachedCreatures > 0)
        {
            _sink.Info("Attaching orphan scripts by EditorId",
                $"Bound {attachedNpcs:N0} NPC, {attachedQuests:N0} quest, {attachedCreatures:N0} creature " +
                "scripts via EditorId match. The proto's runtime hadn't populated TESForm.pFormScript " +
                "for these; the EditorId convention (e.g. UlyssesScript → Ulysses NPC) recovers the link.",
                code: "scri.orphan.attached");
        }
    }

    /// <summary>
    ///     For each NPC whose VTCK FormID doesn't resolve to a known VTYP in either the master
    ///     ESM or our own records, look up a VTYP whose EditorId follows the vanilla naming
    ///     convention (Male/FemaleUnique&lt;NpcEditorId&gt;) and rebind the VTCK to it. Without
    ///     this, dangling VTCKs cause the engine to fall back to MaleAdult01Default, and any
    ///     voice files packed under the unique voicetype directory never load.
    /// </summary>
    private void AttachOrphanVoiceTypesByEditorId(RecordCollection dmpRecords)
    {
        // Build EditorId → VTYP FormID lookup from our captured records. Master VTYPs are
        // implicitly fine — if the NPC's VTCK already points at a master FormID, the master
        // ESM provides the record and we don't need to rebind.
        var vtypsByEditorId = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        var validVtypFormIds = _masterFormIds is not null
            ? new HashSet<uint>(_masterFormIds)
            : [];
        foreach (var vtyp in dmpRecords.VoiceTypes)
        {
            if (vtyp.FormId != 0)
            {
                validVtypFormIds.Add(vtyp.FormId);
            }

            if (!string.IsNullOrEmpty(vtyp.EditorId) && vtyp.FormId != 0)
            {
                vtypsByEditorId.TryAdd(vtyp.EditorId, vtyp.FormId);
            }
        }

        if (vtypsByEditorId.Count == 0)
        {
            return;
        }

        // An NPC "needs heuristic" when its VTCK is set but doesn't resolve to a VTYP
        // specifically — checking validVtypFormIds (which mirrors all master FormIDs, not
        // just VTYP ones) would let a dangling 0x... slip through unchanged just because
        // SOME other record type uses that FormID. Filter by VTYP record-type explicitly.
        var masterVtypSet = _masterFormIdsByType is not null
                            && _masterFormIdsByType.TryGetValue("VTYP", out var masterVtyps)
            ? masterVtyps
            : new HashSet<uint>();
        var dmpVtypSet = new HashSet<uint>();
        foreach (var vtyp in dmpRecords.VoiceTypes)
        {
            if (vtyp.FormId != 0)
            {
                dmpVtypSet.Add(vtyp.FormId);
            }
        }

        bool NeedsHeuristic(uint? vtck)
        {
            return vtck.HasValue && vtck.Value != 0
                                 && !masterVtypSet.Contains(vtck.Value)
                                 && !dmpVtypSet.Contains(vtck.Value);
        }

        var attached = 0;
        var needyCount = 0;
        var sampleMisses = new List<string>();
        var sampleAttaches = new List<string>();
        string[] prefixes = ["MaleUnique", "FemaleUnique"];
        for (var i = 0; i < dmpRecords.Npcs.Count; i++)
        {
            var npc = dmpRecords.Npcs[i];
            if (!NeedsHeuristic(npc.VoiceType)) continue;
            if (string.IsNullOrEmpty(npc.EditorId)) continue;

            needyCount++;

            var matched = false;
            foreach (var prefix in prefixes)
            {
                if (vtypsByEditorId.TryGetValue(prefix + npc.EditorId, out var newVtck))
                {
                    dmpRecords.Npcs[i] = npc with { VoiceType = newVtck };
                    attached++;
                    matched = true;
                    if (sampleAttaches.Count < 5)
                    {
                        sampleAttaches.Add(
                            $"{npc.EditorId} 0x{npc.VoiceType!.Value:X8}→0x{newVtck:X8}");
                    }

                    break;
                }
            }

            if (!matched && sampleMisses.Count < 5)
            {
                sampleMisses.Add($"{npc.EditorId} (VTCK=0x{npc.VoiceType!.Value:X8})");
            }
        }

        if (attached > 0)
        {
            _sink.Info("Attaching orphan voice types by EditorId",
                $"Rebound {attached:N0} of {needyCount:N0} NPC VTCK reference(s) to matching " +
                "unique VTYP records via EditorId convention (e.g. Ulysses → MaleUniqueUlysses). " +
                "The original VTCKs pointed at FormIDs with no record in vanilla or our build.",
                code: "vtck.orphan.attached");
        }
    }

    /// <summary>
    ///     Build the EmitPlan once, up front. Every emission decision is settled here before
    ///     any byte is encoded; <see cref="BuildGrupForType" /> then serializes the plan
    ///     per-type. (Named "…IfEnabled" while the planner was opt-in; it is now the only
    ///     path — the legacy pipeline was retired 2026-08-11.)
    /// </summary>
    private void BuildPlannerStateIfEnabled(
        IReadOnlyList<ParsedMainRecord> pcRecords,
        RecordCollection dmpRecords,
        FormIdAllocator allocator,
        DmpToEsmInputs inputs,
        IReadOnlyDictionary<uint, PcEsmCellContext> masterCellContexts,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterRecordsByFormId,
        MasterRecordIndex masterIndex,
        ConversionPipelineStats stats)
    {
        _emitPlan = null;
        _planWriter = null;

        var enabled = PlannedEncoders.KnownRecordTypes().ToHashSet(StringComparer.Ordinal);

        var registry = PlannedEncoders.BuildRegistry();
        var dispositionEngine = new DispositionEngine(
            new IDispositionPolicy[]
            {
                new DiagnosticKeepMasterDispositionPolicy(
                    inputs.Options.DiagnosticKeepMasterFormIds),
                new ScriptDispositionPolicy(),
                new ImageSpaceModifierDispositionPolicy(),
                new RuntimeStatePolicy(),
                new DefaultDispositionPolicy()
            });
        var degradationPolicy = new DegradationPolicy();
        degradationPolicy.SetDefaultForType(
            "SCPT",
            DanglingAction.DropSubrecord);
        degradationPolicy.SetDefaultForType(
            "IMAD",
            DanglingAction.DropSubrecord);
        degradationPolicy.SetDefaultForType(
            "INFO",
            DanglingAction.DropSubrecord);
        degradationPolicy.SetDefaultForType(
            "PACK",
            DanglingAction.DropSubrecord);
        degradationPolicy.SetDefaultForType(
            "TERM",
            DanglingAction.DropSubrecord);
        degradationPolicy.SetDefaultForType(
            "NPC_",
            DanglingAction.DropSubrecord);
        degradationPolicy.SetDefaultForType(
            "CREA",
            DanglingAction.DropSubrecord);
        degradationPolicy.SetDefaultForType(
            "PERK",
            DanglingAction.DropSubrecord);
        degradationPolicy.SetRule(
            "ALCH",
            FieldPath.Member(
                "ENIT", "WithdrawalEffect"),
            DanglingAction.NullRef);
        degradationPolicy.SetRule(
            "ALCH",
            FieldPath.Member(
                "ENIT", "ConsumeSound"),
            DanglingAction.NullRef);
        var referenceResolver = new ReferenceResolver(
            PlannerReferenceWalkers.BuildAll(),
            degradationPolicy);

        var esmPlanner = new EsmPlanner(
            dispositionEngine, allocator, referenceResolver);
        var plannerMasterAliases = _masterFormIds is null
            ? new Dictionary<uint, uint>()
            : _newRecordSourceToAllocated
                .Where(pair => _masterFormIds.Contains(pair.Value))
                .ToDictionary(static pair => pair.Key, static pair => pair.Value);

        // Before planning this table may contain only validated source->master aliases.
        // Publishing an independently allocated plugin ID here would bypass planner
        // eligibility and recreate phantom reference liveness.
        var unexpectedPrePlannerAllocations = _newRecordSourceToAllocated
            .Where(pair => _masterFormIds is null || !_masterFormIds.Contains(pair.Value))
            .ToArray();
        if (unexpectedPrePlannerAllocations.Length > 0)
        {
            var first = unexpectedPrePlannerAllocations[0];
            throw new InvalidOperationException(
                $"Pre-planner source alias 0x{first.Key:X8}->0x{first.Value:X8} is not master-anchored; "
                + "plugin allocations must be owned by EsmPlanner or recorded as non-live reservations.");
        }

        _emitPlan = esmPlanner.Build(
            pcRecords,
            dmpRecords,
            enabled,
            _masterFormIds ?? new HashSet<uint>(),
            inputs.PcEsmPath,
            masterCellContexts,
            masterRecordsByFormId,
            allocator,
            inputs.Options.EmitMasterCellNavmAugmentation,
            new HashSet<uint>(masterIndex.RefToCell.Keys),
            inputs.Options.ReplaceCellTemporariesOnOverride,
            new CellVerdictInputs
            {
                MasterIndex = masterIndex,
                DmpBaseTypes = _dmpBaseFormIdToRecordType,
                RecoverLeveledSpawnActors = inputs.Options.RecoverLeveledSpawnActors,
                EnableRefrBaseEditorIdRemap = inputs.Options.EnableRefrBaseEditorIdRemap,
                DiagnosticSkipCellNewRefs = inputs.Options.DiagnosticSkipCellNewRefs,
                DiagnosticSkipCellNavm = inputs.Options.DiagnosticSkipCellNavm
            },
            inputs.Options.DiagnosticKeepMasterFormIds,
            inputs.Options.DiagnosticRetainMasterSubrecords,
            plannerMasterAliases);

        if (_scriptVariableAugmentations.Count > 0 && enabled.Contains("SCPT"))
        {
            _emitPlan = ScriptVariableAugmentationPlanner.Apply(
                _emitPlan,
                _scriptVariableAugmentations);
        }

        QuestVariableProducerGate.VerifyPlannerState(
            _emitPlan,
            _scriptVariableProducerRequirements);

        ReportPlannerDiagnostics(_emitPlan, _sink, stats);

        _planWriter = new PlanWriter(registry, _sink);

        _sink.Info("Two-pass planner",
            $"Built EmitPlan covering {enabled.Count} record type(s): " +
            $"{_emitPlan.Records.Length:N0} planned record(s), " +
            $"{_emitPlan.CellsByFormId.Count:N0} cell plan(s), " +
            $"{_emitPlan.SourceToEmittedFormId.Count:N0} FormID allocation(s).",
            code: "planner.built");
    }

    internal static void ReportPlannerDiagnostics(
        EmitPlan plan,
        IConversionProgressSink sink,
        ConversionPipelineStats? stats = null)
    {
        foreach (var diagnostic in plan.Diagnostics)
        {
            switch (diagnostic.Kind)
            {
                case PlanDiagnosticKind.Warning:
                    sink.Warn(diagnostic.Phase, diagnostic.Message,
                        diagnostic.RecordType, diagnostic.FormId, diagnostic.Code,
                        diagnostic.Metadata);
                    // Aggregate plan-phase drops/decisions into DropReasonCounts so they appear in the
                    // run summary; without this, planner-routed drops were only per-event sink lines.
                    stats?.IncrementDropReason(diagnostic.Code);
                    break;
                case PlanDiagnosticKind.Decision:
                    sink.Decision(diagnostic.Phase, diagnostic.Message,
                        diagnostic.RecordType, diagnostic.FormId, diagnostic.Code,
                        diagnostic.Metadata);
                    stats?.IncrementDropReason(diagnostic.Code);
                    break;
                default:
                    sink.Info(diagnostic.Phase, diagnostic.Message,
                        diagnostic.RecordType, diagnostic.FormId, diagnostic.Code,
                        diagnostic.Metadata);
                    break;
            }
        }
    }

    /// <summary>
    ///     Produce one top-level GRUP by dispatching to the planner's writer. The legacy
    ///     per-model encode branch that used to live here was deleted in the 2026-08-11
    ///     retirement (Stage E) — every type <see cref="EnumerateModelsByType" /> yields has
    ///     a planned encoder, so the branch had been unreachable since planner-all became
    ///     the production path. "CELL" never reaches here: the cell hierarchy is structurally
    ///     atomic and runs through <see cref="EsmAssembler" />'s planner cell section.
    /// </summary>
    private byte[] BuildGrupForType(
        string recordType,
        PluginBuildOptions options)
    {
        if (_planWriter is null || _emitPlan is null)
        {
            throw new InvalidOperationException(
                "Planner state was not constructed. BuildPlannerStateIfEnabled was not called " +
                "or failed silently.");
        }

        if (!_planWriter.Handles(recordType))
        {
            throw new InvalidOperationException(
                $"'{recordType}' has no IPlannedRecordEncoder. Add it to PlannedEncoders.BuildAll().");
        }

        return _planWriter.BuildGrupForType(recordType, _emitPlan, options);
    }

    private static void AppendOrCreateTopLevelRecord(
        Dictionary<string, byte[]> grupBytesByType,
        string recordType,
        byte[] recordBytes)
    {
        if (!grupBytesByType.TryGetValue(recordType, out var existingGrup) || existingGrup.Length <= 24)
        {
            grupBytesByType[recordType] = TopLevelRecordEmitter.WrapInTopLevelGrup(recordType, recordBytes);
            return;
        }

        var oldBody = existingGrup.AsSpan(24).ToArray();
        var combined = new byte[oldBody.Length + recordBytes.Length];
        oldBody.CopyTo(combined, 0);
        recordBytes.CopyTo(combined, oldBody.Length);
        grupBytesByType[recordType] = TopLevelRecordEmitter.WrapInTopLevelGrup(recordType, combined);
    }

    private void TrackEmittedNewFormId(string recordType, uint formId)
    {
        if (formId == 0)
        {
            return;
        }

        _emittedNewFormIds.Add(formId);
        if (!_emittedNewFormIdsByType.TryGetValue(recordType, out var typeSet))
        {
            typeSet = [];
            _emittedNewFormIdsByType[recordType] = typeSet;
        }

        typeSet.Add(formId);
    }

    private void TrackNewRecordSourceAlias(string recordType, uint sourceFormId, uint targetFormId)
    {
        if (sourceFormId == 0 || sourceFormId == targetFormId)
        {
            return;
        }

        _newRecordSourceToAllocated[sourceFormId] = targetFormId;
        _newRecordSourceToAllocatedType[sourceFormId] = recordType;
    }

    /// <summary>
    ///     Runs the asset-path rename pass when the user has configured secondary data
    ///     folders + a baseline folder. Mutates record string fields in-place when fuzzy
    ///     resolution matches a differently-named asset.
    /// </summary>
    private void TryApplyAssetRenames(
        RecordCollection dmpRecords,
        PluginBuildOptions options,
        CancellationToken ct)
    {
        var renameService = new AssetRenameService(_sink);
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
        if (recordType == "SCPT" && model is ScriptRecord script)
        {
            var scriptEditorId = ScriptRecordEmissionPolicy.ResolveEditorId(script);
            return !string.IsNullOrWhiteSpace(scriptEditorId)
                   && _masterScriptFormIdByEditorId is not null
                   && _masterScriptFormIdByEditorId.TryGetValue(scriptEditorId, out masterFormId);
        }

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
    ///     Phase C: locate a single master record of <paramref name="expectedBaseType" />
    ///     whose normalized EditorID stem matches <paramref name="prototypeBaseEditorId" />.
    ///     Returns the master FormID on a unique hit; null on no hit, empty stem, or
    ///     mismatched type; null + <paramref name="ambiguous" />=true on multiple-candidate
    ///     hits so the caller can log a refusal decision. Extracted as a static helper
    ///     for unit testability.
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

    /// <summary>
    ///     Returns true when an SCRI subrecord's target FormID would resolve at runtime:
    ///     the sentinel null FormIDs (0 / 0xFFFFFFFF), anything in the master ESM, and
    ///     anything being freshly emitted via the new-record path in the current Build.
    ///     The new-emit case lets reintroduced prototype NPCs bind to their reintroduced
    ///     scripts. Extracted as a static helper for unit testability.
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
    ///     Read the race FormID from an NPC_ record's RNAM subrecord (4 bytes little-endian).
    ///     Returns false when RNAM is missing — those NPCs aren't viable template fallbacks.
    /// </summary>
    private static bool TryReadNpcRaceFormId(ParsedMainRecord npcRecord, out uint raceFormId)
    {
        var rnam = npcRecord.Subrecords.FirstOrDefault(s => s.Signature == "RNAM" && s.Data.Length >= 4);
        if (rnam is null)
        {
            raceFormId = 0;
            return false;
        }

        raceFormId = BinaryPrimitives.ReadUInt32LittleEndian(rnam.Data.AsSpan(0, 4));
        return true;
    }

    /// <summary>
    ///     Read the (gridX, gridY) tile coords from an exterior CELL record's XCLC
    ///     subrecord. XCLC is at least 8 bytes little-endian: int32 X, int32 Y, optional
    ///     uint32 land flags. Returns false when XCLC is missing or undersized — interior
    ///     cells never have XCLC.
    /// </summary>
    private static bool TryReadCellGridCoords(ParsedMainRecord cellRecord, out int gridX, out int gridY)
    {
        var xclc = cellRecord.Subrecords.FirstOrDefault(s => s.Signature == "XCLC" && s.Data.Length >= 8);
        if (xclc is null)
        {
            gridX = gridY = 0;
            return false;
        }

        gridX = BinaryPrimitives.ReadInt32LittleEndian(xclc.Data.AsSpan(0, 4));
        gridY = BinaryPrimitives.ReadInt32LittleEndian(xclc.Data.AsSpan(4, 4));
        return true;
    }

    /// <summary>
    ///     Walks the digested record collection and yields per-type model lists for the
    ///     simple-type set. Cell children (REFR/ACHR/ACRE/PGRE) are NOT yielded here — they
    ///     emit inside the cell hierarchy via the planner's
    ///     <see cref="PlanCellSectionBuilder" />.
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
        // MESG must precede script-bearing records. Object scripts can reference message
        // records by SCRO slot (UlyssesScript -> FollowerMessageDeadUlysses); emitting MESG
        // later means the script encoder has no allocated target yet and preserves the slot
        // count only by writing a null SCRO.
        yield return ("MESG", records.Messages);
        // IMAD must precede scripts: new SCPT SCRO tables may invoke a captured-new
        // imagespace modifier, and legacy routing validates those refs against records
        // already emitted in this pass.
        yield return ("IMAD", records.ImageSpaceModifiers);
        // PACK must precede NPC_ so the NPC encoder can validate its PKID list against the
        // set of (master ∪ just-emitted) PACK FormIDs and drop dangling refs. A dangling
        // PKID leaves the NPC without its packages → it falls through to default behavior →
        // the crucified idle.
        yield return ("PACK", records.Packages);
        // SCPT must precede NPC_ (and QUST) for the same reason: the NPC encoder validates
        // its SCRI against (master ∪ emitted) script FormIDs. Emitting SCPT after NPC_
        // means every new NPC's SCRI to a newly-emitted script dangles, gets dropped, and
        // the engine attaches no script — breaking dialogue control / follower behavior.
        // Observed: UlyssesScript emitted at 0x01000A0F but Ulysses NPC's SCRI was nulled
        // because SCPT hadn't been emitted yet when NPC_ encoder validated it.
        yield return ("SCPT", records.Scripts);
        // VTYP must precede NPC_/CREA so the post-emit FormID remapper can rewrite a new NPC's
        // VTCK from its proto source FormID to the allocator-issued one. Without this, Ulysses'
        // VTCK kept the proto FormID 0x0014F3EB instead of the allocated 0x01002FA5, the
        // engine couldn't resolve the voice type, and audio fell back to MaleAdult01Default.
        yield return ("VTYP", records.VoiceTypes);
        yield return ("NPC_", records.Npcs);
        // DIAL and INFO are handled separately by DialogGrupBuilder so INFOs get nested
        // as type-7 Topic Children GRUPs under each DIAL. Emitting them as two flat
        // top-level GRUPs crashes the FNV runtime on dialog tree walks.
        yield return ("QUST", records.Quests);
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
        // Keep forensic COBJ captures visible to the Phase-3 no-encoder diagnostic. COBJ
        // is intentionally absent from PlannedEncoders because the retained hybrid writer
        // is incompatible with the FNV xEdit schema.
        yield return ("COBJ", records.ConstructibleObjects);
        yield return ("EYES", records.Eyes);
        yield return ("HAIR", records.Hair);
        yield return ("REPU", records.Reputations);
        yield return ("AVIF", records.ActorValueInfos);
        yield return ("MUSC", records.MusicTypes);
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
        // Remaining types with registered encoders — dispatched here so DMP-captured
        // overrides for these record types reach the merge engine.
        yield return ("CSTY", records.CombatStyles);
        yield return ("LGTM", records.LightingTemplates);
        yield return ("WATR", records.Water);
        // PWAT after WATR so its DNAM parent-water FormID resolves against just-emitted new
        // WATR records. PWAT had a registered encoder and a typed model but no yield and no
        // producer, so it was structurally unemittable — refs on a proto-only placeable water
        // dropped as refr.dangling-base.
        yield return ("PWAT", records.PlaceableWaters);
        yield return ("WTHR", records.Weather);
        // CLMT after WTHR so its WLST weather links resolve against just-emitted new WTHR
        // FormIDs; GRAS next to LTEX because LTEX GNAM points at GRAS. Both had encoders that
        // nothing ever fed — this yield is what makes them reachable.
        yield return ("CLMT", records.Climate);
        // TREE must be yielded before REGN below: RDOT "Object" entries target TREE/STAT/LTEX
        // and this enumeration governs FormID allocation + remap order for those references.
        yield return ("TREE", records.Trees);
        yield return ("GRAS", records.Grasses);
        yield return ("IMGS", records.ImageSpaces);
        // Close encoder coverage for every type with a runtime reader.
        yield return ("ECZN", records.EncounterZones);
        yield return ("MICN", records.MenuIcons);
        // VTYP yielded near the top (before NPC_/CREA) — see comment there.
        yield return ("CCRD", records.CaravanCards);
        yield return ("CMNY", records.CaravanMoney);
        yield return ("CDCK", records.CaravanDecks);
        yield return ("INGR", records.Ingredients);
        // FLOR has no typed model — pull flora out of the shared GenericRecords list by signature.
        // PFIG (ingredient), SCRI (script), and SNAM (sound) are remapped from the plan's
        // already-complete allocation table; enumeration order no longer allocates targets.
        // Runtime-only INGR is intentionally not planned until its required ENIT/effects are modeled.
        yield return ("FLOR", records.GenericRecords.Where(g => g.RecordType == "FLOR"));
        // MSTT/ANIO share FLOR's shape: no typed model, decoded by RuntimeGenericReader into the
        // shared GenericRecords list. Yielded after SOUN and IDLE respectively so MSTT's SNAM
        // (sound) and ANIO's DATA (idle animation) references remap against already-allocated new
        // FormIDs. Before these yields existed the records were dropped here with no diagnostic —
        // the encoder-missing warning at the top of the merge loop can only fire for a type that
        // is yielded at all.
        yield return ("MSTT", records.GenericRecords.Where(g => g.RecordType == "MSTT"));
        yield return ("ANIO", records.GenericRecords.Where(g => g.RecordType == "ANIO"));
        // TACT after SCPT/SOUN/VTYP so its SCRI, SNAM/INAM and VNAM references remap against
        // already-allocated new FormIDs; ASPC after SOUN and REGN for its five SNAM slots and
        // RDAT; ADDN after SOUN for its SNAM.
        yield return ("TACT", records.GenericRecords.Where(g => g.RecordType == "TACT"));
        yield return ("ASPC", records.GenericRecords.Where(g => g.RecordType == "ASPC"));
        yield return ("ADDN", records.GenericRecords.Where(g => g.RecordType == "ADDN"));
        yield return ("LSCT", records.LoadScreenTypes);
        // Generic-record types wired 2026-08-26. Each is decoded on every DMP load into the shared
        // GenericRecords list and was previously dropped here with no diagnostic. Placement follows
        // the same forward-reference rule as the block above — a type is yielded only after every
        // type its FormID subrecords point at:
        //   LSCR.WMI1 → LSCT (yielded immediately above)
        //   CHIP.YNAM/ZNAM → SOUN, MSET.HNAM/INAM → SOUN (yielded far earlier)
        //   CAMS.MNAM → IMAD (yielded near the top, before SCPT)
        //   IDLM has no emitted references at all — IDLA is not recovered, so IDLC is written as 0.
        yield return ("LSCR", records.GenericRecords.Where(g => g.RecordType == "LSCR"));
        yield return ("CHIP", records.GenericRecords.Where(g => g.RecordType == "CHIP"));
        yield return ("IDLM", records.GenericRecords.Where(g => g.RecordType == "IDLM"));
        yield return ("CAMS", records.GenericRecords.Where(g => g.RecordType == "CAMS"));
        yield return ("MSET", records.GenericRecords.Where(g => g.RecordType == "MSET"));
        // Round 3 of the M1 wiring. Placed here for the same forward-reference reason: RGDL's XNAM
        // points at an NPC_/CREA and its TNAM at a BPTD, all three yielded well above. EFSH and
        // CSNO reference nothing outside their own DATA block.
        yield return ("EFSH", records.GenericRecords.Where(g => g.RecordType == "EFSH"));
        yield return ("RGDL", records.GenericRecords.Where(g => g.RecordType == "RGDL"));
        yield return ("CSNO", records.GenericRecords.Where(g => g.RecordType == "CSNO"));
        yield return ("IDLE", records.IdleAnimations);
        yield return ("IPCT", records.ImpactData);
        // IPDS's whole payload is a 12-slot table of IPCT references, so it must follow IPCT.
        // DOBJ's 34 slots point at ALCH/SPEL/FACT/NPC_/MUSC/VTYP, all yielded far above.
        yield return ("IPDS", records.GenericRecords.Where(g => g.RecordType == "IPDS"));
        yield return ("DOBJ", records.GenericRecords.Where(g => g.RecordType == "DOBJ"));
        yield return ("HDPT", records.HeadParts);
        yield return ("CPTH", records.CameraPaths);
        yield return ("ALOC", records.AudioLocationControllers);
        yield return ("DEBR", records.Debris);
        yield return ("REGN", records.Regions);
        yield return ("RADS", records.RadiationStages);
        yield return ("DEHY", records.DehydrationStages);
        yield return ("HUNG", records.HungerStages);
        yield return ("SLPD", records.SleepDeprivationStages);
    }

    private static uint ExtractFormId(object model)
    {
        var prop = model.GetType().GetProperty("FormId")
                   ?? throw new InvalidOperationException(
                       $"Model {model.GetType().Name} has no FormId property.");
        return (uint)prop.GetValue(model)!;
    }

    /// <summary>
    ///     Diagnostic: drop every cell whose <c>WorldspaceFormId</c> matches one of the
    ///     supplied excluded IDs, plus the worldspace records themselves and any NavMesh
    ///     records anchored to a dropped cell. All nested placements (REFR/ACHR/ACRE) live
    ///     under <c>CellRecord.PlacedObjects</c> and disappear with the parent cell. Used
    ///     to bisect crashes that point at a specific worldspace — per-FormID merge keeps
    ///     master content in effect for the excluded worldspace.
    /// </summary>
    private void FilterDmpRecordsByExcludedWorldspaces(
        RecordCollection records,
        IReadOnlySet<uint> excluded)
    {
        if (excluded is null || excluded.Count == 0)
        {
            return;
        }

        var cellsBefore = records.Cells.Count;
        var navmsBefore = records.NavMeshes.Count;
        var worldspacesBefore = records.Worldspaces.Count;

        var droppedCellFormIds = new HashSet<uint>();
        foreach (var cell in records.Cells)
        {
            if (cell.WorldspaceFormId is { } wsFid && excluded.Contains(wsFid))
            {
                droppedCellFormIds.Add(cell.FormId);
            }
        }

        records.Cells.RemoveAll(c =>
            c.WorldspaceFormId is { } wsFid && excluded.Contains(wsFid));
        records.NavMeshes.RemoveAll(n =>
            droppedCellFormIds.Contains(n.CellFormId));
        records.Worldspaces.RemoveAll(w => excluded.Contains(w.FormId));

        var cellsDropped = cellsBefore - records.Cells.Count;
        var navmsDropped = navmsBefore - records.NavMeshes.Count;
        var worldspacesDropped = worldspacesBefore - records.Worldspaces.Count;
        _sink.Info("Reading DMP",
            $"Excluded worldspaces: {string.Join(", ", excluded.Select(f => $"0x{f:X8}"))} — " +
            $"dropped {cellsDropped:N0} cell(s), {navmsDropped:N0} NAVM(s), " +
            $"{worldspacesDropped:N0} worldspace(s).");
    }

    /// <summary>
    ///     When an authoritative <c>CellFormId → WorldspaceFormId</c> map is supplied, apply
    ///     it to every parsed CELL before downstream grouping (<c>CellGrupBuilder</c>) keys off
    ///     <c>cell.WorldspaceFormId</c>. The authority overrides existing values because it is,
    ///     by construction, more trustworthy than the per-DMP heuristic inference pipeline.
    /// </summary>
    private void ApplyCellWorldspaceAuthority(
        RecordCollection records,
        EsmRecordScanResult? scanResult,
        IReadOnlyDictionary<uint, uint>? authority,
        IReadOnlyDictionary<uint, string>? worldspaceNames,
        IReadOnlyDictionary<uint, CellAuthorityMetadata>? cellMetadata,
        IReadOnlyDictionary<uint, uint>? refToCell,
        IReadOnlyList<CellReferenceParentWindow>? refWindows,
        bool inferUnresolvedPlacements)
    {
        var result = CellWorldspaceAuthorityApplier.Apply(
            records,
            authority,
            worldspaceNames,
            scanResult,
            cellMetadata,
            refToCell,
            refWindows,
            inferUnresolvedPlacements);
        if (result.Applied > 0 || result.ReferencesReattached > 0)
        {
            _sink.Info("Reading DMP",
                $"Cell authority applied: {result.Applied} mapping(s) - {result.Added} added, " +
                $"{result.Overrode} overrode prior inference; " +
                $"{result.ReferencesReattached} unresolved ref(s) reattached, " +
                $"{result.ReferenceWindowsApplied} pinned window(s) applied, " +
                $"{result.ReferenceCellsCreated} cell shell(s) created; " +
                $"{result.SynthesizedWorldspaces} worldspace shell(s) synthesized; " +
                $"{result.TerrainCellsAttached} terrain cell(s) attached.");
        }
    }

    /// <summary>
    ///     Build a unified VTYP-FormID → VTYP-EditorId lookup so DialogGrupBuilder can resolve
    ///     speaker voice types onto the engine's runtime path-construction shape. Includes
    ///     master VTYPs (master FormID keys) AND DMP-captured new VTYPs (source FormID keys —
    ///     same ones <see cref="BuildNpcVoiceTypeLookup" /> chains to). Without the new-VTYP
    ///     entries, a new NPC pointing at a new VTYP would never resolve to an EditorId and
    ///     dialogue audio bindings would be emitted with VoiceTypeEditorId=null, sending the
    ///     packer's voicetype directory to the engine's MaleAdult01Default fallback.
    /// </summary>
    private static Dictionary<uint, string> BuildVoiceTypeEditorIdLookup(
        IReadOnlyDictionary<uint, ParsedMainRecord> masterRecords,
        RecordCollection records)
    {
        var lookup = new Dictionary<uint, string>();
        foreach (var (fid, rec) in masterRecords)
        {
            if (rec.Header.Signature != "VTYP")
            {
                continue;
            }

            var edid = rec.EditorId;
            if (!string.IsNullOrEmpty(edid))
            {
                lookup[fid] = edid;
            }
        }

        foreach (var vtyp in records.VoiceTypes)
        {
            if (vtyp.FormId == 0 || string.IsNullOrEmpty(vtyp.EditorId))
            {
                continue;
            }

            // Source FormID keys; will collide with master keys only if a new VTYP somehow
            // shares a master VTYP's FormID, in which case the captured EDID wins.
            lookup[vtyp.FormId] = vtyp.EditorId;
        }

        return lookup;
    }

    /// <summary>
    ///     Build a DMP-source-NPC-FormID → DMP-source-VTCK-FormID lookup. Used by the
    ///     dialogue-audio binding emitter to resolve speakers belonging to NPCs the
    ///     converter is itself emitting (i.e., not in master). The keys are SOURCE FormIDs
    ///     so the lookup keys match <c>DialogueRecord.SpeakerFormId</c>; the values are also
    ///     source-side VTCK FormIDs and will hit the master VTYP EditorId lookup directly
    ///     when those VoiceType records are inherited from master.
    /// </summary>
    /// <summary>
    ///     Build a unified source-FormID → quest EDID lookup spanning the master ESM's QUST
    ///     records and the DMP's captured quests. INFOs reference quests by source FormID
    ///     (set at proto-build time), so the lookup is keyed on source-side IDs even for
    ///     new quests that get allocated FormIDs at emission.
    /// </summary>
    private static Dictionary<uint, string> BuildQuestEditorIdLookup(
        IReadOnlyDictionary<uint, ParsedMainRecord> masterRecords,
        RecordCollection records,
        Dictionary<uint, uint>? sourceToAllocated)
    {
        var lookup = new Dictionary<uint, string>();
        foreach (var (fid, rec) in masterRecords)
        {
            if (rec.Header.Signature != "QUST")
            {
                continue;
            }

            var edid = rec.EditorId;
            if (!string.IsNullOrEmpty(edid))
            {
                lookup[fid] = edid;
            }
        }

        foreach (var quest in records.Quests)
        {
            if (quest.FormId == 0 || string.IsNullOrEmpty(quest.EditorId))
            {
                continue;
            }

            lookup[quest.FormId] = quest.EditorId;
            // Also register the allocated key (see BuildNpcVoiceTypeLookup for the same
            // SanitizeInfoReferences-remap rationale).
            if (sourceToAllocated is not null
                && sourceToAllocated.TryGetValue(quest.FormId, out var allocated)
                && allocated != quest.FormId)
            {
                lookup.TryAdd(allocated, quest.EditorId);
            }
        }

        return lookup;
    }

    private static Dictionary<uint, uint> BuildNpcVoiceTypeLookup(
        RecordCollection records,
        Dictionary<uint, uint>? sourceToAllocated)
    {
        var lookup = new Dictionary<uint, uint>(records.Npcs.Count * 2);
        foreach (var npc in records.Npcs)
        {
            if (npc.FormId == 0 || npc.VoiceType is not { } vt || vt == 0)
            {
                continue;
            }

            lookup.TryAdd(npc.FormId, vt);
            // Also register the allocated FormID — DialogGrupBuilder calls SanitizeInfoReferences
            // BEFORE CollectAudioBindings, which remaps INFO.ANAM (SpeakerFormId) from source
            // to allocated. Without an allocated-key entry the speaker→VTYP chain misses and
            // bindings get VoiceTypeEditorId=null, sending audio paths to MaleAdult01Default.
            if (sourceToAllocated is not null
                && sourceToAllocated.TryGetValue(npc.FormId, out var allocatedNpc)
                && allocatedNpc != npc.FormId)
            {
                lookup.TryAdd(allocatedNpc, vt);
            }
        }

        return lookup;
    }

    private PluginBuildResult Fail(string errorMessage, ConversionPipelineStats stats, Stopwatch sw)
    {
        sw.Stop();
        stats.Elapsed = sw.Elapsed;
        stats.Errors++;
        _sink.Error("PluginConversionPipeline", errorMessage);
        _sink.OnComplete(stats);
        return new PluginBuildResult
        {
            Success = false,
            Stats = stats,
            ErrorMessage = errorMessage
        };
    }
}
