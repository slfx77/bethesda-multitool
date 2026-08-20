using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;

/// <summary>
///     Options controlling plugin ESM construction from a DMP + base ESM.
/// </summary>
public sealed record PluginBuildOptions
{
    /// <summary>
    ///     Filename of the master file the plugin depends on.
    ///     Goes into the TES4 MAST subrecord.
    /// </summary>
    public string MasterFileName { get; init; } = "FalloutNV.esm";

    /// <summary>
    ///     File size in bytes of the master file on disk.
    ///     Goes into the TES4 DATA subrecord (matches xEdit/GECK convention).
    /// </summary>
    public long MasterFileSize { get; init; }

    /// <summary>Plugin author (CNAM in TES4). Optional.</summary>
    public string? Author { get; init; }

    /// <summary>Plugin description (SNAM in TES4). Optional.</summary>
    public string? Description { get; init; }

    /// <summary>
    ///     If true, every emitted record gets the compressed flag set and zlib body.
    ///     If false (default), records are emitted uncompressed.
    /// </summary>
    public bool CompressRecords { get; init; }

    /// <summary>
    ///     If true, after writing the output bytes the builder re-parses them and asserts
    ///     structural validity. Good for development and CI.
    /// </summary>
    public bool ValidateOutput { get; init; } = true;

    /// <summary>
    ///     If true, emit one decision/info event per record. If false (default), the pipeline
    ///     aggregates by (recordType, reason) and emits summary events per phase.
    /// </summary>
    public bool VerboseDecisions { get; init; }

    /// <summary>
    ///     When a master cell is already being overridden because the DMP captured new
    ///     content (new placed refs / captured terrain), also emit the proto-authored
    ///     NAVM(s) the DMP captured for that cell so NPCs pathfind on the reshaped layout.
    ///     Default <c>false</c> — proven harmful in-game (see below); opt in only for new
    ///     (non-master) cells, which are emitted regardless of this flag.
    ///     DISABLED BY DEFAULT because our reconstructed NAVMs carry EMPTY NVCI door-link
    ///     arrays (<c>NavInfoMapBuilder.BuildNvci</c>), and degenerate captures (6-7 vertex
    ///     stubs) besides. When emitted into an overridden MASTER interior they shadow the
    ///     master's own complete NAVMs (which load via the cell file-list merge) and
    ///     the engine null-derefs building the cross-cell TeleportDoorSearch / NavMeshInfoMap
    ///     parent-space graph on a cold <c>coc</c> into that cell (Gomorrah01, confirmed
    ///     fixed by suppressing them). Master's navmeshes are strictly better than our
    ///     reconstructions until NVCI door-link reconstruction exists, so master cells keep
    ///     theirs and we emit nothing. Re-enable only after NVCI is reconstructed.
    ///     Independent of the ESM/master flag — that is tied to the presence of cell-child
    ///     overrides (see <see cref="Output.Tes4HeaderBuilder" />), not to this option.
    /// </summary>
    public bool EmitMasterCellNavmAugmentation { get; init; }

    /// <summary>
    ///     Recover placed actors (ACHR/ACRE) whose base is a runtime-dynamic clone
    ///     (<c>BaseFormId &gt;= 0xFF000000</c>) — the generic leveled-spawn crowd the planner
    ///     otherwise drops as <c>refr.dangling-base</c> — by re-pointing the base to the actor's
    ///     captured <c>ExtraLeveledCreature</c> pointer
    ///     (<c>LeveledCreatureOriginalBaseFormId</c>, else <c>LeveledCreatureTemplateFormId</c>),
    ///     resolved through <c>SourceToEmittedFormId</c> and accepted only if it lands on a
    ///     type-compatible <c>NPC_</c> (ACHR) / <c>CREA</c> (ACRE) base — master or captured-proto.
    ///     Default <c>true</c> (opt out with <c>--no-recover-leveled-spawns</c>).
    ///     Trade-off: the re-pointed base is the crash-time resolved template, so the actor is a
    ///     fixed instance (no re-leveling); this faithfully reconstructs the captured scene, which
    ///     is the goal (populate cells the runtime showed inhabited). A leveled-list
    ///     (<c>LVLN/LVLC</c>) candidate is never emitted as an actor base — xEdit-invalid for
    ///     ACHR/ACRE — so the existing base-type validation rejects it and we fall through to the
    ///     other pointer or drop. Ships ON by default: the re-populated crowd was A/B-tested
    ///     in-game (Gomorrah01 29→65 ACHR, cold coc stable). Opt out for a bare-master baseline.
    /// </summary>
    public bool RecoverLeveledSpawnActors { get; init; } = true;

    /// <summary>
    ///     Place refs stranded in <c>[Unresolved …]</c> buckets (parent CELL never captured) by
    ///     their own coordinates: exact-grid match against a uniquely-identifying captured cell,
    ///     else unique containment inside exactly one worldspace's captured grid bounds. Inferred
    ///     placements are provenance-marked (<c>AssignmentSource = "WorldspaceBoundsInference"</c>).
    ///     ON by default (USER RULING 2026-08-05, xex21 Utl* block — 463 captured placements were
    ///     silently unrepresented); <c>--no-cell-inference</c> opts out.
    /// </summary>
    public bool InferUnresolvedCellPlacements { get; init; } = true;

    /// <summary>
    ///     Diagnostic: suppress NAVM record emission inside cell bundles while keeping the
    ///     TES4 ESM flag and everything else unchanged (unlike turning off
    ///     <see cref="EmitMasterCellNavmAugmentation" />, which also clears the flag). Used
    ///     to bisect crash classes where the navmesh records themselves are the suspect —
    ///     the Gomorrah01 attach AV bisect: that cell holds the plugin's only proto NAVMs.
    ///     NAVI rows follow automatically since they're built from actually-emitted NAVMs.
    /// </summary>
    public bool DiagnosticSkipCellNavm { get; init; }

    /// <summary>
    ///     Diagnostic: suppress NEW (plugin-allocated FormID) placed-ref emission inside
    ///     cell bundles — master overrides, carries, and disabled-removals still emit.
    ///     Second knob of the Gomorrah01 attach-AV bisect (new refs are the last content
    ///     class distinguishing the crashing cell from working ones).
    /// </summary>
    public bool DiagnosticSkipCellNewRefs { get; init; }

    /// <summary>
    ///     Starting local FormID for newly-allocated records. Defaults to 0x800 to match the
    ///     GECK convention that local IDs below 0x800 are reserved for the engine.
    /// </summary>
    public uint NewRecordBaseFormId { get; init; } = FormIdAllocator.DefaultBaseLocalId;

    /// <summary>
    ///     Asset-rename pass. When non-empty (and <see cref="AssetRenameBaselineFolder" />
    ///     is set), <c>PluginConversionPipeline.BuildAsync</c> resolves every record-sourced asset path
    ///     against these folders before encoding. Paths that fuzzy-match to a differently-
    ///     named asset get their record field rewritten in-place so the output ESM carries
    ///     the matched filename. Mirror the same folders passed to <c>AssetPackingService</c>.
    /// </summary>
    public IReadOnlyList<SecondaryDataFolder> AssetRenameSecondaryFolders { get; init; } = [];

    /// <summary>
    ///     The user's FNV PC Data folder used as the "baseline" for the rename pass. Assets
    ///     already exact-matchable here are not rewritten. Null disables the rename pass.
    /// </summary>
    public string? AssetRenameBaselineFolder { get; init; }

    /// <summary>
    ///     If true, the rename pass consults the secondary folders before the baseline,
    ///     mirroring <c>AssetPackingOptions.OverrideVanillaBaseline</c>. Use both flags
    ///     together so the rewritten ESM fields and the packed BSA agree on which secondary
    ///     path wins. Defaults to false.
    /// </summary>
    public bool AssetRenameOverrideVanilla { get; init; }

    /// <summary>
    ///     When true (default), prototype REFRs whose base FormID is missing from master
    ///     AND not freshly emitted get a last-chance rename remap by EditorID stem against
    ///     the master ESM. Same-type discipline + single-candidate gate + ambiguity-refusal
    ///     logging. Set false to suppress the remap (e.g. for diagnostic runs comparing
    ///     baseline-vs-remap REFR drop counts).
    /// </summary>
    public bool EnableRefrBaseEditorIdRemap { get; init; } = true;

    /// <summary>
    ///     Optional authoritative <c>CellFormId → WorldspaceFormId</c> projection from richer
    ///     cell metadata. When supplied, the builder applies these assignments to every parsed
    ///     CELL before grouping into world children GRUPs.
    /// </summary>
    public IReadOnlyDictionary<uint, uint>? CellWorldspaceAuthority { get; init; }

    /// <summary>
    ///     Optional richer cell authority metadata. This carries authored worldspace ownership,
    ///     interior classification, grid coordinates, EditorID, and display name when known.
    /// </summary>
    public IReadOnlyDictionary<uint, CellAuthorityMetadata>? CellMetadataAuthority { get; init; }

    /// <summary>
    ///     Optional authoritative <c>ReferenceFormId → CellFormId</c> parent map. Used to move
    ///     placements out of synthetic unresolved DMP buckets before plugin cell grouping.
    /// </summary>
    public IReadOnlyDictionary<uint, uint>? CellReferenceParentAuthority { get; init; }

    /// <summary>
    ///     Optional pinned offset windows for moving still-unresolved references into a
    ///     known parent CELL after exact ref-parent authority has run.
    /// </summary>
    public IReadOnlyList<CellReferenceParentWindow>? CellReferenceParentWindows { get; init; }

    /// <summary>
    ///     Optional worldspace labels from the authority JSON. Used when an authority
    ///     assignment creates a worldspace shell for captured cells whose WRLD record was not
    ///     recovered from the DMP.
    /// </summary>
    public IReadOnlyDictionary<uint, string>? CellWorldspaceAuthorityWorldspaceNames { get; init; }

    /// <summary>
    ///     Diagnostic mode: when a CELL exists in master AND the DMP captured placements
    ///     for it, emit deletion overrides for every master <i>temporary</i> ref that
    ///     isn't in the DMP snapshot — so the in-game / GECK view of that cell shows
    ///     only the prototype's static placements (plus master persistent refs, which
    ///     stay untouched to avoid breaking quest scripts / enable-parent chains).
    ///     <br />
    ///     Off by default. When on, this overrides the classifier's
    ///     <c>PersistentOnly</c> mode for DMP cells that captured placements, and
    ///     bypasses <c>PreserveMissingStructuralCellRefs</c> so structural markers /
    ///     doors / NAVMs not in the DMP also get wiped. Markers + fast-travel teleports
    ///     in affected cells may disappear; persistent refs (and therefore quest-bound
    ///     objects) are preserved.
    /// </summary>
    public bool ReplaceCellTemporariesOnOverride { get; init; }

    /// <summary>
    ///     Experimental opt-in: scan uncovered DMP gaps for validated raw ESM records and
    ///     RTTI-backed runtime forms, then promote only candidates that existing semantic
    ///     readers can consume safely. Browser/audit discovery is separate; this switch
    ///     changes DMP→ESM parse inputs and is off by default.
    /// </summary>
    public bool RecoverGaps { get; init; }

    /// <summary>
    ///     Diagnostic: worldspace FormIDs whose cells (and all nested REFR/ACHR/ACRE
    ///     placements + per-cell LAND/NAVM) the converter should drop from emission
    ///     entirely. Used to bisect crashes that point at a specific worldspace —
    ///     skipping it should leave master's content intact via per-FormID merge.
    ///     Empty (no skips) by default. The WRLD record itself isn't actively
    ///     emitted, so removing its child cells removes our entire footprint there.
    /// </summary>
    public IReadOnlySet<uint> SkipWorldspaceFormIds { get; init; } = new HashSet<uint>();

    /// <summary>
    ///     Diagnostic: top-level record-type signatures (e.g. "STAT", "NPC_", "WEAP")
    ///     that the converter should skip entirely. The DMP-parsed records for these
    ///     types get dropped from EnumerateModelsByType, so neither overrides nor new
    ///     records of that type appear in the output ESM. Master's records remain in
    ///     effect via per-FormID merge for overrides; new records simply aren't emitted.
    ///     Used to bisect crashes that point at a specific record type.
    /// </summary>
    public IReadOnlySet<string> SkipRecordTypes { get; init; } =
        new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    ///     Diagnostic: exact DMP override FormIDs that the planner must leave master-pure.
    ///     The first-priority disposition policy converts the matching override to
    ///     <c>KeepMaster</c>; a missing master/override is rejected before emission so a
    ///     mistyped ID cannot silently produce a misleading bisect artifact.
    /// </summary>
    public ImmutableHashSet<uint> DiagnosticKeepMasterFormIds { get; init; } =
        ImmutableHashSet<uint>.Empty;

    /// <summary>
    ///     Diagnostic: exact override FormID to master subrecord signatures that must be
    ///     retained verbatim during the planner merge. Both the positional replacement and
    ///     append passes suppress the DMP signature, preventing duplicate subrecords.
    /// </summary>
    public ImmutableDictionary<uint, ImmutableHashSet<string>> DiagnosticRetainMasterSubrecords { get; init; } =
        ImmutableDictionary<uint, ImmutableHashSet<string>>.Empty;

    /// <summary>
    ///     Optional Bethesda Audio Transcriber CSV exports used to backfill INFO response
    ///     text (NAM1) when the DMP capture left a response blank or marked it as the
    ///     placeholder "(NOT FOUND IN CRASH DUMP)". Each CSV row carries one response
    ///     (FormID + Text + voice-file-path-with-response-index), so the backfill can
    ///     map each row to the right response slot. CSVs are read in priority order;
    ///     first non-empty Text per (FormID, ResponseNumber) wins. Same CSV may also
    ///     be passed to <see cref="AssetPacking.AssetPackingOptions.DialogueAudioCsvPaths" />
    ///     so the resulting voice audio gets packed into the BSA alongside the patched text.
    /// </summary>
    public IReadOnlyList<string> DialogueTextOverridesCsvPaths { get; init; } = [];

    // PlannerEnabledRecordTypes was removed 2026-08-11 with the legacy emission path: the
    // planner owns every record type unconditionally, so there is nothing left to select.
}
