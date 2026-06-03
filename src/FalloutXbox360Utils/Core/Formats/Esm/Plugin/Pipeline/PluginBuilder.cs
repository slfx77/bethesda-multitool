using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm.Merge;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.AI;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Magic;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Parsing.Subrecords;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.AssetPacking;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Cell;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Nav;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Output;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Reference;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Validation;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;
using FalloutXbox360Utils.Core.Formats.Esm.Records;
using FalloutXbox360Utils.Core.Formats.Esm.Reporting;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using FalloutXbox360Utils.Core.Semantic;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Pipeline;

/// <summary>
///     Orchestrator that converts an Xbox 360 DMP into a PC plugin ESP using a base
///     FalloutNV.esm as the source of subrecord data the DMP doesn't carry.
///     Pipeline:
///     1. Load PC ESM (raw + semantic) and index FormIDs.
///     2. Load DMP semantically (cells with placed objects, simple-type record lists).
///     3. Merge top-level records: GMST/GLOB/WEAP/ARMO/AMMO/etc. become override records
///     inside their respective top-level GRUPs.
///     4. Merge cell-children: for each DMP cell that maps to a PC ESM cell, classify
///     the merge mode (persistent-only vs has-temporary), encode REFR/ACHR/ACRE
///     overrides, and bundle by parent cell.
///     5. Assemble ESP: TES4 header + top-level GRUPs + CELL hierarchy with cell-children
///     overrides.
///     6. Optionally validate by re-parsing.
/// </summary>
#pragma warning disable S1133 // Deprecated code reminder — intentional, removal tracked in roadmap.
[Obsolete("Use PluginConversionPipeline as the conversion orchestration entrypoint.")]
#pragma warning restore S1133
public sealed class PluginBuilder
{
    /// <summary>
    ///     Record types eligible to be referenced as the "base" of a REFR/ACHR/ACRE placed
    ///     reference. Shared with <see cref="ReferenceBaseRemapper" /> so the master index
    ///     and runtime REFR rescue policy use identical type gates.
    /// </summary>
    private static readonly HashSet<string> RefrBaseEligibleTypes = ReferenceBaseRemapper.RefrBaseEligibleTypes;

    /// <summary>
    ///     Tracks every new exterior cell emitted by <c>TryBuildSyntheticExteriorContext</c>,
    ///     keyed by (parent-worldspace FormID, gridX, gridY). The FNV runtime rejects two
    ///     cells claiming the same grid coords within one worldspace ("Cell ... already
    ///     exists at coord", "Error adding cell ... will be destroyed"), so we dedup at
    ///     emit time — first cell wins, subsequent ones are skipped with a warning.
    ///     Reset to empty at the start of each <see cref="BuildAsync" />.
    /// </summary>
    private readonly Dictionary<(uint Worldspace, int GridX, int GridY), uint> _emittedExteriorCellCoords = new();

    /// <summary>
    ///     Master CELL FormIDs we've already emitted an override anchor + children GRUP
    ///     for. Tracks both direct-FormID-match and grid-collision-redirected emissions so
    ///     subsequent DMP cells that resolve to the same master cell don't double-emit it.
    ///     Reset at each Build entry.
    /// </summary>
    private readonly HashSet<uint> _emittedOverrideCellFormIds = [];

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
    ///     Phase 0 pre-allocation: maps DMP-source placed-ref FormIDs (REFR / ACHR / ACRE
    ///     that don't exist in master) to their pre-allocated plugin-local FormIDs. Populated
    ///     in <see cref="PreAllocateNewPlacedRefFormIds" /> immediately before Phase 3 begins,
    ///     so Phase 3 encoders that consume <see cref="BuildAllValidFormIdSet" /> (notably
    ///     <c>PackEncoder.EncodeNew</c>) see the full set of placed-ref FormIDs that Phase 4
    ///     will emit. Phase 4 looks these up via <see cref="TryGetPreAllocatedPlacedRefFormId" />
    ///     instead of calling <see cref="FormIdAllocator.Allocate" /> directly.
    ///     Reset to empty at each Build entry. Orphans (refs Phase 0 pre-allocates but Phase 4
    ///     later drops) are tolerated — FormIDs are cheap, the allocator already permits gaps,
    ///     and the worst-case engine behaviour (PACK PLDT pointing at an allocated-but-unwritten
    ///     FormID) is a load-time warning, strictly better than today's silent Type-2 degradation.
    /// </summary>
    private readonly Dictionary<uint, uint> _preAllocatedRefFormIds = new();

    /// <summary>
    ///     Read-only test surface for <see cref="_preAllocatedRefFormIds" />. Tests assert
    ///     on Phase 0 allocation outcomes without needing to round-trip through Phase 4.
    /// </summary>
    internal IReadOnlyDictionary<uint, uint> PreAllocatedRefFormIdsForTest => _preAllocatedRefFormIds;

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
    ///     New NPC_ FormIDs whose encoded record has no master template + UseTraits bit.
    ///     Placing these as ACHR makes the engine try to load FaceGen assets we do not
    ///     generate, which has reproduced the WastelandNV render-walk crash class.
    ///     Contains both DMP-source IDs and allocated plugin-local IDs.
    /// </summary>
    private readonly HashSet<uint> _renderUnsafeNewNpcFormIds = [];

    private readonly RecordEncoderRegistry _encoderRegistry;

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
    ///     Master REFR/ACHR/ACRE/NAVM/LAND FormIDs — placed inside CELL Children GRUPs in the
    ///     master ESM. These are NOT in <see cref="_masterFormIds" /> (which only holds
    ///     top-level records) but the engine knows about them because the player ref
    ///     (0x00000014) and every placed instance lives here. SCRO validation must accept
    ///     references to them; without that, scripts that target the player or any vanilla
    ///     placed REFR get nulled and refuse to execute.
    /// </summary>
    private HashSet<uint>? _masterChildFormIds;

    /// <summary>
    ///     Per-record-type subset of <see cref="_masterFormIds" />. Lets validators answer
    ///     "is FormID X a SCPT in the master?" rather than the weaker "is FormID X anything
    ///     in the master?" — the loose check let SCRI references through that pointed at
    ///     STAT/ACTI/etc. FormIDs and produced "Unable to find script" load-time errors
    ///     (master FormID exists, but it's the wrong record type).
    /// </summary>
    private Dictionary<string, HashSet<uint>>? _masterFormIdsByType;

    /// <summary>
    ///     Phase C: per-type normalized-EditorID-stem → list of master FormIDs sharing
    ///     that stem. Built alongside <see cref="_masterEditorIdToFormIdByType" />. Used by
    ///     the REFR base-EditorID remap fallback to find rename-suffix candidates (e.g. a
    ///     prototype <c>SCOLParkingLotChunk03</c> mapping to master
    ///     <c>SCOLParkingLotChunk03b</c>). Values are lists so ambiguity (multiple master
    ///     records sharing the same stem) can be detected and refused.
    /// </summary>
    private Dictionary<string, Dictionary<string, List<uint>>>? _masterStemToFormIdsByType;

    /// <summary>
    ///     New worldspaces (not in master) whose DMP carries child cells. These are
    ///     pre-encoded so the cell-children pipeline can emit them as full WRLD GRUPs
    ///     (anchor record + World Children GRUP containing the captured cells). Keyed by
    ///     the ORIGINAL DMP-source FormID of the worldspace so cell-children grouping by
    ///     <c>dmpCell.WorldspaceFormId</c> finds them directly. Null until pre-encode runs.
    /// </summary>
    private Dictionary<uint, NewWorldspaceEntry>? _newWorldspacesForCellPipeline;

    /// <summary>
    ///     Per-build planner state. Constructed once in <see cref="BuildAsync" /> after the
    ///     DMP loads, only when <c>PluginBuildOptions.PlannerEnabledRecordTypes</c> is
    ///     non-empty. Consumed by the dispatch shim in <see cref="BuildGrupForType" />.
    ///     Null whenever the build runs entirely on the legacy pipeline.
    /// </summary>
    private FalloutXbox360Utils.Core.Formats.Esm.Planner.EmitPlan? _emitPlan;
    private FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter.PlanWriter? _planWriter;

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
            _masterChildFormIds = new HashSet<uint>(masterIndex.ChildLocations.Keys);
            _emittedNewFormIds.Clear();
            _emittedNewFormIdsByType.Clear();
            _preAllocatedRefFormIds.Clear();
            _placedRefValidFormIdsCache = null;
            _renderUnsafeNewNpcFormIds.Clear();
            _emittedExteriorCellCoords.Clear();
            _emittedOverrideCellFormIds.Clear();
            _masterExteriorCellByGrid.Clear();
            _masterNpcByRace.Clear();
            _newRecordSourceToAllocated.Clear();
            _newRecordSourceToAllocatedType.Clear();
            _masterEditorIdToFormIdByType = masterIndex.EditorIdToFormIdByType;
            _masterStemToFormIdsByType = masterIndex.StemToFormIdsByType;

            // Build the master child-location index from GRUP ancestry. Existing placed-ref
            // overrides must be emitted under their master child GRUP, not under whichever
            // runtime cell snapshot happened to mention them.
            var masterChildLocations = masterIndex.ChildLocations;
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
                    ApplyDefaultCellWorldspaceAuthority = false
                },
                ct);
            var dmpRecords = unified.Records;
            ApplyCellWorldspaceAuthority(
                dmpRecords,
                unified.RawResult.EsmRecords,
                inputs.Options.CellWorldspaceAuthority,
                inputs.Options.CellWorldspaceAuthorityWorldspaceNames,
                inputs.Options.CellMetadataAuthority,
                inputs.Options.CellReferenceParentAuthority,
                inputs.Options.CellReferenceParentWindows);
            FilterDmpRecordsByExcludedWorldspaces(dmpRecords, inputs.Options.SkipWorldspaceFormIds);
            _dmpBaseFormIdToRecordType = ReferenceBaseRemapper.BuildDmpBaseFormIdToRecordType(dmpRecords);
            _sink.Info("Reading DMP", "DMP semantic load complete.");
            _sink.OnPhaseEnd("Reading DMP", stats);
            ct.ThrowIfCancellationRequested();

            // Asset-rename pass: rewrite record paths in-place when fuzzy resolution
            // matches a differently-named asset in an indexed Data folder. Runs BEFORE
            // encoding so the output ESP carries the unified paths. No-op when the user
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

            // EDID-based SCRI fallback: when an NPC/creature has no captured script binding
            // (its TESScriptableForm::pFormScript slot was null at DMP capture), attach a
            // script whose EditorId is "<formEditorId>Script". This matches FNV vanilla's
            // naming convention (e.g. CassFollowerScript → CassFollower; UlyssesScript →
            // Ulysses) and recovers orphan-script bindings the proto's runtime hadn't yet
            // wired up. This is the SOLE recovery path for null pFormScript on NPCs/creatures
            // — RuntimeActorReader no longer brute-force-scans struct memory for Script*
            // pointers, because the prefix-based gate was unsafe (VMS01PartsSCRIPT falsely
            // matched VMS01DocMitchell, breaking the Doc Mitchell intro and Sunny Smiles
            // quest state). See memory/quest_script_brute_force_scan.md.
            AttachOrphanScriptsByEditorId(dmpRecords);

            // EDID-based VTCK fallback: NPCs may carry a VTCK FormID from the proto runtime
            // that no longer exists in vanilla or our build (FormID 0x0014F3EB on Ulysses, for
            // example — that FormID is a hole between MaleUniqueTheKing (0x...EC) and
            // RobotUniqueRex (0x...EA)). When the engine can't resolve VTCK, it falls back to
            // MaleAdult01Default and audio lookups under the unique voicetype directory all
            // fail. Re-bind the VTCK to a matching VTYP whose EditorId is
            // "MaleUnique<NpcEditorId>" / "FemaleUnique<NpcEditorId>" — vanilla's convention.
            AttachOrphanVoiceTypesByEditorId(dmpRecords);

            // Phase 0: pre-allocate FormIDs for new placed refs so PACK PLDT (and any other
            // Phase-3 encoder consuming BuildAllValidFormIdSet) can resolve Union references
            // to refs Phase 4 will emit. See PreAllocateNewPlacedRefFormIds doc comment for
            // the silent-PLDT-degradation problem this prevents.
            PreAllocateNewPlacedRefFormIds(dmpRecords, pcRecordsByFormId, allocator, stats);

            // Two-pass planner: when any record type opts into the planner via
            // PlannerEnabledRecordTypes, build the EmitPlan once here so BuildGrupForType's
            // dispatch shim can route those types through PlanWriter. Skipped entirely when
            // the option set is empty (the default), so the legacy pipeline runs unchanged.
            BuildPlannerStateIfEnabled(
                pcRecordsList, dmpRecords, allocator, inputs,
                cellContexts, pcRecordsByFormId);

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

            // Dialogue-text backfill from --dialogue-audio-csv: when the DMP capture left a
            // response blank or marked it "(NOT FOUND IN CRASH DUMP)", the audio CSV's Text
            // column (one row per voice file = one response number) supplies the missing line
            // so the engine emits real dialog instead of the placeholder sentinel. Applied
            // in-place to dmpRecords.Dialogues before encoding.
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
            var voiceTypeEditorIdsByFormId = BuildVoiceTypeEditorIdLookup(pcRecordsByFormId, dmpRecords);
            var npcVoiceTypeByNpcFormId = BuildNpcVoiceTypeLookup(dmpRecords, _newRecordSourceToAllocated);
            // Quest source-FormID → EDID lookup, unifying master + DMP quests. Needed by
            // CollectAudioBindings to record QuestEditorId on each emitted binding (so the
            // packer can reconstruct the engine-shaped voice filename when CSV truncation
            // differs from what the runtime constructs).
            var questEditorIdsByFormId = BuildQuestEditorIdLookup(
                pcRecordsByFormId, dmpRecords, _newRecordSourceToAllocated);
            IEnumerable<uint> dialogAdditionalValidFormIds = _newRecordSourceToAllocated.Values;
            if (_masterChildFormIds is not null)
            {
                dialogAdditionalValidFormIds = dialogAdditionalValidFormIds.Concat(_masterChildFormIds);
            }

            var dialogResult = DialogGrupBuilder.BuildDialogSection(
                dmpRecords.DialogTopics, dmpRecords.Dialogues, classifier, allocator,
                pcRecordsByFormId.Keys, pcRecordsByFormId, stats, _sink,
                remapTable: _newRecordSourceToAllocated,
                additionalValidFormIds: dialogAdditionalValidFormIds,
                voiceTypeEditorIdsByFormId: voiceTypeEditorIdsByFormId,
                npcVoiceTypeByNpcFormId: npcVoiceTypeByNpcFormId,
                questEditorIdsByFormId: questEditorIdsByFormId);
            if (dialogResult.DialogSection.Length > 0)
            {
                grupBytesByType["DIAL"] = dialogResult.DialogSection;
            }

            // Record INFO source→allocated mappings so the asset packer can rewrite voice
            // file paths onto the engine's runtime lookup shape.
            foreach (var (sourceId, allocatedId) in dialogResult.NewInfoSourceToAllocated)
            {
                _newRecordSourceToAllocated.TryAdd(sourceId, allocatedId);
            }

            _sink.Info("Merging top-level records",
                $"Recorded {dialogResult.NewInfoSourceToAllocated.Count:N0} INFO source→allocated " +
                $"mapping(s) (total remap entries now: {_newRecordSourceToAllocated.Count:N0}).",
                code: "dialog.remap.recorded");

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
            // Also allocates plugin-index FormIDs for new cells/refs and synthesizes
            // deletion-flag overrides for LoadedReplacement cells.
            _sink.OnPhaseStart("Merging cell children", null);
            var pcRefFormIds = new HashSet<uint>(refToCell.Keys);
            var bundles = BuildCellOverrideBundles(
                dmpRecords, pcRecordsByFormId, refToCell, masterChildLocations, pcRefFormIds, cellContexts,
                navmsByCell, landsByCell, allocator, grupBytesByType, inputs.Options, stats,
                out var newNavmEntries, ct);
            _sink.Info("Merging cell children",
                $"Built {bundles.Count:N0} cell-override bundle(s); allocated {allocator.NextLocalId - allocator.BaseLocalId:N0} new FormID(s).");

            // NAVI override: registers every newly-emitted NAVM in master's NavMeshInfoMap.
            // Required to prevent FalloutNV+0x0069E09A null-deref during plugin load (NavMesh
            // walked by engine -> lookup in NavMeshInfoMap -> not found -> crash). When CELL
            // is planner-routed, BuildCellOverrideBundles still runs (its result isn't used,
            // but it walks the same DMP records) and may populate the legacy list partially.
            // Those entries are stale because the planner allocated different FormIDs. Use
            // the planner's NavmEntries when CELL is planner-routed, the legacy list otherwise.
            var cellPlannerRouted = inputs.Options.PlannerEnabledRecordTypes.Contains("CELL");
            var naviSource = cellPlannerRouted && _emitPlan is not null
                ? _emitPlan.NavmEntries
                    .Select(e => new NewNavmEntry(e.NavmFormId, e.LocationFormId, e.IsInterior,
                        (short)e.GridX, (short)e.GridY, e.NvvxBytes.Length > 0 ? e.NvvxBytes : null))
                    .ToList()
                : newNavmEntries;
            if (naviSource.Count > 0
                && pcRecordsByFormId.TryGetValue(NavInfoMapBuilder.MasterNaviFormId, out var masterNavi)
                && masterNavi.Header.Signature == "NAVI")
            {
                var naviOverrideBytes = NavInfoMapBuilder.BuildNaviOverride(masterNavi, naviSource, inputs.Options);
                AppendOrCreateTopLevelRecord(grupBytesByType, "NAVI", naviOverrideBytes);
                _sink.Info("Merging cell children",
                    $"Emitted NAVI override with {naviSource.Count:N0} new NVMI+NVCI entry pair(s) " +
                    $"(extends master 0x{NavInfoMapBuilder.MasterNaviFormId:X8}, source={(cellPlannerRouted ? "planner" : "legacy")}). " +
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
                _newWorldspacesForCellPipeline,
                _emitPlan);
            await File.WriteAllBytesAsync(inputs.OutputEspPath, outputBytes, ct);
            stats.OutputBytes = outputBytes.LongLength;
            _sink.OnPhaseEnd("Writing ESP", stats);

            // Phase 6 (optional): validate by re-parsing + semantic check.
            string? validationReport = null;
            if (inputs.Options.ValidateOutput)
            {
                _sink.OnPhaseStart("Validating output", null);
                var roundTrip = PluginRoundTripValidator.Validate(outputBytes, stats.RecordsEmitted);
                _sink.Info("Validating output", roundTrip);

                // Semantic check catches the structural issues that round-trip parsing alone
                // misses — duplicate FormIDs, persistent-flag/parent-GRUP-type mismatches,
                // dangling base FormIDs in NAME subrecords. These were the FNVEdit-class
                // bugs behind the WastelandNV render crash.
                var semantic = PluginSemanticValidator.Validate(outputBytes, _masterFormIds, _masterFormIdsByType);
                _sink.Info("Validating output", semantic.Report);
                if (semantic.ErrorCount > 0)
                {
                    _sink.Warn("Validating output",
                        $"Semantic validation surfaced {semantic.ErrorCount:N0} error(s) — ESP may crash at load.",
                        "ESP", 0, "semantic.errors");
                }

                validationReport = $"{roundTrip}\n\n{semantic.Report}";
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
            => !script.HasValue || script.Value == 0 || !validScriptFormIds.Contains(script.Value);

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
            => vtck.HasValue && vtck.Value != 0
               && !masterVtypSet.Contains(vtck.Value)
               && !dmpVtypSet.Contains(vtck.Value);

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
    ///     Phase 0: pre-allocate plugin-local FormIDs for every DMP placed-ref (REFR / ACHR /
    ///     ACRE) that does not exist in master, so Phase 3 encoders that consume
    ///     <see cref="BuildAllValidFormIdSet" /> — notably <c>PackEncoder.EncodeNew</c>'s
    ///     PLDT/PLD2 Union resolution — see the full set of placed-ref FormIDs Phase 4 will
    ///     emit.
    ///     <para>
    ///     Without this pre-pass, PACK records encoded in Phase 3 cannot validate Union
    ///     references against placed refs that won't get FormIDs until Phase 4 cell-children
    ///     iteration. Result: PLDT silently degrades from Type 0 (NearReference) to Type 2
    ///     (NearCurrentLocation), changing NPC AI location targeting.
    ///     </para>
    ///     <para>
    ///     Skip predicates intentionally mirror Phase 4's pre-allocation guards at
    ///     <c>PluginBuilder.cs:2091-2308</c> (runtime-state, master override, no encoder
    ///     registered) so a pre-allocated FormID generally corresponds to a ref Phase 4 will
    ///     emit. Residual orphans (Phase 4 drops a ref after Phase 0 reserved its FormID) are
    ///     tolerated — the engine treats a PLDT pointing at an unwritten FormID as a load-time
    ///     warning, strictly better than the silent-degradation status quo.
    ///     </para>
    ///     <para>
    ///     Pre-allocates via the same shared <see cref="FormIdAllocator" /> instance used by
    ///     Phase 3 and Phase 4, and populates both <see cref="_emittedNewFormIds" /> +
    ///     <see cref="_emittedNewFormIdsByType" /> (via <see cref="TrackEmittedNewFormId" />)
    ///     and the source→allocated remap dict (via <see cref="TrackNewRecordSourceAlias" />)
    ///     so encoder-level <c>FormIdReferenceResolver.Resolve</c> calls find both the source
    ///     FormID's alias and the validity confirmation in one place. Pattern mirrors NAVM
    ///     Phase A pre-allocation at <c>PluginBuilder.cs:1808-1839</c>.
    ///     </para>
    /// </summary>
    internal void PreAllocateNewPlacedRefFormIds(
        RecordCollection dmpRecords,
        IReadOnlyDictionary<uint, ParsedMainRecord> pcRecordsByFormId,
        FormIdAllocator allocator,
        ConversionPipelineStats stats)
    {
        foreach (var cell in dmpRecords.Cells)
        {
            foreach (var placed in cell.PlacedObjects)
            {
                // Mirror Phase 4 skip-runtime-state (PluginBuilder.cs:2091).
                if (RuntimeStateRecordPolicy.IsRuntimeStateFormId(placed.FormId))
                {
                    continue;
                }

                // Mirror Phase 4 skip-master-override (PluginBuilder.cs:2101). Phase 0 only
                // pre-allocates NEW placed refs; existing master refs reuse their master FormID.
                if (pcRecordsByFormId.ContainsKey(placed.FormId))
                {
                    continue;
                }

                // Defensive type gate — Phase 4 only allocates new FormIDs for the placed-ref
                // record types. Phase 0 skips anything else (the typed Cells.PlacedObjects
                // collection should already filter to these).
                if (placed.RecordType is not ("REFR" or "ACHR" or "ACRE"))
                {
                    continue;
                }

                // Mirror Phase 4 skip-no-encoder (PluginBuilder.cs:2308). Refs whose record
                // type has no registered encoder will be dropped in Phase 4 anyway, so they
                // shouldn't consume a FormID slot.
                if (_encoderRegistry.Get(placed.RecordType) is null)
                {
                    continue;
                }

                // Dedup across cell-capture unions — the same source REFR FormID can appear
                // under multiple DMP cell captures (the union pass runs in Phase 4, so Phase 0
                // sees the raw per-cell lists). Mirrors NAVM dedup at PluginBuilder.cs:1835.
                if (_preAllocatedRefFormIds.ContainsKey(placed.FormId))
                {
                    continue;
                }

                var pre = allocator.Allocate();
                _preAllocatedRefFormIds[placed.FormId] = pre;
                TrackEmittedNewFormId(placed.RecordType, pre);
                TrackNewRecordSourceAlias(placed.RecordType, placed.FormId, pre);
            }
        }

        if (_preAllocatedRefFormIds.Count > 0)
        {
            _sink.Info("Pre-allocating placed-ref FormIDs",
                $"Phase 0: pre-allocated {_preAllocatedRefFormIds.Count:N0} placed-ref FormID(s) " +
                "so Phase 3 encoders (e.g. PACK PLDT) can resolve Union references that " +
                "won't be emitted until Phase 4 cell-children.",
                code: "phase0.preallocate-refs");
        }
    }

    /// <summary>
    ///     Build the EmitPlan once when at least one record type is opted into the planner.
    ///     The plan is consumed by <see cref="BuildGrupForType" /> per-type. No-op when
    ///     <c>PlannerEnabledRecordTypes</c> is empty (the legacy pipeline owns the whole build).
    /// </summary>
    private void BuildPlannerStateIfEnabled(
        IReadOnlyList<ParsedMainRecord> pcRecords,
        RecordCollection dmpRecords,
        FormIdAllocator allocator,
        DmpToEspInputs inputs,
        IReadOnlyDictionary<uint, PcEsmCellContext> masterCellContexts,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterRecordsByFormId)
    {
        _emitPlan = null;
        _planWriter = null;

        var enabled = inputs.Options.PlannerEnabledRecordTypes;
        if (enabled.Count == 0)
        {
            return;
        }

        var registry = FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter.PlannedEncoders.BuildRegistry();
        var dispositionEngine = new FalloutXbox360Utils.Core.Formats.Esm.Planner.Disposition.DispositionEngine(
            new FalloutXbox360Utils.Core.Formats.Esm.Planner.Disposition.IDispositionPolicy[]
            {
                new FalloutXbox360Utils.Core.Formats.Esm.Planner.Disposition.Policies.ScriptDispositionPolicy(),
                new FalloutXbox360Utils.Core.Formats.Esm.Planner.Disposition.Policies.RuntimeStatePolicy(),
                new FalloutXbox360Utils.Core.Formats.Esm.Planner.Disposition.Policies.DefaultDispositionPolicy(),
            });
        var degradationPolicy = new FalloutXbox360Utils.Core.Formats.Esm.Planner.References.DegradationPolicy();
        degradationPolicy.SetDefaultForType(
            "SCPT",
            FalloutXbox360Utils.Core.Formats.Esm.Planner.References.DanglingAction.DropSubrecord);
        degradationPolicy.SetDefaultForType(
            "INFO",
            FalloutXbox360Utils.Core.Formats.Esm.Planner.References.DanglingAction.DropSubrecord);
        degradationPolicy.SetDefaultForType(
            "PACK",
            FalloutXbox360Utils.Core.Formats.Esm.Planner.References.DanglingAction.DropSubrecord);
        // PACK PLDT/PLD2 union dangles reshape the location subrecord to Type 2 instead of
        // dropping it, matching the v51 fix's behavior. The reshape constants are documented
        // on ContainerDowngrade.
        var pldtDowngrade = new FalloutXbox360Utils.Core.Formats.Esm.Planner.ContainerDowngrade
        {
            ContainerSignature = "PLDT",
            FromShape = "Type 0 (NearReference)",
            ToShape = "Type 2 (NearCurrentLocation)",
        };
        var pld2Downgrade = new FalloutXbox360Utils.Core.Formats.Esm.Planner.ContainerDowngrade
        {
            ContainerSignature = "PLD2",
            FromShape = "Type 0 (NearReference)",
            ToShape = "Type 2 (NearCurrentLocation)",
        };
        degradationPolicy.SetRule(
            "PACK", "PLDT.Union",
            FalloutXbox360Utils.Core.Formats.Esm.Planner.References.DanglingAction.DowngradeContainer(pldtDowngrade));
        degradationPolicy.SetRule(
            "PACK", "PLD2.Union",
            FalloutXbox360Utils.Core.Formats.Esm.Planner.References.DanglingAction.DowngradeContainer(pld2Downgrade));
        degradationPolicy.SetDefaultForType(
            "NPC_",
            FalloutXbox360Utils.Core.Formats.Esm.Planner.References.DanglingAction.DropSubrecord);
        degradationPolicy.SetDefaultForType(
            "CREA",
            FalloutXbox360Utils.Core.Formats.Esm.Planner.References.DanglingAction.DropSubrecord);
        degradationPolicy.SetDefaultForType(
            "PERK",
            FalloutXbox360Utils.Core.Formats.Esm.Planner.References.DanglingAction.DropSubrecord);
        var referenceResolver = new FalloutXbox360Utils.Core.Formats.Esm.Planner.References.ReferenceResolver(
            new FalloutXbox360Utils.Core.Formats.Esm.Planner.References.IRecordReferenceWalker[]
            {
                new FalloutXbox360Utils.Core.Formats.Esm.Planner.References.Walkers.ScriptReferenceWalker(),
                new FalloutXbox360Utils.Core.Formats.Esm.Planner.References.Walkers.PackageReferenceWalker(),
                new FalloutXbox360Utils.Core.Formats.Esm.Planner.References.Walkers.InfoReferenceWalker(),
                new FalloutXbox360Utils.Core.Formats.Esm.Planner.References.Walkers.NpcReferenceWalker(),
                new FalloutXbox360Utils.Core.Formats.Esm.Planner.References.Walkers.CreatureReferenceWalker(),
                new FalloutXbox360Utils.Core.Formats.Esm.Planner.References.Walkers.PerkReferenceWalker(),
            },
            degradationPolicy);

        var esmPlanner = new FalloutXbox360Utils.Core.Formats.Esm.Planner.EsmPlanner(
            dispositionEngine, allocator, referenceResolver);

        _emitPlan = esmPlanner.Build(
            pcRecords,
            dmpRecords,
            enabled,
            _masterFormIds ?? new HashSet<uint>(),
            inputs.PcEsmPath,
            masterCellContexts: masterCellContexts,
            masterRecordsByFormId: masterRecordsByFormId,
            cellChildAllocator: allocator);

        _planWriter = new FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter.PlanWriter(registry);

        _sink.Info("Two-pass planner",
            $"Built EmitPlan covering {enabled.Count} record type(s): " +
            $"{_emitPlan.Records.Length:N0} planned record(s), " +
            $"{_emitPlan.CellsByFormId.Count:N0} cell plan(s), " +
            $"{_emitPlan.SourceToEmittedFormId.Count:N0} FormID allocation(s).",
            code: "planner.built");
    }

    private Dictionary<uint, NewWorldspaceEntry> PreEncodeNewWorldspacesWithCells(
        RecordCollection dmpRecords,
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

            if (_newRecordSourceToAllocated.TryGetValue(wrld.FormId, out var masterAlias)
                && classifier.IsOverride(masterAlias))
            {
                continue;
            }

            if (!worldspacesWithCells.Contains(wrld.FormId))
            {
                continue;
            }

            var encoded = WrldEncoder.EncodeNew(wrld);
            if (encoded.Subrecords.Count == 0)
            {
                continue; // Encoder declined this WRLD (rare; e.g., insufficient metadata).
            }

            var emittedFormId = allocator.Allocate();
            var flags = options.CompressRecords ? 0x00040000u : 0u;
            var recordBytes =
                PluginRecordByteBuilder.BuildNewRecordBytes("WRLD", emittedFormId, flags, encoded.Subrecords);

            result[wrld.FormId] = new NewWorldspaceEntry(emittedFormId, recordBytes);
            TrackNewRecordSourceAlias("WRLD", wrld.FormId, emittedFormId);

            if (options.VerboseDecisions)
            {
                _sink.Decision("Merging top-level records",
                    $"Pre-encoded new WRLD 0x{wrld.FormId:X8} → emitted 0x{emittedFormId:X8} " +
                    "(deferred to cell-children pipeline so child cells nest under it).",
                    "WRLD", wrld.FormId, "wrld.deferred-with-cells");
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
        // Two-pass migration switch. When a record type is listed in
        // PlannerEnabledRecordTypes, the new EsmPlanner / PlanWriter pipeline owns its
        // emission and the legacy path below is bypassed. The plan was built upfront in
        // BuildAsync (BuildPlannerStateIfEnabled); we just dispatch to PlanWriter here.
        // "CELL" is the cell-hierarchy sentinel — that key activates the whole cell
        // pipeline (CELL/REFR/ACHR/ACRE/LAND/NAVM/NAVI) through EspAssembler instead of
        // through this top-level dispatch.
        if (options.PlannerEnabledRecordTypes.Contains(recordType) && recordType != "CELL")
        {
            if (_planWriter is null || _emitPlan is null)
            {
                throw new InvalidOperationException(
                    "PlannerEnabledRecordTypes is non-empty but planner state was not constructed. " +
                    "BuildPlannerStateIfEnabled was not called or failed silently.");
            }

            if (!_planWriter.Handles(recordType))
            {
                throw new InvalidOperationException(
                    $"PlannerEnabledRecordTypes contains '{recordType}' but no IPlannedRecordEncoder is " +
                    "registered for it. Add the encoder to PlannedEncoders.BuildAll() or drop the type " +
                    "from the option set.");
            }

            return _planWriter.BuildGrupForType(recordType, _emitPlan, options);
        }

        var policy = SubrecordMergePolicy.ForRecordType(recordType);

        using var grupBodyStream = new MemoryStream();
        var anyEmitted = false;

        // Per-FormID dedup: the DMP scanner can pick up the same record twice (e.g. when the
        // dump contains both an in-memory GMST master and a duplicate GRUP snapshot). Emitting
        // both yields "ESP contains 481 duplicate FormID(s)" — the engine then resolves only
        // one and may bind ACRE/ACHR base pointers to the wrong copy, causing crashes.
        var emittedFormIds = new HashSet<uint>();

        foreach (var model in models)
        {
            stats.RecordsConsidered++;

            // Phase B (SCOL census): tally every DMP SCOL we look at, regardless of branch.
            if (recordType == "SCOL")
            {
                stats.Scols.TotalParsed++;
            }

            var formId = ExtractFormId(model);

            if (formId != 0 && !emittedFormIds.Add(formId))
            {
                stats.IncrementSkipped(recordType);
                _sink.Decision("Merging top-level records",
                    $"Dedup: dropping duplicate {recordType} 0x{formId:X8} — DMP parsing surfaced " +
                    "two copies; keeping the first.",
                    recordType, formId, "dedup.duplicate-formid");
                continue;
            }

            if (RuntimeStateRecordPolicy.IsRuntimeStateFormId(formId))
            {
                stats.IncrementSkipped(recordType);
                _sink.Decision("Merging top-level records",
                    $"Skipping runtime-state record 0x{formId:X8} — DMP captures live gameplay " +
                    "state for engine-reserved records; re-emitting would clobber the engine's " +
                    "runtime setup (player / clock / default weapon).",
                    recordType, formId, "runtime-state.skip");
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

            if (!classifier.IsOverride(formId))
            {
                // Exact FormID remains primary identity. If the source FormID is not
                // present in master but the same-type EditorID exactly names a master record,
                // treat the source ID as an alias to that final master ID and emit a normal
                // override. No stem/fuzzy matching is used here; e.g. RoseOfSharonCassidyOLD
                // remains a distinct prototype record.
                if (TryFindMasterByEditorId(recordType, model, out var masterFormId))
                {
                    // Dedup on the ALIASED master FormID — two source models with different
                    // source FormIDs but the same EditorID would otherwise emit two override
                    // records pointing at the same master.
                    if (!emittedFormIds.Add(masterFormId))
                    {
                        stats.IncrementSkipped(recordType);
                        _sink.Decision("Merging top-level records",
                            $"Dedup: dropping alias override {recordType} 0x{formId:X8} → master 0x{masterFormId:X8} " +
                            "— another source already aliased to the same master.",
                            recordType, formId, "dedup.alias-collision");
                        continue;
                    }

                    TrackNewRecordSourceAlias(recordType, formId, masterFormId);
                    if (TryEncodeEditorIdAliasOverride(recordType, model, encoder, pcRecords, masterFormId,
                            policy, options, stats, out var aliasOverrideBytes))
                    {
                        grupBodyStream.Write(aliasOverrideBytes);
                        anyEmitted = true;
                        stats.IncrementEmitted(recordType);
                        stats.OverridesEmitted++;
                    }
                    else
                    {
                        stats.IncrementSkipped(recordType);
                    }

                    continue;
                }

                // Route through new-record path if this type has a new-record encoder.
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

                    // Track FormIDs emitted via the new-record path so ValidateScriRefs
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

            if (TryEncodeProvenScriptBearingOverride(
                    recordType, model, pcRecords, formId, options, stats, out var scriptOverrideBytes))
            {
                grupBodyStream.Write(scriptOverrideBytes);
                anyEmitted = true;
                stats.IncrementEmitted(recordType);
                stats.OverridesEmitted++;
                continue;
            }

            // Phase B (SCOL census): observe master-vs-DMP delta for SCOLs without emitting
            // anything. Counts toward the Phase D gate (only implement SCOL override emission
            // once non-zero deltas are observed across the active DMP corpus).
            if (recordType == "SCOL"
                && model is StaticCollectionRecord dmpScol)
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
                        recordType, formId, "scol.override-delta-observed");
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

            var remappedSubrecords = RemapEncodedFormIds(recordType, encoded.Subrecords);
            if (recordType == "LTEX")
            {
                remappedSubrecords = ValidateLtexRefs(remappedSubrecords, formId);
            }

            var validatedSubrecords = ValidateScriRefs(remappedSubrecords, recordType, formId);
            if (validatedSubrecords.Count == 0)
            {
                stats.IncrementSkipped(recordType);
                continue;
            }

            encoded = encoded with { Subrecords = validatedSubrecords };
            var merge = RecordMergeEngine.Merge(esmRecord, encoded, policy);
            foreach (var w in merge.Warnings)
            {
                stats.Warnings++;
                _sink.Warn("Merging top-level records", w, recordType, formId);
            }

            var recordBytes = PluginRecordByteBuilder.BuildOverrideRecordBytes(esmRecord, merge.SubrecordBytes, options);
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

    private bool TryEncodeEditorIdAliasOverride(
        string recordType,
        object model,
        IRecordEncoder encoder,
        Dictionary<uint, ParsedMainRecord> pcRecords,
        uint masterFormId,
        SubrecordMergePolicy policy,
        PluginBuildOptions options,
        ConversionPipelineStats stats,
        out byte[] recordBytes)
    {
        recordBytes = [];
        var sourceFormId = ExtractFormId(model);

        if (!pcRecords.TryGetValue(masterFormId, out var esmRecord))
        {
            _sink.Warn("Merging top-level records",
                $"EditorID alias target 0x{masterFormId:X8} was not in the PC ESM index.",
                recordType, sourceFormId, "editor-id.alias-missing-master");
            return false;
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
                $"Alias override encoder threw {ex.GetType().Name}: {ex.Message}",
                recordType, sourceFormId);
            return false;
        }

        foreach (var warning in encoded.Warnings)
        {
            stats.Warnings++;
            _sink.Warn("Merging top-level records", warning, recordType, sourceFormId);
        }

        if (encoded.Subrecords.Count == 0)
        {
            if (options.VerboseDecisions)
            {
                _sink.Decision("Merging top-level records",
                    $"EditorID alias 0x{sourceFormId:X8} -> 0x{masterFormId:X8} produced no override subrecords.",
                    recordType, sourceFormId, "editor-id.alias-empty");
            }

            return false;
        }

        var subrecords = RemapEncodedFormIds(recordType, encoded.Subrecords);
        if (recordType == "LTEX")
        {
            subrecords = ValidateLtexRefs(subrecords, sourceFormId);
        }

        subrecords = ValidateScriRefs(subrecords, recordType, sourceFormId);
        if (subrecords.Count == 0)
        {
            return false;
        }

        encoded = encoded with { Subrecords = subrecords };
        var merge = RecordMergeEngine.Merge(esmRecord, encoded, policy);
        foreach (var warning in merge.Warnings)
        {
            stats.Warnings++;
            _sink.Warn("Merging top-level records", warning, recordType, sourceFormId);
        }

        recordBytes = PluginRecordByteBuilder.BuildOverrideRecordBytes(esmRecord, merge.SubrecordBytes, options);
        if (options.VerboseDecisions)
        {
            _sink.Decision("Merging top-level records",
                $"EditorID alias override emitted: DMP 0x{sourceFormId:X8} -> master 0x{masterFormId:X8} " +
                $"({merge.DmpSignaturesUsed.Count} DMP, {merge.EsmSignaturesRetained.Count} ESM).",
                recordType, sourceFormId, "editor-id.alias-override");
        }

        return true;
    }

    /// <summary>
    ///     Optionally emit a full QUST or SCPT override when the DMP captured a meaningful
    ///     script body that diverges from master. Bypasses the canonical
    ///     <see cref="IRecordEncoder.Encode" /> override path (which deliberately skips
    ///     SCRI / full script blocks because partial DMP captures could desynchronize the
    ///     master record).
    ///     <para>
    ///     Safe only because the upstream Script* probes are now constrained:
    ///     <c>RuntimeQuestTerminalReader</c> validates candidates via the
    ///     <c>Script.pOwnerQuest</c> backpointer, and <c>RuntimeActorReader</c> no longer
    ///     brute-force-scans NPC struct memory at all (the prefix-based gate it relied on
    ///     was unsafe — VMS01PartsSCRIPT falsely matched VMS01DocMitchell). Null
    ///     <c>pFormScript</c> on NPCs/creatures is now recovered only via the exact-name
    ///     <see cref="AttachOrphanScriptsByEditorId" /> heuristic. See
    ///     <c>memory/quest_script_brute_force_scan.md</c>. Before these constraints landed,
    ///     this override silently bound master quests (e.g. VMS01) to arbitrary
    ///     tutorial-cluster scripts, breaking the Doc Mitchell "Finished" prompt.
    ///     <see cref="Tests.Core.Formats.Esm.Plugin.ScriptOverridePolicyTests" /> in the test
    ///     project pins the contract that <see cref="QustEncoder.Encode" /> +
    ///     <see cref="ScptEncoder" /> never emit SCRI / full script overrides via the standard
    ///     encoder loop; this method is the dedicated channel for that work, gated on a
    ///     content-divergence check.
    ///     </para>
    /// </summary>
    private bool TryEncodeProvenScriptBearingOverride(
        string recordType,
        object model,
        Dictionary<uint, ParsedMainRecord> pcRecords,
        uint formId,
        PluginBuildOptions options,
        ConversionPipelineStats stats,
        out byte[] recordBytes)
    {
        recordBytes = [];
        if (!pcRecords.TryGetValue(formId, out var masterRecord))
        {
            return false;
        }

        if (recordType == "SCPT" && model is ScriptRecord script)
        {
            if (!HasMeaningfulScriptContent(script))
            {
                return false;
            }

            // Refuse to downgrade master's compiled bytecode. Proto captures often carry
            // populated Variables / ReferencedObjects but an empty (or truncated) CompiledData
            // because the proto build hadn't fully compiled the script yet — for VCGTutorialSCRIPT
            // (Doc Mitchell intro) the proto has 0 bytes vs master's 2151, and emitting the
            // override neuters the tutorial flow ("Travel onwards" → opens SPECIAL menu instead
            // of advancing). For VCG02SCRIPT (Sunny / Back in the Saddle) the proto has 4 bytes
            // vs master's 83, with the same downgrade effect. Strictly require our SCDA to be
            // at least as large as master's before allowing the override; if not, keep master's
            // mature script intact. Real fix is to investigate why the runtime reader produces
            // truncated bytecode for these scripts (separate from this stopgap).
            var protoCompiledSize = script.CompiledData?.Length ?? 0;
            var masterScdaIndex = masterRecord.Subrecords.FindIndex(s => s.Signature == "SCDA");
            var masterCompiledSize = masterScdaIndex >= 0 ? masterRecord.Subrecords[masterScdaIndex].Data.Length : 0;
            if (masterCompiledSize > 0 && protoCompiledSize < masterCompiledSize)
            {
                if (options.VerboseDecisions)
                {
                    _sink.Decision("Merging top-level records",
                        $"Refused SCPT 0x{formId:X8} override: proto SCDA {protoCompiledSize}B < master {masterCompiledSize}B — would downgrade compiled script.",
                        recordType, formId, "script.override-refused-downgrade");
                }

                return false;
            }

            var encoded = ScptEncoder.EncodeNew(
                script,
                BuildAllValidFormIdSet(),
                _newRecordSourceToAllocated.Count > 0 ? _newRecordSourceToAllocated : null);
            foreach (var warning in encoded.Warnings)
            {
                stats.Warnings++;
                _sink.Warn("Merging top-level records", warning, recordType, formId);
            }

            if (encoded.Subrecords.Count == 0 ||
                !ScriptRelevantSubrecordsDiffer(masterRecord, encoded.Subrecords))
            {
                return false;
            }

            recordBytes = PluginRecordByteBuilder.BuildOverrideRecordBytes(
                masterRecord, BuildSubrecordBytes(encoded.Subrecords), options);
            if (options.VerboseDecisions)
            {
                _sink.Decision("Merging top-level records",
                    "Emitted full SCPT override because DMP script bytecode/source/refs differ from master.",
                    recordType, formId, "script.override-proven-delta");
            }

            return true;
        }

        if (recordType == "QUST" && model is QuestRecord quest && quest.Script.HasValue)
        {
            var resolvedScript = FormIdReferenceResolver.Resolve(
                quest.Script.Value,
                BuildAllValidFormIdSet(),
                _newRecordSourceToAllocated.Count > 0 ? _newRecordSourceToAllocated : null);
            if (!resolvedScript.HasValue || resolvedScript.Value == 0)
            {
                return false;
            }

            var masterScriIndex = masterRecord.Subrecords.FindIndex(s =>
                s.Signature == "SCRI" && s.Data.Length >= 4);
            if (masterScriIndex < 0)
            {
                return false;
            }

            var masterScript = BinaryPrimitives.ReadUInt32LittleEndian(
                masterRecord.Subrecords[masterScriIndex].Data.AsSpan(0, 4));
            if (masterScript == resolvedScript.Value)
            {
                return false;
            }

            recordBytes = PluginRecordByteBuilder.BuildOverrideRecordBytes(
                masterRecord,
                BuildMasterSubrecordsWithReplacement(masterRecord, "SCRI", resolvedScript.Value),
                options);
            if (options.VerboseDecisions)
            {
                _sink.Decision("Merging top-level records",
                    $"Emitted QUST override replacing SCRI 0x{masterScript:X8} -> 0x{resolvedScript.Value:X8}.",
                    recordType, formId, "quest-script.override-proven-delta");
            }

            return true;
        }

        return false;
    }

    private HashSet<uint> BuildAllValidFormIdSet()
    {
        var allValidFormIdsUnion = new HashSet<uint>();
        if (_masterFormIds is not null) allValidFormIdsUnion.UnionWith(_masterFormIds);
        if (_masterChildFormIds is not null) allValidFormIdsUnion.UnionWith(_masterChildFormIds);
        allValidFormIdsUnion.UnionWith(_emittedNewFormIds);
        allValidFormIdsUnion.UnionWith(RuntimeStateRecordPolicy.EngineFormIds);
        return allValidFormIdsUnion;
    }

    private static bool HasMeaningfulScriptContent(ScriptRecord script)
    {
        return script.CompiledData is { Length: > 0 }
               || !string.IsNullOrWhiteSpace(script.SourceText)
               || script.Variables.Count > 0
               || script.ReferencedObjects.Count > 0;
    }

    private static bool ScriptRelevantSubrecordsDiffer(
        ParsedMainRecord masterRecord,
        IReadOnlyList<EncodedSubrecord> candidateSubrecords)
    {
        static bool Relevant(string signature)
        {
            return signature is "SCHR" or "SCDA" or "SCTX" or "SLSD" or "SCVR" or "SCRO" or "SCRV";
        }

        var masterRelevant = masterRecord.Subrecords
            .Where(s => Relevant(s.Signature))
            .Select(s => (s.Signature, Bytes: s.Data))
            .ToList();
        var candidateRelevant = candidateSubrecords
            .Where(s => Relevant(s.Signature))
            .Select(s => (s.Signature, Bytes: s.Bytes))
            .ToList();

        if (masterRelevant.Count != candidateRelevant.Count)
        {
            return true;
        }

        for (var i = 0; i < masterRelevant.Count; i++)
        {
            if (masterRelevant[i].Signature != candidateRelevant[i].Signature ||
                !masterRelevant[i].Bytes.AsSpan().SequenceEqual(candidateRelevant[i].Bytes))
            {
                return true;
            }
        }

        return false;
    }

    private static byte[] BuildSubrecordBytes(IReadOnlyList<EncodedSubrecord> subrecords)
    {
        using var subStream = new MemoryStream();
        using var writer = new BinaryWriter(subStream, Encoding.Latin1, true);
        foreach (var sub in subrecords)
        {
            SubrecordEncoder.WriteSubrecord(writer, sub.Signature, sub.Bytes);
        }

        return subStream.ToArray();
    }

    private static byte[] BuildMasterSubrecordsWithReplacement(
        ParsedMainRecord masterRecord,
        string signatureToReplace,
        uint formIdValue)
    {
        using var subStream = new MemoryStream();
        using var writer = new BinaryWriter(subStream, Encoding.Latin1, true);
        var replacement = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(replacement, formIdValue);

        foreach (var sub in masterRecord.Subrecords)
        {
            SubrecordEncoder.WriteSubrecord(
                writer,
                sub.Signature,
                sub.Signature == signatureToReplace ? replacement : sub.Data);
        }

        return subStream.ToArray();
    }

    /// <summary>
    ///     Dispatch a DMP model that lacks a master FormID through the appropriate type-specific
    ///     <c>EncodeNew</c> method. Types without a registered encoder fall through to a
    ///     "skipped" decision.
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
        // For NPC PKID validation we need (master PACK ∪ emitted PACK). PACK is enumerated
        // before NPC_ in EnumerateModelsByType so the emitted set is populated by the time
        // the NPC encoder asks for it. The remap table is _newRecordSourceToAllocated, the
        // converter-wide runtime→PC alias dictionary used by EncodedSubrecordFormIdRemapper.
        IReadOnlySet<uint>? validPackages = null;
        if (recordType == "NPC_")
        {
            var masterPacks = _masterFormIdsByType?.GetValueOrDefault("PACK");
            var emittedPacks = _emittedNewFormIdsByType.GetValueOrDefault("PACK");
            if (masterPacks is not null || emittedPacks is not null)
            {
                var union = new HashSet<uint>();
                if (masterPacks is not null) union.UnionWith(masterPacks);
                if (emittedPacks is not null) union.UnionWith(emittedPacks);
                validPackages = union;
            }
        }

        // Every encoder that takes a (validFormIds, remapTable) pair wants the full
        // master ∪ all-emitted-new union so it can distinguish dangling refs from legitimate
        // ones. The previous selective allow-list quietly fed null to encoders not on it —
        // most notably SCPT, where unresolved SCROs caused the engine to log
        // "Could not find referenced object … Script will not be executed" and refuse to run
        // the script. Always compute the union; the HashSet union is cheap and the
        // allow-list is too easy to forget to update when adding new validating encoders.
        var allValidFormIdsUnion = new HashSet<uint>();
        if (_masterFormIds is not null) allValidFormIdsUnion.UnionWith(_masterFormIds);
        if (_masterChildFormIds is not null) allValidFormIdsUnion.UnionWith(_masterChildFormIds);
        allValidFormIdsUnion.UnionWith(_emittedNewFormIds);
        // Engine-hardcoded refs (PlayerRef 0x14, game-time GLOBs, etc.) aren't in any
        // ESM record but scripts reference them constantly — without whitelisting,
        // SCRO validators would null them and break every script that touches the player.
        allValidFormIdsUnion.UnionWith(RuntimeStateRecordPolicy.EngineFormIds);
        IReadOnlySet<uint>? allValidFormIds = allValidFormIdsUnion;

        var context = new NewTopLevelRecordEncodingContext(
            _masterFormIds ?? new HashSet<uint>(),
            _emittedNewFormIdsByType.TryGetValue("STAT", out var statSet)
                ? statSet
                : new HashSet<uint>(),
            _masterNpcByRace,
            validPackages,
            _newRecordSourceToAllocated.Count > 0 ? _newRecordSourceToAllocated : null,
            allValidFormIds);
        try
        {
            encoded = NewTopLevelRecordEncoderDispatcher.TryEncode(recordType, model, context);
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
            // Type doesn't have a new-record path.
            if (options.VerboseDecisions)
            {
                _sink.Decision("Merging top-level records",
                    "New-record emission deferred for this type — skipped.",
                    recordType, ExtractFormId(model), $"skipped:new-{recordType}");
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

        var encodedSubrecords = RemapEncodedFormIds(recordType, encoded.Subrecords);
        if (recordType == "LTEX")
        {
            encodedSubrecords = ValidateLtexRefs(encodedSubrecords, ExtractFormId(model));
        }

        // Drop SCRI subrecords whose script FormID isn't in the master. The DMP often carries
        // SCRI references to FO3-vintage scripts that don't exist in the FNV master and would
        // cause the engine to log "Unable to find script (XXXXXXXX) on owner object" warnings
        // plus null-deref later during script-binding setup.
        var validatedSubrecords = ValidateScriRefs(encodedSubrecords, recordType, ExtractFormId(model));

        if (validatedSubrecords.Count == 0)
        {
            return false;
        }

        var dmpSourceFormId = ExtractFormId(model);
        var renderUnsafeNewNpc = recordType == "NPC_" && !NpcHasRenderableTemplate(validatedSubrecords);
        var allocatedFormId = allocator.Allocate();
        var flags = options.CompressRecords ? 0x00040000u : 0u;
        recordBytes =
            PluginRecordByteBuilder.BuildNewRecordBytes(recordType, allocatedFormId, flags, validatedSubrecords);

        if (dmpSourceFormId != 0 && dmpSourceFormId != allocatedFormId)
        {
            TrackNewRecordSourceAlias(recordType, dmpSourceFormId, allocatedFormId);
        }

        if (renderUnsafeNewNpc)
        {
            _renderUnsafeNewNpcFormIds.Add(allocatedFormId);
            if (dmpSourceFormId != 0)
            {
                _renderUnsafeNewNpcFormIds.Add(dmpSourceFormId);
            }

            stats.Warnings++;
            stats.IncrementDropReason("npc.render-unsafe-no-template");
            if (options.VerboseDecisions)
            {
                _sink.Decision("Merging top-level records",
                    $"New NPC_ 0x{dmpSourceFormId:X8} -> 0x{allocatedFormId:X8} has no master TPLT " +
                    "with UseTraits; generated ACHR placements using this base will be skipped to avoid " +
                    "missing-FaceGen renderer crashes.",
                    recordType,
                    dmpSourceFormId,
                    "npc.render-unsafe-no-template");
            }
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
    ///     Build cell-children bundles. Handles three cases:
    ///     1. Cell exists in PC ESM, ref exists in PC ESM → override
    ///     2. Cell exists in PC ESM, ref doesn't → new ref allocated under existing cell
    ///     3. Cell doesn't exist in PC ESM → new CELL with new refs as children
    ///     Plus, for master cells that get a replacement child GRUP, vanilla child refs
    ///     missing from sparse DMP captures are copied forward so the override does not
    ///     gut populated interiors.
    /// </summary>
    private List<CellOverrideBundle> BuildCellOverrideBundles(
        RecordCollection dmpRecords,
        Dictionary<uint, ParsedMainRecord> pcRecordsByFormId,
        Dictionary<uint, uint> refToCell,
        Dictionary<uint, MasterChildLocation> masterChildLocations,
        IReadOnlySet<uint> pcRefFormIds,
        Dictionary<uint, PcEsmCellContext> cellContexts,
        Dictionary<uint, List<uint>> navmsByCell,
        Dictionary<uint, List<uint>> landsByCell,
        FormIdAllocator allocator,
        Dictionary<string, byte[]> grupBytesByType,
        PluginBuildOptions options,
        ConversionPipelineStats stats,
        out List<NewNavmEntry> newNavmEntries,
        CancellationToken ct)
    {
        newNavmEntries = [];
        var bundles = new List<CellOverrideBundle>();
        var policy = SubrecordMergePolicy.Default;

        // Inverse of refToCell — gives us the master refs in each cell so we can compute
        // the set difference for deletion-flag synthesis.
        var cellToRefs = BuildCellToRefsIndex(refToCell);
        var actorBaseRemap = BuildDmpActorBaseMasterRemap(dmpRecords);

        // Union placements across all parser snapshots of each logical cell. The DMP
        // memory carver routinely produces multiple CellRecord captures for the same cell
        // (different runtime snapshots / mirrored memory regions), and the per-capture
        // PlacedObjects lists diverge. Without unioning, we'd emit only the FIRST capture's
        // placements and silently drop the rest — observed losing 20+ placements per cell
        // when secondary captures held additional content.
        var unionResult = CellCaptureUnioner.Union(dmpRecords.Cells);
        var mergedCells = unionResult.Cells;
        var reroutedChildRecordsByCell = new Dictionary<uint, CellChildRecordBuckets>();
        var staticDoorRepairsByTargetRef = new Dictionary<uint, DoorTeleportRepair>();
        if (unionResult.TotalUnionedPlacements > 0)
        {
            _sink.Info("Merging cell children",
                $"Multi-capture cell union: gained {unionResult.TotalUnionedPlacements:N0} placement(s) across " +
                $"{unionResult.CellGroupsWithMerges:N0} cell(s) (was first-capture-only).");
        }

        if (options.VerboseDecisions)
        {
            foreach (var diagnostic in unionResult.Diagnostics)
            {
                _sink.Decision("Merging cell children",
                    $"Cell {diagnostic.CellKey} unioned {diagnostic.CaptureCount:N0} captures: primary had " +
                    $"{diagnostic.PrimaryPlacementCount:N0}, union has {diagnostic.UnionPlacementCount:N0} " +
                    $"(+{diagnostic.AddedPlacementCount:N0} from secondary captures).",
                    "CELL", diagnostic.PrimaryFormId, "cell.capture-union");
            }
        }

        var landOverrideBuilder = new LandOverrideBuilder(_sink, RewriteLandTextureFormIds);

        // Re-parent OVERRIDE refs to their master parent cell when the DMP capture has them
        // under a different cell. The runtime engine attaches persistent refs (map markers,
        // quest anchors, named-location refs) to whichever grid cell is currently nearest the
        // player; master ESM has them in the worldspace's persistent-cell container (or a
        // different cell entirely). Without this pre-pass the prototype-captured DATA bytes
        // (position/rotation) get emitted under the wrong parent — two parent assignments at
        // load time crash the FNV renderer with a NiAlphaProperty access violation.
        mergedCells = RepartitionPlacementsByMasterParent(mergedCells, pcRecordsByFormId,
            refToCell, cellContexts, options);

        // Plan 1.4: pre-populate cell.LandVisualData with master-ESM LAND fallback at the
        // same (worldspace, grid) so the downstream cell loop reads the merged value
        // directly. Replaces the in-loop TryExtractMasterLandVisualData lookup and lets
        // any GUI/CLI consumer that loads master ESM see the same enriched model.
        mergedCells = EsmLandEnricher.EnrichCellsWithMasterEsmLandFallback(
            mergedCells.ToList(), _masterExteriorCellByGrid, landsByCell, pcRecordsByFormId);

        // Phase A — index DMP-captured NAVMs by their parent cell FormID and allocate
        // emitted FormIDs upfront. Allocating ahead of the per-cell loop guarantees that
        // NVEX cross-navmesh links (which reference other NAVMs) can be rewritten to
        // their emitted FormIDs regardless of cell emission order. NAVMs whose FormID
        // already exists in master flow through the master-preservation block instead.
        var dmpNavmsByCell = new Dictionary<uint, List<NavMeshRecord>>();
        var navmDmpToEmittedFormId = new Dictionary<uint, uint>();
        // Tracks NAVMs we actually emit in Phase B. Phase A allocates FormIDs for every
        // DMP NAVM with a parent cell + body, but the cell loop may skip some cells
        // (dedup, grid-collision redirect, etc.) — so an allocated FormID can remain
        // un-emitted, and any NVEX entry already rewritten to point at it dangles.
        // Used by the post-cell-loop NVEX sanitizer to drop dangling cross-NAVM links.
        var emittedNavmFormIds = new HashSet<uint>();
        foreach (var dmpNavm in dmpRecords.NavMeshes)
        {
            if (dmpNavm.CellFormId == 0
                || dmpNavm.RawSubrecords.Count == 0
                || pcRecordsByFormId.ContainsKey(dmpNavm.FormId))
            {
                continue;
            }
            if (!dmpNavmsByCell.TryGetValue(dmpNavm.CellFormId, out var list))
            {
                list = [];
                dmpNavmsByCell[dmpNavm.CellFormId] = list;
            }
            list.Add(dmpNavm);
            if (!navmDmpToEmittedFormId.ContainsKey(dmpNavm.FormId))
            {
                navmDmpToEmittedFormId[dmpNavm.FormId] = allocator.Allocate();
            }
        }
        if (dmpNavmsByCell.Count > 0)
        {
            _sink.Info("Merging cell children",
                $"DMP NAVM emission: indexed {dmpNavmsByCell.Values.Sum(l => l.Count):N0} new navmesh(es) " +
                $"across {dmpNavmsByCell.Count:N0} cell(s) (proto-only worldspaces + master-cell augmentation).");
        }

        // newNavmEntries (the out parameter, initialized at method entry) tracks every NAVM
        // we actually emit in Phase B. After the cell loop completes, the caller hands this
        // list to NavInfoMapBuilder to build a NAVI override record so the engine's
        // NavMeshInfoMap can resolve our new NAVM FormIDs — without it, plugin load crashes at
        // FalloutNV+0x0069E09A on the first cell that contains one of our NAVMs.

        foreach (var dmpCell in mergedCells)
        {
            ct.ThrowIfCancellationRequested();

            var pcCellExists = pcRecordsByFormId.TryGetValue(dmpCell.FormId, out var pcCellRecord)
                               && pcCellRecord!.Header.Signature == "CELL";

            // Grid-collision redirect: if the DMP cell's FormID isn't in master but master
            // already has an exterior cell at the same (worldspace, gridX, gridY), treat the
            // DMP cell as an override of master's cell. Without this redirect the FNV runtime
            // would see two cells claiming the same grid, destroy the duplicate, and orphan
            // every REFR we placed in it (which then crashes on render walk).
            uint? gridRedirectedToMasterFormId = null;
            if (!pcCellExists
                && !dmpCell.IsInterior
                && dmpCell.WorldspaceFormId.HasValue
                && dmpCell.GridX.HasValue
                && dmpCell.GridY.HasValue
                && _masterExteriorCellByGrid.TryGetValue(
                    (dmpCell.WorldspaceFormId.Value, dmpCell.GridX.Value, dmpCell.GridY.Value),
                    out var masterCellAtGrid)
                && pcRecordsByFormId.TryGetValue(masterCellAtGrid, out var masterCellRec)
                && masterCellRec!.Header.Signature == "CELL")
            {
                // Bug-2 guard: if another DMP cell already redirected to this same grid,
                // skip — emitting the same master cell anchor + children GRUP twice
                // produces duplicate top-level records that desync the engine's cell loader.
                var gridKey = (dmpCell.WorldspaceFormId.Value, dmpCell.GridX.Value, dmpCell.GridY.Value);
                if (_emittedExteriorCellCoords.ContainsKey(gridKey))
                {
                    stats.IncrementSkipped("CELL");
                    stats.IncrementDropReason("cell.grid-collision-redirect-dup");
                    if (options.VerboseDecisions)
                    {
                        _sink.Decision("Merging cell children",
                            $"DMP cell 0x{dmpCell.FormId:X8} at grid ({dmpCell.GridX}, {dmpCell.GridY}) " +
                            $"skipped — grid already redirected to master cell 0x{masterCellAtGrid:X8} " +
                            "by an earlier DMP cell.",
                            "CELL", dmpCell.FormId, "cell.grid-collision-redirect-dup");
                    }

                    continue;
                }

                _emittedExteriorCellCoords[gridKey] = masterCellAtGrid;
                pcCellRecord = masterCellRec;
                pcCellExists = true;
                gridRedirectedToMasterFormId = masterCellAtGrid;
                stats.IncrementDropReason("cell.grid-collision-redirect");

                if (options.VerboseDecisions)
                {
                    _sink.Decision("Merging cell children",
                        $"DMP cell 0x{dmpCell.FormId:X8} at grid ({dmpCell.GridX}, {dmpCell.GridY}) " +
                        $"redirected to override master cell 0x{masterCellAtGrid:X8} (same worldspace, " +
                        "same grid coords) — avoids duplicate-cell destruction at load time.",
                        "CELL", dmpCell.FormId, "cell.grid-collision-redirect");
                }
            }

            // Build the cell's anchor record bytes + GRUP context.
            byte[] cellRecordBytes;
            PcEsmCellContext context;
            uint emittedCellFormId;

            if (pcCellExists)
            {
                var contextLookupFormId = gridRedirectedToMasterFormId ?? dmpCell.FormId;

                // Bug-2 supplemental dedup: if a previous DMP cell already emitted an
                // override anchor + children GRUP for this master FormID (via grid-redirect
                // or direct match), don't emit it again. Two top-level CELL records with the
                // same FormID is malformed and crashes the FNV cell loader.
                if (_emittedOverrideCellFormIds.Contains(contextLookupFormId))
                {
                    stats.IncrementSkipped("CELL");
                    stats.IncrementDropReason("cell.override-formid-dup");
                    if (options.VerboseDecisions)
                    {
                        _sink.Decision("Merging cell children",
                            $"DMP cell 0x{dmpCell.FormId:X8} → master 0x{contextLookupFormId:X8} " +
                            "skipped — already emitted as override by an earlier DMP cell.",
                            "CELL", dmpCell.FormId, "cell.override-formid-dup");
                    }

                    continue;
                }

                _emittedOverrideCellFormIds.Add(contextLookupFormId);

                if (!cellContexts.TryGetValue(contextLookupFormId, out var existingContext))
                {
                    stats.IncrementSkipped("CELL");
                    _sink.Warn("Merging cell children",
                        "Cell has no master GRUP context — skipped.",
                        "CELL", dmpCell.FormId, "skipped:no-context");
                    continue;
                }

                // Cell-anchor merge: apply DMP-captured metadata (FULL name, water height,
                // lighting template, encounter zone, etc.) on top of master's anchor subrecords.
                // For grid-redirected overrides the DMP cell is logically a *different* cell
                // that happens to share grid coords — its metadata doesn't describe master's
                // cell, so retain master verbatim. For FormID-matched overrides the DMP did
                // observe the same cell, so its captures are the authoritative deltas.
                if (gridRedirectedToMasterFormId is null
                    && TryEncodeOverrideCellAnchor(dmpCell, pcCellRecord!, options, stats, out var mergedAnchorBytes))
                {
                    cellRecordBytes = mergedAnchorBytes;
                }
                else
                {
                    cellRecordBytes = CellGrupBuilder.ReconstructRecordBytes(pcCellRecord!);
                }
                context = existingContext;
                emittedCellFormId = gridRedirectedToMasterFormId ?? dmpCell.FormId;
            }
            else
            {
                // New cell — allocate a plugin-index FormID and synthesize record bytes via CellEncoder.
                if (_encoderRegistry.Get("CELL") is not { } cellEncoder)
                {
                    stats.IncrementSkipped("CELL");
                    _sink.Warn("Merging cell children",
                        "Registry missing CellEncoder — new cell skipped.",
                        "CELL", dmpCell.FormId, "skipped:no-cell-encoder");
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

            var masterChildLookupFormId = gridRedirectedToMasterFormId ?? dmpCell.FormId;

            // NEW cells (not in master) can't be "override merges" — there's no
            // master GRUP to retain temporaries from, so the only correct policy is to
            // emit every captured placement. The classifier's PersistentOnly mode would
            // otherwise drop all temporary placements (sidewalks, building geometry,
            // clutter) because *none* of a new cell's placements live in the master.
            // For override cells (cell exists in master) Classify's binary policy applies:
            // any non-persistent DMP ref flips the cell to LoadedReplacement; otherwise
            // persistent-only DMP captures stay in PersistentOnly merge mode.
            var mode = pcCellExists
                ? CellMerger.Classify(dmpCell, pcRefFormIds)
                : CellMergeMode.LoadedReplacement;

            if (options.VerboseDecisions && pcCellExists)
            {
                var masterChildRefCount = cellToRefs.TryGetValue(masterChildLookupFormId, out var refs)
                    ? refs.Count
                    : 0;
                _sink.Decision("Merging cell children",
                    $"Cell 0x{emittedCellFormId:X8} merge mode {mode}; master child refs {masterChildRefCount:N0}.",
                    "CELL", dmpCell.FormId, "cell.merge-mode");
            }

            // Diagnostic full-replace mode: when the user opts in AND the master cell exists
            // AND the DMP captured any placements for the cell, force LoadedReplacement so all
            // DMP placements emit (not just persistent ones). The per-ref preservation filter
            // passed to the deletion synthesizer below decides which master temporaries get
            // delete-marks vs which are retained.
            var replaceTemporaries = options.ReplaceCellTemporariesOnOverride
                                     && pcCellExists
                                     && dmpCell.PlacedObjects.Count > 0;
            if (replaceTemporaries)
            {
                mode = CellMergeMode.LoadedReplacement;
            }

            // Build a base-FormID → DMP placements index for the per-ref replacement check.
            // A master REFR is considered "replaced" by the DMP only when the DMP has a
            // placement of the same base record at the same world position (within
            // PositionMatchTolerance units). Master refs without such a same-base-at-same-spot
            // match are preserved during wipeout, since the DMP carries no replacement for
            // them — that's the difference between "DMP authoritatively removed this object"
            // and "DMP never had this object in memory in the first place" (the latter is
            // the common case for walls/floor/ceiling and other non-instantiated geometry).
            //
            // Note: we normalize the index key through _newRecordSourceToAllocated so DMP
            // placements pointing at base FormIDs we've freshly allocated under new plugin-
            // local IDs still match against master records that use the source ID.
            Dictionary<uint, List<PlacedReference>>? dmpPlacementsByBase = null;
            if (replaceTemporaries)
            {
                dmpPlacementsByBase = CellReplacementPreservationPolicy.BuildPlacementsByBase(
                    dmpCell.PlacedObjects,
                    _newRecordSourceToAllocated);
            }

            var persistentRecords = new List<byte[]>();
            var vwdRecords = new List<byte[]>();
            var temporaryRecords = new List<byte[]>();
            var dmpRefFormIdsInCell = new HashSet<uint>();
            var coveredMasterRefFormIdsInCell = new HashSet<uint>();

            // Walk every DMP placed object in this cell. The CellMerger.SelectOverrideRefs
            // filter is for OVERRIDE-ONLY mode; we also handle new refs, so we walk the full
            // list and decide per-ref.
            foreach (var placed in dmpCell.PlacedObjects)
            {
                stats.RecordsConsidered++;
                dmpRefFormIdsInCell.Add(placed.FormId);

                if (RuntimeStateRecordPolicy.IsRuntimeStateFormId(placed.FormId))
                {
                    stats.IncrementSkipped(placed.RecordType);
                    _sink.Decision("Merging cell children",
                        $"Skipping runtime-state ref 0x{placed.FormId:X8} — DMP captures live " +
                        "gameplay state for engine-reserved records.",
                        placed.RecordType, placed.FormId, "runtime-state.skip-ref");
                    continue;
                }

                var refIsInMaster = pcRecordsByFormId.ContainsKey(placed.FormId);
                var placedForEmit = !refIsInMaster
                    ? RemapNewPlacedActorBase(placed, actorBaseRemap, options)
                    : placed;

                if (!pcCellExists && refIsInMaster)
                {
                    // A DMP snapshot can attach an existing master ref to a synthetic
                    // runtime cell. Emitting that override under the new cell duplicates the
                    // FormID and breaks master-authored links such as door XTEL parent-cell
                    // resolution. Keep the master record in its original cell instead.
                    stats.IncrementSkipped(placed.RecordType);
                    stats.IncrementDropReason("refr.master-ref-in-new-cell");
                    if (options.VerboseDecisions)
                    {
                        _sink.Decision("Merging cell children",
                            $"Skipping existing master {placed.RecordType} 0x{placed.FormId:X8} captured " +
                            $"under new cell 0x{emittedCellFormId:X8}; master parent-cell ownership wins.",
                            placed.RecordType,
                            placed.FormId,
                            "refr.master-ref-in-new-cell");
                    }

                    continue;
                }

                var allowSparseMapMarkerOverride = false;
                if (mode == CellMergeMode.PersistentOnly
                    && refIsInMaster
                    && placedForEmit.IsMapMarker
                    && pcRecordsByFormId.TryGetValue(placed.FormId, out var sparseMasterRef))
                {
                    allowSparseMapMarkerOverride = MapMarkerDiffersFromMaster(placedForEmit, sparseMasterRef);
                }

                if (mode == CellMergeMode.PersistentOnly && refIsInMaster && !allowSparseMapMarkerOverride)
                {
                    // Sparse unloaded-cell captures are not authoritative for existing
                    // master refs: they often carry only persistent doors / markers and
                    // live-state teleport data. Preserve the master record verbatim in the
                    // child-GRUP preservation pass instead of overriding it with DMP bytes.
                    stats.IncrementSkipped(placed.RecordType);
                    stats.IncrementDropReason("refr.sparse-cell-master-preserved");
                    if (options.VerboseDecisions)
                    {
                        _sink.Decision("Merging cell children",
                            $"Preserving master {placed.RecordType} 0x{placed.FormId:X8} in sparse " +
                            $"cell 0x{emittedCellFormId:X8}; DMP persistent-only capture is not " +
                            "authoritative for existing refs.",
                            placed.RecordType,
                            placed.FormId,
                            "refr.sparse-cell-master-preserved");
                    }

                    continue;
                }

                if (allowSparseMapMarkerOverride && options.VerboseDecisions)
                {
                    _sink.Decision("Merging cell children",
                        $"Emitting sparse map-marker override 0x{placed.FormId:X8}; DMP name/type/position " +
                        "differs from master.",
                        placed.RecordType, placed.FormId, "map-marker.sparse-override");
                }

                // When a placed ref's base FormID is a DMP-source ID that we've
                // freshly emitted under a new plugin-local FormID, rewrite the BaseFormId
                // to point at the allocation. This must be type-aware: the source ID space
                // is not globally unique enough for actor refs to blindly accept any alias
                // target. An ACRE pointing at an ARMO base is fatal during FNV load.
                if (placedForEmit.BaseFormId != 0
                    && _newRecordSourceToAllocated.TryGetValue(
                        placedForEmit.BaseFormId, out var allocatedBase))
                {
                    if (TryRemapPlacedBaseAlias(placedForEmit, allocatedBase, out var remappedPlaced))
                    {
                        placedForEmit = remappedPlaced;
                    }
                    else
                    {
                        var aliasType = _newRecordSourceToAllocatedType.GetValueOrDefault(
                            placedForEmit.BaseFormId, "unknown");
                        stats.IncrementSkipped(placed.RecordType);
                        stats.IncrementDropReason("refr.base-alias-type-mismatch");
                        _sink.Decision("Merging cell children",
                            $"Skipping {placed.RecordType} 0x{placed.FormId:X8} — base " +
                            $"0x{placedForEmit.BaseFormId:X8} aliases to {aliasType} " +
                            $"0x{allocatedBase:X8}, which is not a valid base type for " +
                            $"{placed.RecordType}.",
                            placed.RecordType, placed.FormId, "refr.base-alias-type-mismatch");
                        continue;
                    }
                }

                if (!refIsInMaster
                    && placedForEmit.RecordType == "ACHR"
                    && _renderUnsafeNewNpcFormIds.Contains(placedForEmit.BaseFormId))
                {
                    stats.IncrementSkipped(placed.RecordType);
                    stats.IncrementDropReason("achr.render-unsafe-npc-base");
                    if (options.VerboseDecisions)
                    {
                        _sink.Decision("Merging cell children",
                            $"Skipping new ACHR 0x{placed.FormId:X8} — generated NPC_ base " +
                            $"0x{placedForEmit.BaseFormId:X8} has no render-safe master template/UseTraits.",
                            placed.RecordType,
                            placed.FormId,
                            "achr.render-unsafe-npc-base");
                    }

                    continue;
                }

                if (!refIsInMaster
                    && IsRuntimeStructuralMarkerPlacement(
                        placedForEmit,
                        pcRecordsByFormId,
                        out var structuralMarkerBaseEditorId))
                {
                    stats.IncrementSkipped(placed.RecordType);
                    stats.IncrementDropReason("refr.runtime-structural-marker");
                    if (options.VerboseDecisions)
                    {
                        _sink.Decision("Merging cell children",
                            $"Skipping new REFR 0x{placed.FormId:X8} — structural marker base " +
                            $"0x{placedForEmit.BaseFormId:X8} \"{structuralMarkerBaseEditorId}\" is an " +
                            "authored-cell marker, not a renderable runtime placement.",
                            placed.RecordType,
                            placed.FormId,
                            "refr.runtime-structural-marker");
                    }

                    continue;
                }

                if (placedForEmit.BaseFormId != 0
                    && !IsValidPlacedBaseForOutput(
                        placedForEmit, pcRecordsByFormId, out var knownBaseRecordType))
                {
                    // Phase C: last-chance EditorID-stem remap for NEW refs only. The
                    // prototype's base FormID doesn't exist in master and isn't being emitted,
                    // but if a master record of the same kind has a matching normalized
                    // EditorID stem (e.g. SCOLParkingLotChunk03 → master SCOLParkingLotChunk03b),
                    // rewrite the REFR's NAME to point at that master record and keep the
                    // placement. Overrides with invalid DMP base changes are skipped so the
                    // master reference remains intact.
                    if (!refIsInMaster
                        && knownBaseRecordType is null
                        && options.EnableRefrBaseEditorIdRemap
                        && TryRemapRefrBaseByEditorIdStem(placed, placedForEmit, stats, out var remapped)
                        && IsValidPlacedBaseForOutput(remapped, pcRecordsByFormId, out _))
                    {
                        placedForEmit = remapped;
                    }
                    else
                    {
                        var reason = knownBaseRecordType is null
                            ? "refr.dangling-base"
                            : "refr.base-type-mismatch";
                        var message = knownBaseRecordType is null
                            ? $"base 0x{placedForEmit.BaseFormId:X8} is not in master ESM and not " +
                              "freshly emitted (FO3-vintage / deleted in released FNV)."
                            : $"base 0x{placedForEmit.BaseFormId:X8} is {knownBaseRecordType}, " +
                              $"which is not a valid base type for {placedForEmit.RecordType}.";

                        stats.IncrementSkipped(placed.RecordType);
                        stats.IncrementDropReason(reason);
                        _sink.Decision("Merging cell children",
                            $"Skipping {placed.RecordType} 0x{placed.FormId:X8} — {message}",
                            placed.RecordType, placed.FormId, reason);
                        continue;
                    }
                }

                // Mode A (PersistentOnly): skip non-persistent overrides — DMP didn't actually
                // load this cell, so we can't trust temporary refs. New persistent refs are still
                // allowed (user spec). Map markers are intrinsically persistent (the Pip-Boy world
                // map data is always loaded regardless of cell visit state), but proto captures do
                // not always set the IsPersistent flag on REFR map-marker records — the engine
                // synthesizes that classification at run time. Exempt them from the filter so new
                // map markers reach the new-ref encoder; without this exemption they're dropped
                // here before `TryEncodeNewRef` ever sees them (the v52-xex44 "locations renamed
                // but new markers missing" symptom).
                if (mode == CellMergeMode.PersistentOnly && !placed.IsPersistent && !placedForEmit.IsMapMarker)
                {
                    continue;
                }

                uint? allocatedNewRefFormId = null;
                if (!refIsInMaster)
                {
                    // Prefer the FormID Phase 0 (PreAllocateNewPlacedRefFormIds) reserved for
                    // this placed ref so Phase 3 encoders (PACK PLDT etc.) and Phase 4 emission
                    // agree. Fallback to a fresh allocator slot for refs that appeared post-
                    // Phase-0 (shouldn't happen for DMP-origin refs; defensive).
                    allocatedNewRefFormId = _preAllocatedRefFormIds.TryGetValue(placed.FormId, out var preId)
                        ? preId
                        : allocator.Allocate();
                    if (!TryRepairStaticDoorTeleport(
                            placedForEmit,
                            allocatedNewRefFormId.Value,
                            pcRecordsByFormId,
                            masterChildLocations,
                            reroutedChildRecordsByCell,
                            grupBytesByType,
                            staticDoorRepairsByTargetRef,
                            allocator,
                            options,
                            stats,
                            out placedForEmit))
                    {
                        placedForEmit = SanitizeNewRefTeleport(
                            placedForEmit,
                            pcRecordsByFormId,
                            options,
                            stats);
                    }
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
                    // Bug-1 guard: for OVERRIDE refs, master is the source of truth for the
                    // parent-cell relationship. The DMP runtime occasionally attaches
                    // persistent refs (map markers, quest anchors, named-location refs) to
                    // the grid cell the player is in, while master has them in the
                    // worldspace's persistent-cell container. Emitting the override under
                    // the DMP cell rather than master's parent produces two competing parent
                    // assignments at load time — and the FNV renderer crashes the next time
                    // it walks the ref's BSFadeNode through its NiAlphaProperty. Skip the
                    // mismatched-parent emit; master's data for this ref is unchanged.
                    var masterParentCell = refToCell.GetValueOrDefault(placed.FormId);
                    if (masterParentCell != 0
                        && masterParentCell != emittedCellFormId
                        && !placedForEmit.IsMapMarker)
                    {
                        stats.IncrementSkipped(placed.RecordType);
                        stats.IncrementDropReason("refr.parent-cell-mismatch");
                        if (options.VerboseDecisions)
                        {
                            _sink.Decision("Merging cell children",
                                $"Skipping override of {placed.RecordType} 0x{placed.FormId:X8} — " +
                                $"master parent is cell 0x{masterParentCell:X8}, but DMP captured it " +
                                $"under cell 0x{emittedCellFormId:X8}. Emitting in the wrong cell " +
                                "produces a duplicate parent assignment that crashes the renderer.",
                                placed.RecordType, placed.FormId, "refr.parent-cell-mismatch");
                        }

                        continue;
                    }

                    // Existing ref — override path.
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
                    // New ref — full-record path.
                    if (!TryEncodeNewRef(
                            placedForEmit,
                            allocatedNewRefFormId!.Value,
                            options,
                            stats,
                            out var bytes))
                    {
                        continue;
                    }

                    recordBytes = bytes;
                    stats.NewRecordsEmitted++;
                }

                var targetCellFormId = emittedCellFormId;
                var targetGroupType = placed.IsPersistent ? 8 : 9;
                if (refExistsInPc
                    && masterChildLocations.TryGetValue(placed.FormId, out var masterChildLocation))
                {
                    targetCellFormId = masterChildLocation.CellFormId;
                    targetGroupType = masterChildLocation.GroupType is 8 or 9 or 10
                        ? masterChildLocation.GroupType
                        : targetGroupType;
                }

                if (targetCellFormId == emittedCellFormId)
                {
                    AddChildRecordToBuckets(persistentRecords, vwdRecords, temporaryRecords, targetGroupType,
                        recordBytes);
                    if (refExistsInPc)
                    {
                        coveredMasterRefFormIdsInCell.Add(placed.FormId);
                    }
                }
                else
                {
                    AddReroutedChildRecord(reroutedChildRecordsByCell, targetCellFormId, targetGroupType,
                        recordBytes);

                    if (options.VerboseDecisions)
                    {
                        _sink.Decision("Merging cell children",
                            $"Rerouted existing {placed.RecordType} 0x{placed.FormId:X8} override from " +
                            $"captured cell 0x{emittedCellFormId:X8} to master parent cell " +
                            $"0x{targetCellFormId:X8}.",
                            placed.RecordType, placed.FormId, "cell.child-reroute-master-parent");
                    }
                }

                stats.IncrementEmitted(placed.RecordType);
            }

            var hasCapturedTerrain = !dmpCell.IsInterior &&
                                     (dmpCell.Heightmap != null ||
                                      dmpCell.RuntimeTerrainMesh != null ||
                                      dmpCell.LandVisualData?.HasAny == true);

            // Sparse/merge cells with a master: preserve master refs that were not
            // successfully covered by emitted overrides. Grid-redirected sparse cells can
            // classify as Skip because none of their refs are master-owned, but if we emitted
            // any new sparse child records, the master children still need to be copied so the
            // plugin augments the cell instead of replacing it with only the new refs.
            var hasEmittedDmpChildRecords =
                persistentRecords.Count > 0 || vwdRecords.Count > 0 || temporaryRecords.Count > 0;
            if ((mode != CellMergeMode.Skip || hasCapturedTerrain || hasEmittedDmpChildRecords)
                && pcCellExists
                && cellToRefs.TryGetValue(masterChildLookupFormId, out var masterRefIds))
            {
                var masterRefs = masterRefIds
                    .Select(id => pcRecordsByFormId.GetValueOrDefault(id))
                    .Where(r => r is not null)
                    .ToList()!;

                if (replaceTemporaries)
                {
                    // In diagnostic full-replace mode, use a per-ref position-matching
                    // filter for the deletion synthesizer so only refs with an authoritative
                    // DMP replacement are wiped. Missing runtime refs are preserved by the
                    // filter because sparse captures are not a complete cell inventory.
                    // Per-ref position-matching filter. A master ref is wipe-eligible only
                    // when the DMP carries a placement of the *same base record* at the
                    // *same world position* (within PositionMatchTolerance). Master refs
                    // with no such same-base+same-spot replacement are preserved — that's
                    // the difference between "DMP authoritatively replaced this object" and
                    // "DMP never had this object in memory". The latter is the common case
                    // for walls/floor/ceiling/clutter the engine didn't instantiate
                    // TESObjectREFRs for, and wiping them leaves interiors gutted.
                    var placementsByBase = dmpPlacementsByBase
                                           ?? new Dictionary<uint, List<PlacedReference>>();
                    var preserveFilter = CellReplacementPreservationPolicy.CreatePreserveFilter(placementsByBase);
                    var deleted = DeletedRefSynthesizer.Synthesize(
                        masterRefs!, dmpRefFormIdsInCell, preserveFilter);
                    persistentRecords.AddRange(deleted.Persistent);
                    temporaryRecords.AddRange(deleted.Temporary);

                    if (deleted.Persistent.Count + deleted.Temporary.Count > 0 && options.VerboseDecisions)
                    {
                        _sink.Decision("Merging cell children",
                            $"Full-replace: {deleted.Persistent.Count} persistent + {deleted.Temporary.Count} " +
                            "temporary master refs marked deleted via per-ref base+position match " +
                            $"(tolerance {CellReplacementPreservationPolicy.PositionMatchTolerance:F0}u).",
                            "CELL", dmpCell.FormId, "cell.full-replace-on-override");
                    }
                }
                else
                {
                    int preservedMasterRefs;
                    string preservationReason;
                    if (mode == CellMergeMode.LoadedReplacement)
                    {
                        var hasAuthoritativeDmpStructuralRefs = dmpCell.PlacedObjects.Any(
                            placed => placed.StructuralData is { HasAny: true });
                        preservedMasterRefs = CellStructuralReferencePreserver.PreserveLoadedReplacementMissing(
                            masterRefs!, coveredMasterRefFormIdsInCell, masterChildLocations,
                            pcRecordsByFormId, persistentRecords, vwdRecords, temporaryRecords, stats,
                            hasAuthoritativeDmpStructuralRefs);
                        preservationReason = "script-critical/structural";

                        var deleted = DeletedRefSynthesizer.Synthesize(
                            masterRefs!,
                            coveredMasterRefFormIdsInCell,
                            masterRef =>
                                (!hasAuthoritativeDmpStructuralRefs ||
                                 !CellStructuralReferencePreserver.IsStructuralCellRef(masterRef, pcRecordsByFormId))
                                && CellStructuralReferencePreserver.ShouldPreserveInLoadedReplacement(
                                    masterRef, pcRecordsByFormId));
                        persistentRecords.AddRange(deleted.Persistent);
                        temporaryRecords.AddRange(deleted.Temporary);
                        var deletedCount = deleted.Persistent.Count + deleted.Temporary.Count;
                        if (deletedCount > 0)
                        {
                            stats.OverridesEmitted += deletedCount;
                            foreach (var _ in deleted.Persistent.Concat(deleted.Temporary))
                            {
                                stats.IncrementEmitted("REFR");
                            }

                            if (options.VerboseDecisions)
                            {
                                _sink.Decision("Merging cell children",
                                    $"Loaded replacement deleted {deletedCount:N0} ordinary missing master temp ref(s); " +
                                    $"preserved {preservedMasterRefs:N0} {preservationReason} ref(s).",
                                    "CELL", dmpCell.FormId, "cell.loaded-replacement-deletions");
                            }
                        }
                    }
                    else
                    {
                        // PersistentOnly mode: the DMP only captured persistent refs for this
                        // cell, so nothing we've emitted covers a master temporary. Preserve
                        // every missing master ref (treats the cell's temporary content as
                        // untouched by the DMP snapshot).
                        preservedMasterRefs = CellStructuralReferencePreserver.PreserveAllMissing(
                            masterRefs!, new HashSet<uint>(), masterChildLocations,
                            persistentRecords, vwdRecords, temporaryRecords, stats);
                        preservationReason = "vanilla";
                    }

                    if (preservedMasterRefs > 0 && options.VerboseDecisions)
                    {
                        _sink.Decision("Merging cell children",
                            $"Preserved {preservedMasterRefs} {preservationReason} child ref(s) missing " +
                            "from DMP cell snapshot.",
                            "CELL", dmpCell.FormId, "cell.master-children-preserved");
                    }
                }
            }

            if (persistentRecords.Count == 0
                && vwdRecords.Count == 0
                && temporaryRecords.Count == 0
                && pcCellExists
                && !hasCapturedTerrain)
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
                    && landsByCell.TryGetValue(masterChildLookupFormId, out var masterLandFormIds)
                    && masterLandFormIds.Count > 0)
                {
                    masterLandFormId = masterLandFormIds[0];
                }

                ParsedMainRecord? masterLandRecord = null;
                LandHeightmap? masterLandHeightmap = null;
                if (masterLandFormId.HasValue)
                {
                    pcRecordsByFormId.TryGetValue(masterLandFormId.Value, out masterLandRecord);
                    if (masterLandRecord is not null)
                    {
                        // Plan 1.4: master visual data is pre-merged into dmpCell.LandVisualData
                        // by EsmLandEnricher.EnrichCellsWithMasterEsmLandFallback above; only
                        // the heightmap fallback still flows through this call site (flat-
                        // override rejection in LandOverrideBuilder needs the master baseline
                        // separately).
                        masterLandHeightmap = TryExtractMasterLandHeightmap(masterLandRecord);
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
                        masterVisualData: null,
                        masterLandHeightmap))
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
                            "LAND", masterLandFormId.Value, "land.preserved");
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
                && navmsByCell.TryGetValue(masterChildLookupFormId, out var masterNavmFormIds))
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
                            "CELL", dmpCell.FormId, "navm.preserved");
                    }
                }
            }

            // Phase B — emit DMP-captured NAVMs whose CellFormId points at this cell.
            // Covers two cases: cells in proto-only worldspaces (no master NAVMs at all)
            // and master-cell augmentation (DMP captured a navmesh master doesn't have).
            // Phase A pre-allocated the emitted FormIDs so NVEX cross-references can
            // resolve across cells in this emission batch.
            //
            // STOPGAP GATE: drop master-cell augmentation by default. Adding a second navmesh
            // on top of vanilla's may confuse AI pathing (idle NPCs flip-flopping to the
            // crucified animation every few seconds — same symptom as the original INFO
            // emission bug). Opt in via PluginBuildOptions.EmitMasterCellNavmAugmentation
            // for smoke testing; the flag stays default-false until xenia smoke confirms
            // the symptom doesn't return.
            var skipMasterCellNavmAugmentation = !options.EmitMasterCellNavmAugmentation;
            var cellIsNew = (dmpCell.FormId & 0xFF000000) == 0x01000000
                            || !pcRecordsByFormId.ContainsKey(dmpCell.FormId);
            if (skipMasterCellNavmAugmentation
                && !cellIsNew
                && dmpNavmsByCell.TryGetValue(dmpCell.FormId, out var skippedAugList))
            {
                var skipped = skippedAugList.Count;
                for (var s = 0; s < skipped; s++)
                {
                    stats.IncrementSkipped("NAVM");
                }
                if (options.VerboseDecisions)
                {
                    _sink.Decision("Merging cell children",
                        $"Master-cell NAVM augmentation gated: dropped {skipped} new NAVM(s) " +
                        $"in master cell 0x{dmpCell.FormId:X8}.",
                        "CELL", dmpCell.FormId, "navm.master-cell-aug-gated");
                }
                dmpNavmsByCell.Remove(dmpCell.FormId);
            }

            if (dmpNavmsByCell.TryGetValue(dmpCell.FormId, out var dmpNavmsForCell))
            {
                var emittedNavmCount = 0;
                foreach (var dmpNavm in dmpNavmsForCell)
                {
                    if (!navmDmpToEmittedFormId.TryGetValue(dmpNavm.FormId, out var newNavmFormId))
                    {
                        continue;
                    }
                    var patched = NavMeshByteRewriter.Rewrite(
                        dmpNavm.RawSubrecords, emittedCellFormId, navmDmpToEmittedFormId);
                    var navmBytes = PluginRecordByteBuilder.BuildNewRecordBytes(
                        "NAVM", newNavmFormId, flags: 0u, patched);
                    temporaryPrefixRecords.Add(navmBytes);
                    emittedNavmFormIds.Add(newNavmFormId);

                    // Capture the data NavInfoMapBuilder needs for the matching NVMI entry.
                    // NVVX is the only subrecord with the vertices used to compute the
                    // approximate centroid; if absent the builder falls back to grid-center.
                    var nvvxBytes = dmpNavm.RawSubrecords
                        .FirstOrDefault(s => s.Signature == "NVVX").Bytes;
                    // LocationFormId: per master's NVMI convention (verified against NAVM
                    // 0x00136567 → 0x000DA726 WastelandNV), this is the parent worldspace
                    // for exterior cells (or cell FormID for interior). For new worldspaces
                    // we must use the EMITTED FormID, not the DMP-source FormID — otherwise
                    // the engine looks for a non-existent FormID and crashes in
                    // NavMeshInfoMap setup at FalloutNV+0x0069DFDC.
                    uint locationFid;
                    if (dmpCell.IsInterior)
                    {
                        locationFid = emittedCellFormId;
                    }
                    else if (dmpCell.WorldspaceFormId.HasValue
                             && _newWorldspacesForCellPipeline is not null
                             && _newWorldspacesForCellPipeline.TryGetValue(dmpCell.WorldspaceFormId.Value, out var newWrld))
                    {
                        // New worldspace: remap DMP-source → emitted FormID.
                        locationFid = newWrld.EmittedFormId;
                    }
                    else if (dmpCell.WorldspaceFormId.HasValue)
                    {
                        // Master worldspace: DMP-source IS the master FormID.
                        locationFid = dmpCell.WorldspaceFormId.Value;
                    }
                    else
                    {
                        locationFid = emittedCellFormId;
                    }
                    newNavmEntries.Add(new NewNavmEntry(
                        NavmFormId: newNavmFormId,
                        LocationFormId: locationFid,
                        IsInterior: dmpCell.IsInterior,
                        GridX: dmpCell.IsInterior ? (short)0 : (short)(dmpCell.GridX ?? 0),
                        GridY: dmpCell.IsInterior ? (short)0 : (short)(dmpCell.GridY ?? 0),
                        NvvxBytes: nvvxBytes));

                    emittedNavmCount++;
                    stats.IncrementEmitted("NAVM");
                    stats.NewRecordsEmitted++;
                }

                if (emittedNavmCount > 0 && options.VerboseDecisions)
                {
                    _sink.Decision("Merging cell children",
                        $"Emitted {emittedNavmCount} DMP NAVM record(s) in cell 0x{emittedCellFormId:X8} " +
                        $"(DMP source cell 0x{dmpCell.FormId:X8}).",
                        "CELL", emittedCellFormId, "navm.dmp-emitted");
                }
            }

            if (temporaryPrefixRecords.Count > 0)
            {
                temporaryRecords.InsertRange(0, temporaryPrefixRecords);
            }

            if (persistentRecords.Count == 0 && vwdRecords.Count == 0 && temporaryRecords.Count == 0)
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
                VwdChildRecords = vwdRecords,
                TemporaryChildRecords = temporaryRecords
            });

            stats.CellsMerged++;
        }

        foreach (var (cellFormId, buckets) in reroutedChildRecordsByCell)
        {
            if (buckets.IsEmpty)
            {
                continue;
            }

            if (!pcRecordsByFormId.TryGetValue(cellFormId, out var cellRecord)
                || cellRecord.Header.Signature != "CELL"
                || !cellContexts.TryGetValue(cellFormId, out var context))
            {
                stats.IncrementSkipped("CELL");
                _sink.Warn("Merging cell children",
                    $"Rerouted child override targets master cell 0x{cellFormId:X8}, but no CELL context was available.",
                    "CELL", cellFormId, "skipped:rerouted-child-no-context");
                continue;
            }

            bundles.Add(new CellOverrideBundle
            {
                CellFormId = cellFormId,
                Context = context,
                CellRecordBytes = CellGrupBuilder.ReconstructRecordBytes(cellRecord),
                PersistentChildRecords = buckets.Persistent,
                VwdChildRecords = buckets.Vwd,
                TemporaryChildRecords = buckets.Temporary
            });
        }

        SanitizeNvexInBundles(bundles, emittedNavmFormIds);

        return CoalesceCellOverrideBundles(FilterInvalidPlacedChildRecords(bundles, stats));
    }

    /// <summary>
    ///     Post-cell-loop pass: strips NVEX entries from emitted NAVM records whose target
    ///     NAVM FormID doesn't exist as either a master NAVM or one we actually emitted.
    ///     Catches both dangling shapes seen in xex4: NVEX targets in the master range whose
    ///     FormID isn't actually in master, and allocator-issued FormIDs for NAVMs whose
    ///     parent cell ended up skipped (so the rewrite landed but the NAVM was never written).
    ///     Either dangling shape crashes the engine in NavMeshInfoMap setup.
    /// </summary>
    private void SanitizeNvexInBundles(
        List<CellOverrideBundle> bundles,
        HashSet<uint> emittedNavmFormIds)
    {
        if (bundles.Count == 0)
        {
            return;
        }

        var validTargets = new HashSet<uint>(emittedNavmFormIds);
        if (_masterFormIdsByType is not null
            && _masterFormIdsByType.TryGetValue("NAVM", out var masterNavms))
        {
            foreach (var fid in masterNavms)
            {
                validTargets.Add(fid);
            }
        }

        var totalDropped = 0;
        var sanitizedRecords = 0;
        for (var b = 0; b < bundles.Count; b++)
        {
            var bundle = bundles[b];
            var bundleChanged = false;
            var newTemp = new List<byte[]>(bundle.TemporaryChildRecords.Count);
            foreach (var rec in bundle.TemporaryChildRecords)
            {
                if (rec.Length < 4 || rec[0] != (byte)'N' || rec[1] != (byte)'A'
                    || rec[2] != (byte)'V' || rec[3] != (byte)'M')
                {
                    newTemp.Add(rec);
                    continue;
                }
                var sanitized = NavMeshByteRewriter.SanitizeNvexInNavmRecord(
                    rec, validTargets, out var dropped);
                newTemp.Add(sanitized);
                // SanitizeNvexInNavmRecord can mutate the record without dropping any NVEX
                // entries — e.g. when DATA.EdgeLinkCount is stale relative to the (possibly
                // zero) NVEX entries actually present (xex2 NAVM 0x01003F41: DATA.edgeCnt=82
                // but no NVEX subrecord). Detect a byte-level swap so the corrected record
                // actually replaces the original.
                if (!ReferenceEquals(sanitized, rec))
                {
                    bundleChanged = true;
                    if (dropped > 0)
                    {
                        totalDropped += dropped;
                        sanitizedRecords++;
                    }
                }
            }
            if (bundleChanged)
            {
                bundles[b] = bundle with { TemporaryChildRecords = newTemp };
            }
        }

        if (totalDropped > 0)
        {
            _sink.Info("Merging cell children",
                $"NVEX sanitization: dropped {totalDropped:N0} dangling cross-NAVM entry/entries " +
                $"across {sanitizedRecords:N0} NAVM record(s) (targets pointed at master FormIDs " +
                "not actually in master, or at allocated-but-unemitted new FormIDs).",
                code: "navm.nvex-sanitized");
        }
    }

    private List<CellOverrideBundle> FilterInvalidPlacedChildRecords(
        List<CellOverrideBundle> bundles,
        ConversionPipelineStats stats)
    {
        var filtered = new List<CellOverrideBundle>(bundles.Count);
        foreach (var bundle in bundles)
        {
            var persistent = FilterInvalidPlacedChildRecords(bundle.PersistentChildRecords, stats);
            var vwd = FilterInvalidPlacedChildRecords(bundle.VwdChildRecords, stats);
            var temporary = FilterInvalidPlacedChildRecords(bundle.TemporaryChildRecords, stats);

            if (persistent.Count == bundle.PersistentChildRecords.Count
                && vwd.Count == bundle.VwdChildRecords.Count
                && temporary.Count == bundle.TemporaryChildRecords.Count)
            {
                filtered.Add(bundle);
                continue;
            }

            if (persistent.Count == 0 && vwd.Count == 0 && temporary.Count == 0)
            {
                continue;
            }

            filtered.Add(bundle with
            {
                PersistentChildRecords = persistent,
                VwdChildRecords = vwd,
                TemporaryChildRecords = temporary
            });
        }

        return filtered;
    }

    private List<byte[]> FilterInvalidPlacedChildRecords(
        IReadOnlyList<byte[]> records,
        ConversionPipelineStats stats)
    {
        var filtered = new List<byte[]>(records.Count);
        foreach (var recordBytes in records)
        {
            if (ShouldDropInvalidPlacedChildRecord(recordBytes, stats))
            {
                continue;
            }

            filtered.Add(recordBytes);
        }

        return filtered;
    }

    private bool ShouldDropInvalidPlacedChildRecord(byte[] recordBytes, ConversionPipelineStats stats)
    {
        var header = EsmParser.ParseRecordHeader(recordBytes);
        if (header is null || header.Signature is not ("REFR" or "ACHR" or "ACRE"))
        {
            return false;
        }

        if (_masterChildFormIds?.Contains(header.FormId) == true)
        {
            return false;
        }

        if (recordBytes.Length < EsmParser.MainRecordHeaderSize + header.DataSize)
        {
            return false;
        }

        var recordData = recordBytes.AsSpan(EsmParser.MainRecordHeaderSize, (int)header.DataSize);
        byte[] subrecordBytes;
        if ((header.Flags & EsmParser.CompressedFlag) != 0)
        {
            subrecordBytes = EsmParser.DecompressRecordData(recordData, false) ?? [];
        }
        else
        {
            subrecordBytes = recordData.ToArray();
        }

        if (subrecordBytes.Length == 0)
        {
            return false;
        }

        var name = EsmParser.ParseSubrecords(subrecordBytes)
            .FirstOrDefault(static s => s.Signature == "NAME" && s.Data.Length >= 4);
        if (name is null)
        {
            return false;
        }

        var baseFormId = BinaryPrimitives.ReadUInt32LittleEndian(name.Data.AsSpan(0, 4));
        if (baseFormId == 0 || baseFormId == 0xFFFFFFFFu)
        {
            return false;
        }

        if (IsValidPlacedBaseFormIdForOutput(header.Signature, baseFormId, out var knownBaseRecordType))
        {
            return false;
        }

        var reason = knownBaseRecordType is null
            ? "refr.dangling-base"
            : "refr.base-type-mismatch";
        var message = knownBaseRecordType is null
            ? $"base 0x{baseFormId:X8} is not in master ESM and not freshly emitted."
            : $"base 0x{baseFormId:X8} is {knownBaseRecordType}, which is not a valid base type for " +
              $"{header.Signature}.";

        stats.IncrementSkipped(header.Signature);
        stats.IncrementDropReason(reason);
        _sink.Decision("Merging cell children",
            $"Dropping final {header.Signature} 0x{header.FormId:X8} child record — {message}",
            header.Signature,
            header.FormId,
            reason);
        return true;
    }

    /// <summary>
    ///     Re-partition DMP-captured placements so OVERRIDE refs land under the same parent
    ///     cell master uses for them. The runtime engine attaches persistent refs (map
    ///     markers, quest anchors, named-location refs) to whichever grid cell is currently
    ///     near the player; master ESM has them in the worldspace's persistent-cell
    ///     container or a different exterior cell entirely. Emitting the override under the
    ///     DMP-captured cell creates a duplicate parent assignment at load time which
    ///     crashes the FNV renderer.
    ///
    ///     This pre-pass keeps each placement's captured DATA bytes (position/rotation)
    ///     intact but moves it from the DMP-cell bucket into the master-parent-cell bucket.
    ///     If the master parent cell isn't present in the input list, a synthetic CellRecord
    ///     entry is added so the main loop emits its cell-children GRUP. For new (DMP-only)
    ///     refs (those not in master), the original DMP cell stays the parent — the
    ///     reroute applies to OVERRIDE refs only.
    /// </summary>
    private List<CellRecord> RepartitionPlacementsByMasterParent(
        IReadOnlyList<CellRecord> mergedCells,
        Dictionary<uint, ParsedMainRecord> pcRecordsByFormId,
        Dictionary<uint, uint> refToCell,
        Dictionary<uint, PcEsmCellContext> cellContexts,
        PluginBuildOptions options)
    {
        // Resolve grid-redirect targets up-front so the master-parent comparison uses the
        // effective cell each DMP entry maps to (DMP FormID → master cell at same grid).
        var gridRedirectTargets = new Dictionary<uint, uint>();
        foreach (var dmpCell in mergedCells)
        {
            if (pcRecordsByFormId.ContainsKey(dmpCell.FormId) || dmpCell.IsInterior
                || !dmpCell.WorldspaceFormId.HasValue || !dmpCell.GridX.HasValue
                || !dmpCell.GridY.HasValue)
            {
                continue;
            }

            if (_masterExteriorCellByGrid.TryGetValue(
                    (dmpCell.WorldspaceFormId.Value, dmpCell.GridX.Value, dmpCell.GridY.Value),
                    out var redirectTo))
            {
                gridRedirectTargets[dmpCell.FormId] = redirectTo;
            }
        }

        // Bucket placements by their *correct* target cell.
        var refsByTargetCell = new Dictionary<uint, List<PlacedReference>>();
        var moveCount = 0;
        var skipDuplicatePersistentCount = 0;
        var emittedPersistentByCellAndFormId = new Dictionary<(uint Cell, uint FormId), bool>();

        foreach (var dmpCell in mergedCells)
        {
            // Effective DMP cell FormID after grid-redirect.
            var effectiveFormId = gridRedirectTargets.GetValueOrDefault(dmpCell.FormId, dmpCell.FormId);

            foreach (var placed in dmpCell.PlacedObjects)
            {
                var targetCellFormId = effectiveFormId;

                // OVERRIDE refs (in master) — route to master's authoritative parent.
                if (pcRecordsByFormId.ContainsKey(placed.FormId)
                    && refToCell.TryGetValue(placed.FormId, out var masterParent)
                    && masterParent != 0
                    && masterParent != effectiveFormId)
                {
                    targetCellFormId = masterParent;
                    moveCount++;
                }

                // Diagnostic skip: if the target cell sits in an excluded worldspace, drop
                // the placement so we emit nothing under it (matches the upstream
                // FilterDmpRecordsByExcludedWorldspaces but catches re-parented overrides
                // that arrive here without a WastelandNV parent in their DMP cell).
                if (options.SkipWorldspaceFormIds.Count > 0
                    && cellContexts.TryGetValue(targetCellFormId, out var targetCtx)
                    && targetCtx.WorldspaceFormId is { } targetWs
                    && options.SkipWorldspaceFormIds.Contains(targetWs))
                {
                    continue;
                }

                // Dedup: the DMP can capture the same persistent REFR under multiple DMP
                // cells (because the engine moves it to different grid cells as the player
                // travels). Emitting the same FormID more than once under one cell creates
                // an invalid GRUP. First-wins.
                var dedupKey = (targetCellFormId, placed.FormId);
                if (emittedPersistentByCellAndFormId.ContainsKey(dedupKey))
                {
                    skipDuplicatePersistentCount++;
                    continue;
                }

                emittedPersistentByCellAndFormId[dedupKey] = true;

                if (!refsByTargetCell.TryGetValue(targetCellFormId, out var list))
                {
                    list = new List<PlacedReference>();
                    refsByTargetCell[targetCellFormId] = list;
                }

                list.Add(placed);
            }
        }

        // Rebuild the cell list: each placement bucket goes to the FIRST original cell that
        // maps to it. Subsequent original cells mapping to the same target get empty
        // placements (the main loop's Bug-2 dedup then skips their emit attempt). This
        // matters when two DMP captures both target the same master cell — either through
        // direct FormID match or grid-redirect — without claim-tracking they'd both feed off
        // the same shared bucket and the engine would see duplicate top-level CELL records.
        var rebuilt = new List<CellRecord>(mergedCells.Count);
        var handledFormIds = new HashSet<uint>();
        var claimedTargets = new HashSet<uint>();
        foreach (var orig in mergedCells)
        {
            var key = gridRedirectTargets.GetValueOrDefault(orig.FormId, orig.FormId);
            var placements = claimedTargets.Add(key)
                ? refsByTargetCell.GetValueOrDefault(key, [])
                : [];
            rebuilt.Add(orig with { PlacedObjects = placements });
            handledFormIds.Add(orig.FormId);
            handledFormIds.Add(key);
        }

        var synthesizedCount = 0;
        foreach (var (cellFormId, placed) in refsByTargetCell)
        {
            if (handledFormIds.Contains(cellFormId) || placed.Count == 0)
            {
                continue;
            }

            if (!pcRecordsByFormId.TryGetValue(cellFormId, out var masterCellRec)
                || masterCellRec.Header.Signature != "CELL")
            {
                continue;
            }

            if (!cellContexts.TryGetValue(cellFormId, out var ctx))
            {
                continue;
            }

            int? gridX = null;
            int? gridY = null;
            if (TryReadCellGridCoords(masterCellRec, out var gx, out var gy))
            {
                gridX = gx;
                gridY = gy;
            }

            rebuilt.Add(new CellRecord
            {
                FormId = cellFormId,
                WorldspaceFormId = ctx.WorldspaceFormId,
                GridX = gridX,
                GridY = gridY,
                Flags = ctx.IsInterior ? (byte)0x01 : (byte)0,
                PlacedObjects = placed,
                IsBigEndian = false
            });
            synthesizedCount++;
        }

        if (moveCount > 0 || skipDuplicatePersistentCount > 0 || synthesizedCount > 0)
        {
            _sink.Info("Merging cell children",
                $"Placement re-partition: moved {moveCount:N0} override(s) to master parent cells, " +
                $"deduped {skipDuplicatePersistentCount:N0} cross-cell persistent capture(s), " +
                $"synthesized {synthesizedCount:N0} master-cell entr(y/ies) for re-parented refs.");
        }

        return rebuilt;
    }

    private static void AddReroutedChildRecord(
        Dictionary<uint, CellChildRecordBuckets> bucketsByCell,
        uint cellFormId,
        int groupType,
        byte[] recordBytes)
    {
        if (!bucketsByCell.TryGetValue(cellFormId, out var buckets))
        {
            buckets = new CellChildRecordBuckets();
            bucketsByCell[cellFormId] = buckets;
        }

        AddChildRecordToBuckets(buckets.Persistent, buckets.Vwd, buckets.Temporary, groupType, recordBytes);
    }

    private static void AddChildRecordToBuckets(
        List<byte[]> persistentRecords,
        List<byte[]> vwdRecords,
        List<byte[]> temporaryRecords,
        int groupType,
        byte[] recordBytes)
    {
        switch (groupType)
        {
            case 8:
                persistentRecords.Add(recordBytes);
                break;
            case 10:
                vwdRecords.Add(recordBytes);
                break;
            default:
                temporaryRecords.Add(recordBytes);
                break;
        }
    }

    private static List<CellOverrideBundle> CoalesceCellOverrideBundles(List<CellOverrideBundle> bundles)
    {
        if (bundles.Count <= 1)
        {
            return [.. bundles];
        }

        var coalesced = new List<CellOverrideBundle>();
        foreach (var group in bundles.GroupBy(b => b.CellFormId))
        {
            var first = group.First();
            if (group.Count() == 1)
            {
                coalesced.Add(first);
                continue;
            }

            var seenRecords = new HashSet<RecordIdentity>();
            var persistent = new List<byte[]>();
            var vwd = new List<byte[]>();
            var temporary = new List<byte[]>();

            foreach (var bundle in group.Reverse())
            {
                PrependUniqueRecords(persistent, bundle.PersistentChildRecords, seenRecords);
                PrependUniqueRecords(vwd, bundle.VwdChildRecords, seenRecords);
                PrependUniqueRecords(temporary, bundle.TemporaryChildRecords, seenRecords);
            }

            coalesced.Add(first with
            {
                PersistentChildRecords = persistent,
                VwdChildRecords = vwd,
                TemporaryChildRecords = temporary
            });
        }

        return coalesced;
    }

    private static void PrependUniqueRecords(
        List<byte[]> target,
        IReadOnlyList<byte[]> records,
        HashSet<RecordIdentity> seenRecords)
    {
        var unique = new List<byte[]>(records.Count);
        foreach (var record in records)
        {
            var identity = ReadRecordIdentity(record);
            if (seenRecords.Add(identity))
            {
                unique.Add(record);
            }
        }

        if (unique.Count > 0)
        {
            target.InsertRange(0, unique);
        }
    }

    private static RecordIdentity ReadRecordIdentity(byte[] recordBytes)
    {
        if (recordBytes.Length < 16)
        {
            return new RecordIdentity(0, 0);
        }

        return new RecordIdentity(
            BinaryPrimitives.ReadUInt32LittleEndian(recordBytes.AsSpan(0, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(recordBytes.AsSpan(12, 4)));
    }

    internal sealed record DoorTeleportRepair(
        uint StaticTargetRefFormId,
        uint ReplacementDoorBaseFormId,
        uint ReplacementRefFormId,
        uint TargetCellFormId,
        PositionSubrecord TargetTeleportPosRot);

    internal sealed class CellChildRecordBuckets
    {
        public List<byte[]> Persistent { get; } = [];
        public List<byte[]> Vwd { get; } = [];
        public List<byte[]> Temporary { get; } = [];

        public bool IsEmpty => Persistent.Count == 0 && Vwd.Count == 0 && Temporary.Count == 0;
    }

    private readonly record struct RecordIdentity(uint Signature, uint FormId);

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

    private static LandHeightmap? TryExtractMasterLandHeightmap(ParsedMainRecord masterLandRecord)
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

        return EsmWorldExtractor.ExtractLandFromBuffer(data, dataSize, header)?.Heightmap;
    }

    /// <summary>
    ///     Encode a master CELL anchor as an override that layers DMP-captured metadata
    ///     subrecords (FULL / DATA flags / XCLW water / XCLL lighting / XCLR regions /
    ///     XEZN encounter zone / XCAS / XCMO / XCIM / LTMP+LNAM) on top of master's bytes.
    ///     Mirrors the top-level record override pattern at line 753 and REFR override pattern
    ///     at line 2125 (see <see cref="TryEncodeOverrideRef" />). Returns false when the
    ///     CellEncoder isn't registered or produces no subrecords — caller should fall back
    ///     to <c>CellGrupBuilder.ReconstructRecordBytes</c> in that case (verbatim master).
    /// </summary>
    private bool TryEncodeOverrideCellAnchor(
        CellRecord dmpCell,
        ParsedMainRecord pcCellRecord,
        PluginBuildOptions options,
        ConversionPipelineStats stats,
        out byte[] bytes)
    {
        bytes = [];

        if (_encoderRegistry.Get("CELL") is not { } cellEncoder)
        {
            return false;
        }

        EncodedRecord encoded;
        try
        {
            encoded = cellEncoder.Encode(dmpCell);
        }
        catch (Exception ex)
        {
            stats.Errors++;
            _sink.Error("Merging cell children",
                $"CellEncoder threw {ex.GetType().Name} on override 0x{dmpCell.FormId:X8}: {ex.Message}",
                "CELL", dmpCell.FormId);
            return false;
        }

        if (encoded.Subrecords.Count == 0)
        {
            return false;
        }

        encoded = encoded with { Subrecords = RemapEncodedFormIds("CELL", encoded.Subrecords) };
        var policy = SubrecordMergePolicy.ForRecordType("CELL");
        var merge = RecordMergeEngine.Merge(pcCellRecord, encoded, policy);
        foreach (var w in merge.Warnings)
        {
            stats.Warnings++;
            _sink.Warn("Merging cell children", w, "CELL", dmpCell.FormId);
        }

        bytes = PluginRecordByteBuilder.BuildOverrideRecordBytes(pcCellRecord, merge.SubrecordBytes, options);
        return true;
    }

    /// <summary>Encode an override ref (FormID matches PC ESM master).</summary>
    private bool TryEncodeOverrideRef(
        PlacedReference placed,
        IRecordEncoder encoder,
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

        encoded = encoded with { Subrecords = RemapEncodedFormIds(placed.RecordType, encoded.Subrecords) };
        if (!ValidateEncodedPlacedBase(placed.RecordType, placed.FormId, encoded.Subrecords, stats))
        {
            return false;
        }

        var merge = RecordMergeEngine.Merge(pcRefRecord, encoded, policy);
        foreach (var w in merge.Warnings)
        {
            stats.Warnings++;
            _sink.Warn("Merging cell children", w, placed.RecordType, placed.FormId);
        }

        bytes = PluginRecordByteBuilder.BuildOverrideRecordBytes(pcRefRecord, merge.SubrecordBytes, options);
        return true;
    }

    internal bool TryRepairStaticDoorTeleport(
        PlacedReference placed,
        uint allocatedSourceRefFormId,
        IReadOnlyDictionary<uint, ParsedMainRecord> pcRecordsByFormId,
        IReadOnlyDictionary<uint, MasterChildLocation> masterChildLocations,
        Dictionary<uint, CellChildRecordBuckets> reroutedChildRecordsByCell,
        Dictionary<string, byte[]> grupBytesByType,
        Dictionary<uint, DoorTeleportRepair> repairsByStaticTargetRef,
        FormIdAllocator allocator,
        PluginBuildOptions options,
        ConversionPipelineStats stats,
        out PlacedReference repaired)
    {
        repaired = placed;
        if (placed.RecordType != "REFR" || !placed.DestinationDoorFormId.HasValue)
        {
            return false;
        }

        if (!pcRecordsByFormId.TryGetValue(placed.BaseFormId, out var sourceDoorBase)
            || sourceDoorBase.Header.Signature != "DOOR")
        {
            return false;
        }

        var targetRefFormId = placed.DestinationDoorFormId.Value;
        if (!pcRecordsByFormId.TryGetValue(targetRefFormId, out var targetRef)
            || targetRef.Header.Signature != "REFR"
            || !masterChildLocations.TryGetValue(targetRefFormId, out var targetLocation))
        {
            return false;
        }

        var targetBaseFormId = CellStructuralReferencePreserver.ReadNameFormId(targetRef);
        if (!targetBaseFormId.HasValue
            || !pcRecordsByFormId.TryGetValue(targetBaseFormId.Value, out var targetStaticBase)
            || targetStaticBase.Header.Signature != "STAT")
        {
            return false;
        }

        if (!TryReadPlacementData(targetRef, out var targetTransform))
        {
            return false;
        }

        if (repairsByStaticTargetRef.TryGetValue(targetRefFormId, out var existingRepair))
        {
            repaired = placed with
            {
                DestinationDoorFormId = existingRepair.ReplacementRefFormId,
                TeleportPosRot = placed.TeleportPosRot ?? existingRepair.TargetTeleportPosRot
            };
            stats.IncrementDropReason("refr.static-door-retarget-reuse");
            return true;
        }

        var targetModel = targetStaticBase.Subrecords.FirstOrDefault(s => s.Signature == "MODL");
        if (targetModel is null || targetModel.Data.Length == 0)
        {
            return false;
        }

        var replacementDoorBaseFormId = allocator.Allocate();
        var replacementRefFormId = allocator.Allocate();

        var doorBaseBytes = BuildSyntheticDoorBaseBytes(
            replacementDoorBaseFormId,
            sourceDoorBase,
            targetStaticBase,
            targetRefFormId,
            options);
        AppendOrCreateTopLevelRecord(grupBytesByType, "DOOR", doorBaseBytes);
        TrackEmittedNewFormId("DOOR", replacementDoorBaseFormId);
        stats.NewRecordsEmitted++;
        stats.IncrementEmitted("DOOR");

        var sourceTeleportPosRot = new PositionSubrecord(
            placed.X,
            placed.Y,
            placed.Z,
            placed.RotX,
            placed.RotY,
            placed.RotZ,
            placed.Offset,
            false);

        var replacementDoorRef = new PlacedReference
        {
            FormId = replacementRefFormId,
            BaseFormId = replacementDoorBaseFormId,
            BaseEditorId = targetStaticBase.EditorId,
            RecordType = "REFR",
            X = targetTransform.X,
            Y = targetTransform.Y,
            Z = targetTransform.Z,
            RotX = targetTransform.RotX,
            RotY = targetTransform.RotY,
            RotZ = targetTransform.RotZ,
            Scale = TryReadScale(targetRef, out var scale) ? scale : 1.0f,
            IsPersistent = true,
            DestinationDoorFormId = allocatedSourceRefFormId,
            TeleportPosRot = sourceTeleportPosRot,
            TeleportFlags = placed.TeleportFlags
        };

        // Synthesized door replacement refs use FormIDs that were just allocated in the same
        // pass — they aren't in _emittedNewFormIds yet at this point in the flow, so passing
        // BuildPlacedRefValidFormIds would cause the placed-ref sanitizer to incorrectly
        // drop XTEL as dangling. The synthesized values are known-good by construction,
        // so we bypass the sanitizer for this path.
        var replacementRefEncoded = RefrEncoder.EncodeNewPlacedReference(replacementDoorRef);
        foreach (var warning in replacementRefEncoded.Warnings)
        {
            stats.Warnings++;
            _sink.Warn("Merging cell children", warning, "REFR", replacementRefFormId);
        }

        var replacementRefBytes = PluginRecordByteBuilder.BuildNewRecordBytes(
            "REFR",
            replacementRefFormId,
            ComputeNewRefFlags(replacementDoorRef, options),
            replacementRefEncoded.Subrecords);
        AddReroutedChildRecord(reroutedChildRecordsByCell, targetLocation.CellFormId, 8, replacementRefBytes);
        TrackEmittedNewFormId("REFR", replacementRefFormId);
        stats.NewRecordsEmitted++;
        stats.IncrementEmitted("REFR");

        var deletedStatic = DeletedRefSynthesizer.Synthesize(
            [targetRef],
            new HashSet<uint>());
        var targetGroupType = targetLocation.GroupType is 8 or 9 or 10 ? targetLocation.GroupType : 9;
        foreach (var deletedBytes in deletedStatic.Persistent.Concat(deletedStatic.Temporary))
        {
            AddReroutedChildRecord(reroutedChildRecordsByCell, targetLocation.CellFormId, targetGroupType,
                deletedBytes);
            stats.OverridesEmitted++;
            stats.IncrementEmitted("REFR");
        }

        var targetTeleportPosRot = placed.TeleportPosRot ?? targetTransform;
        var repair = new DoorTeleportRepair(
            targetRefFormId,
            replacementDoorBaseFormId,
            replacementRefFormId,
            targetLocation.CellFormId,
            targetTeleportPosRot);
        repairsByStaticTargetRef[targetRefFormId] = repair;

        repaired = placed with
        {
            DestinationDoorFormId = replacementRefFormId,
            TeleportPosRot = targetTeleportPosRot
        };

        stats.IncrementDropReason("refr.static-door-retarget");
        if (options.VerboseDecisions)
        {
            _sink.Decision("Merging cell children",
                $"Replaced static teleport target 0x{targetRefFormId:X8} " +
                $"({targetStaticBase.Header.Signature}:{targetStaticBase.EditorId}) with generated DOOR " +
                $"base 0x{replacementDoorBaseFormId:X8} + ref 0x{replacementRefFormId:X8}; " +
                $"retargeted source ref 0x{placed.FormId:X8}.",
                "REFR",
                placed.FormId,
                "refr.static-door-retarget");
        }

        return true;
    }

    private static byte[] BuildSyntheticDoorBaseBytes(
        uint formId,
        ParsedMainRecord sourceDoorBase,
        ParsedMainRecord targetStaticBase,
        uint targetRefFormId,
        PluginBuildOptions options)
    {
        var subrecords = new List<EncodedSubrecord>
        {
            new("EDID", NullTerminatedAscii($"DmpDoorStatic{targetRefFormId:X8}"))
        };

        AddSubrecordCopy(subrecords, "OBND", targetStaticBase, sourceDoorBase);
        AddSubrecordCopy(subrecords, "FULL", sourceDoorBase);
        if (!subrecords.Any(s => s.Signature == "FULL"))
        {
            subrecords.Add(new EncodedSubrecord("FULL", NullTerminatedAscii("Door")));
        }

        AddSubrecordCopy(subrecords, "MODL", targetStaticBase);
        AddSubrecordCopy(subrecords, "MODT", targetStaticBase);

        foreach (var sub in sourceDoorBase.Subrecords)
        {
            if (sub.Signature is "EDID" or "OBND" or "FULL" or "MODL" or "MODT")
            {
                continue;
            }

            subrecords.Add(new EncodedSubrecord(sub.Signature, sub.Data));
        }

        var flags = options.CompressRecords ? 0x00040000u : 0u;
        return PluginRecordByteBuilder.BuildNewRecordBytes("DOOR", formId, flags, subrecords);
    }

    private static void AddSubrecordCopy(
        List<EncodedSubrecord> target,
        string signature,
        params ParsedMainRecord[] sources)
    {
        foreach (var source in sources)
        {
            var sub = source.Subrecords.FirstOrDefault(s => s.Signature == signature);
            if (sub is null)
            {
                continue;
            }

            target.Add(new EncodedSubrecord(signature, sub.Data));
            return;
        }
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

    private static byte[] NullTerminatedAscii(string value)
    {
        var bytes = new byte[Encoding.ASCII.GetByteCount(value) + 1];
        Encoding.ASCII.GetBytes(value, bytes);
        return bytes;
    }

    private static bool TryReadPlacementData(ParsedMainRecord record, out PositionSubrecord position)
    {
        var data = record.Subrecords.FirstOrDefault(s => s.Signature == "DATA" && s.Data.Length >= 24);
        if (data is null)
        {
            position = new PositionSubrecord(0, 0, 0, 0, 0, 0, 0, false);
            return false;
        }

        position = new PositionSubrecord(
            BinaryPrimitives.ReadSingleLittleEndian(data.Data.AsSpan(0, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(data.Data.AsSpan(4, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(data.Data.AsSpan(8, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(data.Data.AsSpan(12, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(data.Data.AsSpan(16, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(data.Data.AsSpan(20, 4)),
            0,
            false);
        return true;
    }

    private static bool MapMarkerDiffersFromMaster(PlacedReference placed, ParsedMainRecord masterRecord)
    {
        if (!placed.IsMapMarker)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(placed.MarkerName))
        {
            var masterName = masterRecord.Subrecords
                .FirstOrDefault(s => s.Signature == "FULL")?.DataAsString;
            if (!string.Equals(placed.MarkerName, masterName ?? string.Empty, StringComparison.Ordinal))
            {
                return true;
            }
        }

        if (placed.MarkerType.HasValue)
        {
            var masterTypeSubrecord = masterRecord.Subrecords
                .FirstOrDefault(s => s.Signature == "TNAM" && s.Data.Length >= 2);
            var masterType = masterTypeSubrecord is null
                ? (ushort)0
                : BinaryPrimitives.ReadUInt16LittleEndian(masterTypeSubrecord.Data.AsSpan(0, 2));
            if (masterType != (ushort)placed.MarkerType.Value)
            {
                return true;
            }
        }

        if (TryReadPlacementData(masterRecord, out var masterPosition))
        {
            var dx = placed.X - masterPosition.X;
            var dy = placed.Y - masterPosition.Y;
            var dz = placed.Z - masterPosition.Z;
            if ((dx * dx) + (dy * dy) + (dz * dz) > 1.0f)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryReadScale(ParsedMainRecord record, out float scale)
    {
        var xscl = record.Subrecords.FirstOrDefault(s => s.Signature == "XSCL" && s.Data.Length >= 4);
        if (xscl is null)
        {
            scale = 1.0f;
            return false;
        }

        scale = BinaryPrimitives.ReadSingleLittleEndian(xscl.Data);
        return true;
    }

    /// <summary>
    ///     Runtime DMP captures can carry ExtraTeleport data from prototype doors whose
    ///     destination refs either no longer exist in the released FNV master or are only
    ///     interior static marker art. Emitting those as XTEL makes the engine bind a
    ///     teleport to a null/non-door target during cell attach, which produces
    ///     MASTERFILE linked-door errors and has shown up in the Goodsprings crash logs.
    ///     Until generated REFR destinations have a full source→allocated remap pass, only
    ///     keep teleports whose source and destination both resolve to master/emitted DOOR
    ///     records.
    /// </summary>
    internal PlacedReference SanitizeNewRefTeleport(
        PlacedReference placed,
        IReadOnlyDictionary<uint, ParsedMainRecord> pcRecordsByFormId,
        PluginBuildOptions options,
        ConversionPipelineStats stats)
    {
        if (!placed.DestinationDoorFormId.HasValue)
        {
            return placed;
        }

        if (!IsKnownDoorBase(placed.BaseFormId, pcRecordsByFormId, out var sourceDescription))
        {
            return DropNewRefTeleport(placed,
                $"source base 0x{placed.BaseFormId:X8} is not a DOOR ({sourceDescription})",
                options,
                stats);
        }

        var targetFormId = placed.DestinationDoorFormId.Value;
        if (!pcRecordsByFormId.TryGetValue(targetFormId, out var targetRecord))
        {
            return DropNewRefTeleport(placed,
                $"destination ref 0x{targetFormId:X8} is not present in the master ESM",
                options,
                stats);
        }

        if (targetRecord.Header.Signature != "REFR")
        {
            return DropNewRefTeleport(placed,
                $"destination 0x{targetFormId:X8} is {targetRecord.Header.Signature}, not REFR",
                options,
                stats);
        }

        var targetBaseFormId = CellStructuralReferencePreserver.ReadNameFormId(targetRecord);
        if (!targetBaseFormId.HasValue)
        {
            return DropNewRefTeleport(placed,
                $"destination ref 0x{targetFormId:X8} has no NAME base",
                options,
                stats);
        }

        if (!IsKnownDoorBase(targetBaseFormId.Value, pcRecordsByFormId, out var targetDescription))
        {
            return DropNewRefTeleport(placed,
                $"destination ref 0x{targetFormId:X8} base 0x{targetBaseFormId.Value:X8} " +
                $"is not a DOOR ({targetDescription})",
                options,
                stats);
        }

        return placed;
    }

    private PlacedReference DropNewRefTeleport(
        PlacedReference placed,
        string reason,
        PluginBuildOptions options,
        ConversionPipelineStats stats)
    {
        stats.Warnings++;
        stats.IncrementDropReason("refr.invalid-xtel");

        if (options.VerboseDecisions)
        {
            _sink.Decision("Merging cell children",
                $"Dropping XTEL from new {placed.RecordType} 0x{placed.FormId:X8}: {reason}.",
                placed.RecordType,
                placed.FormId,
                "refr.invalid-xtel");
        }

        return placed with
        {
            DestinationDoorFormId = null,
            DestinationCellFormId = null,
            TeleportPosRot = null,
            TeleportFlags = null
        };
    }

    private bool IsKnownDoorBase(
        uint formId,
        IReadOnlyDictionary<uint, ParsedMainRecord> pcRecordsByFormId,
        out string description)
    {
        if (pcRecordsByFormId.TryGetValue(formId, out var record))
        {
            description = $"{record.Header.Signature}:{record.EditorId ?? string.Empty}";
            return record.Header.Signature == "DOOR";
        }

        if (_emittedNewFormIdsByType.TryGetValue("DOOR", out var emittedDoors)
            && emittedDoors.Contains(formId))
        {
            description = "freshly emitted DOOR";
            return true;
        }

        description = "missing";
        return false;
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

    private bool TryRemapPlacedBaseAlias(
        PlacedReference placed,
        uint allocatedBase,
        out PlacedReference remapped)
    {
        remapped = placed;

        if (!_newRecordSourceToAllocatedType.TryGetValue(placed.BaseFormId, out var aliasRecordType)
            || !ReferenceBaseRemapper.CanPlacedRecordUseBaseType(placed.RecordType, aliasRecordType))
        {
            return false;
        }

        remapped = placed with { BaseFormId = allocatedBase };
        return true;
    }


    private bool IsValidPlacedBaseForOutput(
        PlacedReference placed,
        Dictionary<uint, ParsedMainRecord> pcRecordsByFormId,
        out string? knownBaseRecordType)
    {
        knownBaseRecordType = null;

        if (placed.BaseFormId == 0)
        {
            return true;
        }

        if (pcRecordsByFormId.TryGetValue(placed.BaseFormId, out var masterBase))
        {
            knownBaseRecordType = masterBase.Header.Signature;
            return ReferenceBaseRemapper.CanPlacedRecordUseBaseType(
                placed.RecordType, knownBaseRecordType);
        }

        foreach (var (recordType, ids) in _emittedNewFormIdsByType)
        {
            if (!ids.Contains(placed.BaseFormId)
                || _newRecordSourceToAllocated.ContainsKey(placed.BaseFormId))
            {
                continue;
            }

            knownBaseRecordType = recordType;
            return ReferenceBaseRemapper.CanPlacedRecordUseBaseType(
                placed.RecordType, knownBaseRecordType);
        }

        return false;
    }

    private bool ValidateEncodedPlacedBase(
        string placedRecordType,
        uint placedFormId,
        IReadOnlyList<EncodedSubrecord> subrecords,
        ConversionPipelineStats stats)
    {
        if (placedRecordType is not ("REFR" or "ACHR" or "ACRE"))
        {
            return true;
        }

        var name = subrecords.FirstOrDefault(static s => s.Signature == "NAME" && s.Bytes.Length >= 4);
        if (name is null)
        {
            return true;
        }

        var baseFormId = BinaryPrimitives.ReadUInt32LittleEndian(name.Bytes.AsSpan(0, 4));
        if (baseFormId == 0 || baseFormId == 0xFFFFFFFFu)
        {
            return true;
        }

        if (IsValidPlacedBaseFormIdForOutput(placedRecordType, baseFormId, out var knownBaseRecordType))
        {
            return true;
        }

        var reason = knownBaseRecordType is null
            ? "refr.dangling-base"
            : "refr.base-type-mismatch";
        var message = knownBaseRecordType is null
            ? $"base 0x{baseFormId:X8} is not in master ESM and not freshly emitted."
            : $"base 0x{baseFormId:X8} is {knownBaseRecordType}, which is not a valid base type for " +
              $"{placedRecordType}.";

        stats.IncrementSkipped(placedRecordType);
        stats.IncrementDropReason(reason);
        _sink.Decision("Merging cell children",
            $"Skipping {placedRecordType} 0x{placedFormId:X8} after FormID remap — {message}",
            placedRecordType,
            placedFormId,
            reason);
        return false;
    }

    private bool IsValidPlacedBaseFormIdForOutput(
        string placedRecordType,
        uint baseFormId,
        out string? knownBaseRecordType)
    {
        knownBaseRecordType = null;

        if (_masterFormIdsByType is not null)
        {
            foreach (var (recordType, ids) in _masterFormIdsByType)
            {
                if (!ids.Contains(baseFormId))
                {
                    continue;
                }

                knownBaseRecordType = recordType;
                return ReferenceBaseRemapper.CanPlacedRecordUseBaseType(
                    placedRecordType, knownBaseRecordType);
            }
        }

        foreach (var (recordType, ids) in _emittedNewFormIdsByType)
        {
            if (!ids.Contains(baseFormId)
                || _newRecordSourceToAllocated.ContainsKey(baseFormId))
            {
                continue;
            }

            knownBaseRecordType = recordType;
            return ReferenceBaseRemapper.CanPlacedRecordUseBaseType(
                placedRecordType, knownBaseRecordType);
        }

        return false;
    }

    /// <summary>
    ///     Build (cached, lazy) the master ∪ all-emitted-new FormID set used by the
    ///     placed-ref subrecord sanitizer. Recomputed once per build pass — cell child
    ///     encoding happens after all top-level types are emitted, so this is stable.
    /// </summary>
    private HashSet<uint>? _placedRefValidFormIdsCache;
    private HashSet<uint> BuildPlacedRefValidFormIds()
    {
        if (_placedRefValidFormIdsCache is not null)
        {
            return _placedRefValidFormIdsCache;
        }

        var union = new HashSet<uint>();
        if (_masterFormIds is not null) union.UnionWith(_masterFormIds);
        union.UnionWith(_emittedNewFormIds);
        _placedRefValidFormIdsCache = union;
        return union;
    }

    /// <summary>Encode a new ref (FormID not in PC ESM — new-record path).</summary>
    private bool TryEncodeNewRef(
        PlacedReference placed,
        uint allocatedFormId,
        PluginBuildOptions options,
        ConversionPipelineStats stats,
        out byte[] bytes)
    {
        bytes = [];

        EncodedRecord encoded;
        try
        {
            // Validate optional FormID-bearing subrecords (XEZN, XLKR, XOWN, XESP, XTEL)
            // against master ∪ all-emitted-new. Dangling refs skip the subrecord (engine
            // would log "Unable to find linked reference" / "Unable to find enable state
            // parent" otherwise and remove the data anyway). Same remap-then-validity policy
            // as IDLE ANAM, CTDA params, and PACK PLDT/PTDT.
            encoded = RefrEncoder.EncodeNewPlacedReference(
                placed, BuildPlacedRefValidFormIds(),
                _newRecordSourceToAllocated.Count > 0 ? _newRecordSourceToAllocated : null);
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

        var flags = ComputeNewRefFlags(placed, options);
        var subrecords = RemapEncodedFormIds(placed.RecordType, encoded.Subrecords);
        if (!ValidateEncodedPlacedBase(placed.RecordType, placed.FormId, subrecords, stats))
        {
            return false;
        }

        bytes = PluginRecordByteBuilder.BuildNewRecordBytes(placed.RecordType, allocatedFormId, flags,
            subrecords);
        TrackEmittedNewFormId(placed.RecordType, allocatedFormId);

        if (options.VerboseDecisions)
        {
            _sink.Decision("Merging cell children",
                $"New {placed.RecordType} allocated FormID 0x{allocatedFormId:X8} (DMP source 0x{placed.FormId:X8}).",
                placed.RecordType, placed.FormId);
        }

        return true;
    }

    private byte[] BuildNewCellRecordBytes(
        CellRecord dmpCell,
        uint emittedFormId,
        IRecordEncoder cellEncoder,
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
        var subrecords = RemapEncodedFormIds("CELL", encoded.Subrecords);
        return PluginRecordByteBuilder.BuildNewRecordBytes("CELL", emittedFormId, flags, subrecords);
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
        Dictionary<string, uint> masterByEditorId)
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
    ///     Returns true (and produces a <paramref name="remapped" /> reference with the
    ///     updated <c>BaseFormId</c>) when a unique same-type stem match exists;
    ///     returns false on no match, ambiguity, or missing prototype EditorID. Counters
    ///     and decision logs are emitted as a side-effect so callers don't need to
    ///     duplicate that bookkeeping.
    /// </summary>
    private bool TryRemapRefrBaseByEditorIdStem(
        PlacedReference placed,
        PlacedReference placedForEmit,
        ConversionPipelineStats stats,
        out PlacedReference remapped)
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
                placed.RecordType, placed.FormId, "refr.editorid-remap-ambiguous");
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
                placed.RecordType, placed.FormId, "refr.editorid-remap-ambiguous");
            return false;
        }

        var (winningType, winningFormId) = hits[0];

        stats.IncrementDropReason("refr.editorid-remap");
        _sink.Decision("Merging cell children",
            $"Remapped new ref 0x{placed.FormId:X8} base 0x{placedForEmit.BaseFormId:X8} " +
            $"\"{placed.BaseEditorId}\" → master {winningType} 0x{winningFormId:X8} by " +
            "EditorID-stem fallback.",
            placed.RecordType, placed.FormId, "refr.editorid-remap");

        remapped = placedForEmit with { BaseFormId = winningFormId };
        return true;
    }

    private PlacedReference RemapNewPlacedActorBase(
        PlacedReference placed,
        Dictionary<uint, uint> actorBaseRemap,
        PluginBuildOptions options)
    {
        if (placed.RecordType is not ("ACHR" or "ACRE")
            || placed.BaseFormId == 0
            || _masterFormIds?.Contains(placed.BaseFormId) == true)
        {
            return placed;
        }

        var masterBaseFormId = actorBaseRemap.TryGetValue(placed.BaseFormId, out var mappedBase)
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
                placed.RecordType, placed.FormId, "refr.actor-base-remap");
        }

        return placed with { BaseFormId = masterBaseFormId.Value };
    }

    private uint? TryFindMasterActorBaseByEditorId(PlacedReference placed)
    {
        if (_masterEditorIdToFormIdByType is null || string.IsNullOrEmpty(placed.BaseEditorId))
        {
            return null;
        }

        var baseTypes = placed.RecordType switch
        {
            "ACHR" => (IReadOnlyList<string>)["NPC_"],
            "ACRE" => ["CREA"],
            _ => Array.Empty<string>()
        };

        foreach (var baseType in baseTypes)
        {
            if (_masterEditorIdToFormIdByType.TryGetValue(baseType, out var byEditorId)
                && byEditorId.TryGetValue(placed.BaseEditorId, out var masterFormId))
            {
                return masterFormId;
            }
        }

        return null;
    }

    /// <summary>
    ///     Phase B SCOL census helper: walks a master SCOL's parsed subrecords and compares
    ///     it to a DMP SCOL to detect override-emission-worthy deltas. Returns true on
    ///     first difference (with a human-readable reason); false when content matches
    ///     within tolerance. Sub-epsilon float drift (0.01 abs) is treated as equal to
    ///     avoid flapping from float-precision noise.
    /// </summary>
    internal static bool TryDetectScolOverrideDelta(
        StaticCollectionRecord dmp,
        ParsedMainRecord master,
        out string reason)
    {
        const float epsilon = 0.01f;
        var masterParts = new List<(uint OnamFormId, List<StaticCollectionPlacement> Placements)>();
        var currentPart = -1;

        foreach (var sub in master.Subrecords)
        {
            switch (sub.Signature)
            {
                case "ONAM" when sub.Data.Length == 4:
                    masterParts.Add((BinaryPrimitives.ReadUInt32LittleEndian(sub.Data),
                        new List<StaticCollectionPlacement>()));
                    currentPart = masterParts.Count - 1;
                    break;
                case "DATA" when sub.Data.Length > 0 && sub.Data.Length % 28 == 0 && currentPart >= 0:
                    var placements = masterParts[currentPart].Placements;
                    var count = sub.Data.Length / 28;
                    for (var i = 0; i < count; i++)
                    {
                        var span = sub.Data.AsSpan(i * 28, 28);
                        placements.Add(new StaticCollectionPlacement(
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
                reason =
                    $"part #{i} ONAM dmp=0x{dmp.Parts[i].OnamFormId:X8} vs master=0x{masterParts[i].OnamFormId:X8}";
                return true;
            }

            if (dmp.Parts[i].Placements.Count != masterParts[i].Placements.Count)
            {
                reason =
                    $"part #{i} placement count dmp={dmp.Parts[i].Placements.Count} vs master={masterParts[i].Placements.Count}";
                return true;
            }

            for (var p = 0; p < masterParts[i].Placements.Count; p++)
            {
                var dp = dmp.Parts[i].Placements[p];
                var mp = masterParts[i].Placements[p];
                if (MathF.Abs(dp.X - mp.X) > epsilon || MathF.Abs(dp.Y - mp.Y) > epsilon
                                                     || MathF.Abs(dp.Z - mp.Z) > epsilon ||
                                                     MathF.Abs(dp.RotX - mp.RotX) > epsilon
                                                     || MathF.Abs(dp.RotY - mp.RotY) > epsilon ||
                                                     MathF.Abs(dp.RotZ - mp.RotZ) > epsilon
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

    internal static bool NpcHasRenderableTemplate(IReadOnlyList<EncodedSubrecord> subrecords)
    {
        var hasTemplate = subrecords.Any(s => s.Signature == "TPLT" && s.Bytes.Length >= 4);
        if (!hasTemplate)
        {
            return NpcHasCompleteCapturedFaceGen(subrecords);
        }

        var acbs = subrecords.FirstOrDefault(s => s.Signature == "ACBS" && s.Bytes.Length >= 24);
        if (acbs is null)
        {
            return NpcHasCompleteCapturedFaceGen(subrecords);
        }

        const ushort useTraitsTemplateFlag = 0x0001;
        var templateFlags = BinaryPrimitives.ReadUInt16LittleEndian(acbs.Bytes.AsSpan(22, 2));
        return (templateFlags & useTraitsTemplateFlag) != 0
               || NpcHasCompleteCapturedFaceGen(subrecords);
    }

    private static bool NpcHasCompleteCapturedFaceGen(IReadOnlyList<EncodedSubrecord> subrecords)
    {
        return subrecords.Any(s => s.Signature == "RNAM" && s.Bytes.Length >= 4)
               && subrecords.Any(s => s.Signature == "FGGS" && s.Bytes.Length == 50 * sizeof(float))
               && subrecords.Any(s => s.Signature == "FGGA" && s.Bytes.Length == 30 * sizeof(float))
               && subrecords.Any(s => s.Signature == "FGTS" && s.Bytes.Length == 50 * sizeof(float));
    }

    internal static bool IsRuntimeStructuralMarkerPlacement(
        PlacedReference placed,
        IReadOnlyDictionary<uint, ParsedMainRecord> pcRecordsByFormId,
        out string? baseEditorId)
    {
        baseEditorId = null;
        if (placed.RecordType != "REFR"
            || placed.BaseFormId == 0
            || !pcRecordsByFormId.TryGetValue(placed.BaseFormId, out var baseRecord)
            || !CellStructuralReferencePreserver.IsStructuralMarkerBase(baseRecord))
        {
            return false;
        }

        baseEditorId = baseRecord.EditorId;
        return true;
    }

    private IReadOnlyList<EncodedSubrecord> RemapEncodedFormIds(
        string recordType,
        IReadOnlyList<EncodedSubrecord> subrecords)
    {
        return _newRecordSourceToAllocated.Count == 0
            ? subrecords
            : EncodedSubrecordFormIdRemapper.Remap(recordType, subrecords, _newRecordSourceToAllocated);
    }

    /// <summary>
    ///     Drop SCRI subrecords whose script FormID isn't in the master ESM. Returns the
    ///     original list when nothing was dropped to avoid unnecessary allocation. Logs one
    ///     warning per drop so the user can see what was nulled and which records lose
    ///     scripts.
    /// </summary>
    private IReadOnlyList<EncodedSubrecord> ValidateScriRefs(
        IReadOnlyList<EncodedSubrecord> subrecords,
        string recordType,
        uint? sourceFormId)
    {
        if (_masterFormIds is null || _masterFormIds.Count == 0)
        {
            return subrecords;
        }

        List<EncodedSubrecord>? filtered = null;
        for (var i = 0; i < subrecords.Count; i++)
        {
            var sub = subrecords[i];
            if (sub.Signature != "SCRI" || sub.Bytes.Length != 4)
            {
                filtered?.Add(sub);
                continue;
            }

            var formId = BinaryPrimitives.ReadUInt32LittleEndian(sub.Bytes);
            // Validate against the SCPT-typed subsets, not the generic union.
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
            filtered ??= new List<EncodedSubrecord>(subrecords.Count);
            if (filtered.Count == 0)
            {
                for (var j = 0; j < i; j++)
                {
                    filtered.Add(subrecords[j]);
                }
            }

            _sink.Warn("Merging top-level records",
                $"Dropping SCRI 0x{formId:X8} — script FormID not in master ESM or newly emitted set.",
                recordType, sourceFormId, "scri.dangling");
        }

        return filtered ?? subrecords;
    }

    private IReadOnlyList<EncodedSubrecord> ValidateLtexRefs(
        IReadOnlyList<EncodedSubrecord> subrecords,
        uint sourceFormId)
    {
        List<EncodedSubrecord>? rewritten = null;
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

            var rawFormId = BinaryPrimitives.ReadUInt32LittleEndian(sub.Bytes);
            var formId = _newRecordSourceToAllocated.GetValueOrDefault(rawFormId, rawFormId);
            if (IsKnownFormIdForType(formId, targetType))
            {
                var replacement = formId == rawFormId ? sub : RewriteFormIdSubrecord(sub.Signature, formId);
                rewritten?.Add(replacement);
                continue;
            }

            rewritten ??= new List<EncodedSubrecord>(subrecords.Count);
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
                "LTEX", sourceFormId, "ltex.dangling-ref");
        }

        return rewritten ?? subrecords;
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

        raceFormId = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(rnam.Data.AsSpan(0, 4));
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

        gridX = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(xclc.Data.AsSpan(0, 4));
        gridY = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(xclc.Data.AsSpan(4, 4));
        return true;
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

    private static EncodedSubrecord RewriteFormIdSubrecord(string signature, uint formId)
    {
        var bytes = new byte[4];
        SubrecordEncoder.WriteFormId(bytes, 0, formId);
        return new EncodedSubrecord(signature, bytes);
    }

    /// <summary>
    ///     Compute the record header flags for a newly-emitted placed ref. Captures persistent
    ///     and initially-disabled flags from the parsed model.
    /// </summary>
    private static uint ComputeNewRefFlags(PlacedReference placed, PluginBuildOptions options)
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
            BlockLabel = new byte[4], // value = 0
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
        CellRecord dmpCell,
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
                "CELL", dmpCell.FormId, "skipped:no-master-worldspace");
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
                "CELL", dmpCell.FormId, "skipped:no-master-worldspace");
            return false;
        }

        if (!dmpCell.GridX.HasValue || !dmpCell.GridY.HasValue)
        {
            stats.IncrementSkipped("CELL");
            _sink.Warn("Merging cell children",
                $"New exterior CELL 0x{dmpCell.FormId:X8} skipped — missing grid coordinates.",
                "CELL", dmpCell.FormId, "skipped:no-grid-coords");
            return false;
        }

        var gridX = dmpCell.GridX.Value;
        var gridY = dmpCell.GridY.Value;

        // Dedup by (worldspace, gridX, gridY). Two new cells claiming the same coords
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
                "CELL", dmpCell.FormId, "cell.coord-dup");
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
            BinaryPrimitives.WriteUInt32LittleEndian(label, packed);
        }

        return label;
    }

    /// <summary>
    ///     Walks the digested record collection and yields per-type model lists for the
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
        // MESG must precede script-bearing records. Object scripts can reference message
        // records by SCRO slot (UlyssesScript -> FollowerMessageDeadUlysses); emitting MESG
        // later means the script encoder has no allocated target yet and preserves the slot
        // count only by writing a null SCRO.
        yield return ("MESG", records.Messages);
        // PACK must precede NPC_ so the NPC encoder can validate its PKID list against the
        // set of (master ∪ just-emitted) PACK FormIDs and drop dangling refs. Dangling PKIDs
        // were the leading suspect for the "every NPC plays the crucified idle" regression —
        // missing packages → NPC falls through to default behavior → crucify idle.
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
        // Types whose encoders are registered but were not previously dispatched.
        // Adding them ensures DMP-captured overrides for these record types reach the
        // merge engine.
        yield return ("CSTY", records.CombatStyles);
        yield return ("LGTM", records.LightingTemplates);
        yield return ("WATR", records.Water);
        yield return ("WTHR", records.Weather);
        // Close encoder coverage for every type with a runtime reader.
        yield return ("ECZN", records.EncounterZones);
        yield return ("MICN", records.MenuIcons);
        // VTYP yielded near the top (before NPC_/CREA) — see comment there.
        yield return ("CCRD", records.CaravanCards);
        yield return ("CMNY", records.CaravanMoney);
        yield return ("CDCK", records.CaravanDecks);
        yield return ("INGR", records.Ingredients);
        yield return ("LSCT", records.LoadScreenTypes);
        yield return ("IDLE", records.IdleAnimations);
        yield return ("IPCT", records.ImpactData);
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
        Models.RecordCollection records,
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
        Models.RecordCollection records,
        EsmRecordScanResult? scanResult,
        IReadOnlyDictionary<uint, uint>? authority,
        IReadOnlyDictionary<uint, string>? worldspaceNames,
        IReadOnlyDictionary<uint, CellAuthorityMetadata>? cellMetadata,
        IReadOnlyDictionary<uint, uint>? refToCell,
        IReadOnlyList<CellReferenceParentWindow>? refWindows)
    {
        var result = CellWorldspaceAuthorityApplier.Apply(
            records,
            authority,
            worldspaceNames,
            scanResult,
            cellMetadata,
            refToCell,
            refWindows);
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
    ///     same ones <see cref="BuildNpcVoiceTypeLookup"/> chains to). Without the new-VTYP
    ///     entries, a new NPC pointing at a new VTYP would never resolve to an EditorId and
    ///     dialogue audio bindings would be emitted with VoiceTypeEditorId=null, sending the
    ///     packer's voicetype directory to the engine's MaleAdult01Default fallback.
    /// </summary>
    private static Dictionary<uint, string> BuildVoiceTypeEditorIdLookup(
        IReadOnlyDictionary<uint, ParsedMainRecord> masterRecords,
        Models.RecordCollection records)
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
        Models.RecordCollection records,
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
        Models.RecordCollection records,
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
