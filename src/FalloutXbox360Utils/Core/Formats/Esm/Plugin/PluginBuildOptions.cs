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
}
