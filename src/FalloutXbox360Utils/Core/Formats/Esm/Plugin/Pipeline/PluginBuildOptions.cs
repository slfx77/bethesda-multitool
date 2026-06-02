using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.AssetPacking;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Reference;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Pipeline;

/// <summary>
///     Options controlling plugin ESP construction from a DMP + base ESM.
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
    ///     When true, the converter emits DMP-captured NAVMs whose parent cell is itself a
    ///     master cell (master-cell augmentation). Defaults to false: master-cell augmentation
    ///     has historically surfaced a crucified-animation symptom even after the Phase 7b
    ///     <c>TesConditionListWalker</c> fix, and the NAVI override builder has not been
    ///     smoke-validated on the master-cell augmentation path. Set true only when running
    ///     a smoke build that intends to test that path.
    /// </summary>
    public bool EmitMasterCellNavmAugmentation { get; init; }

    /// <summary>
    ///     Starting local FormID for newly-allocated records. Defaults to 0x800 to match the
    ///     GECK convention that local IDs below 0x800 are reserved for the engine.
    /// </summary>
    public uint NewRecordBaseFormId { get; init; } = FormIdAllocator.DefaultBaseLocalId;

    /// <summary>
    ///     Asset-rename pass. When non-empty (and <see cref="AssetRenameBaselineFolder" />
    ///     is set), <c>PluginBuilder.BuildAsync</c> resolves every record-sourced asset path
    ///     against these folders before encoding. Paths that fuzzy-match to a differently-
    ///     named asset get their record field rewritten in-place so the output ESP carries
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
    ///     together so the rewritten ESP fields and the packed BSA agree on which secondary
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
    ///     records of that type appear in the output ESP. Master's records remain in
    ///     effect via per-FormID merge for overrides; new records simply aren't emitted.
    ///     Used to bisect crashes that point at a specific record type.
    /// </summary>
    public IReadOnlySet<string> SkipRecordTypes { get; init; } =
        new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    ///     Optional Fallout Audio Transcriber CSV exports used to backfill INFO response
    ///     text (NAM1) when the DMP capture left a response blank or marked it as the
    ///     placeholder "(NOT FOUND IN CRASH DUMP)". Each CSV row carries one response
    ///     (FormID + Text + voice-file-path-with-response-index), so the backfill can
    ///     map each row to the right response slot. CSVs are read in priority order;
    ///     first non-empty Text per (FormID, ResponseNumber) wins. Same CSV may also
    ///     be passed to <see cref="AssetPacking.AssetPackingOptions.DialogueAudioCsvPaths" />
    ///     so the resulting voice audio gets packed into the BSA alongside the patched text.
    /// </summary>
    public IReadOnlyList<string> DialogueTextOverridesCsvPaths { get; init; } = [];

    /// <summary>
    ///     Record signatures the new two-pass <c>EsmPlanner</c> / <c>PlanWriter</c> pipeline
    ///     owns on this run. When non-empty, the plugin builder routes those record types
    ///     through the planner; everything else continues to use the legacy single-pass
    ///     pipeline. Empty (default) keeps the entire build on the legacy path — Tier 0
    ///     ships the plumbing without enabling any types; tiers 1–5 progressively add types
    ///     here until the legacy pipeline is deleted.
    /// </summary>
    /// <remarks>
    ///     Contract: if a type is listed here, the planner produces all bytes for that type's
    ///     GRUP — legacy never emits even one record for it. This is the per-record-type
    ///     migration switch described in the planner architecture plan.
    /// </remarks>
    public IReadOnlySet<string> PlannerEnabledRecordTypes { get; init; } =
        new HashSet<string>(StringComparer.Ordinal);
}
