using FalloutXbox360Utils.Core.Formats.Esm.Plugin.AssetPacking;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin;

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
    ///     Starting local FormID for newly-allocated records. Defaults to 0x800 to match the
    ///     GECK convention that local IDs below 0x800 are reserved for the engine.
    /// </summary>
    public uint NewRecordBaseFormId { get; init; } = FormIdAllocator.DefaultBaseLocalId;

    /// <summary>
    ///     v22 asset-rename pass. When non-empty (and <see cref="AssetRenameBaselineFolder" />
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
    ///     Diagnostic mode: when a CELL exists in master AND the DMP captured placements
    ///     for it, emit deletion overrides for every master <i>temporary</i> ref that
    ///     isn't in the DMP snapshot — so the in-game / GECK view of that cell shows
    ///     only the prototype's static placements (plus master persistent refs, which
    ///     stay untouched to avoid breaking quest scripts / enable-parent chains).
    ///     <br/>
    ///     Off by default. When on, this overrides the classifier's
    ///     <c>PersistentOnly</c> mode for DMP cells that captured placements, and
    ///     bypasses <c>PreserveMissingStructuralCellRefs</c> so structural markers /
    ///     doors / NAVMs not in the DMP also get wiped. Markers + fast-travel teleports
    ///     in affected cells may disappear; persistent refs (and therefore quest-bound
    ///     objects) are preserved.
    /// </summary>
    public bool ReplaceCellTemporariesOnOverride { get; init; }
}
