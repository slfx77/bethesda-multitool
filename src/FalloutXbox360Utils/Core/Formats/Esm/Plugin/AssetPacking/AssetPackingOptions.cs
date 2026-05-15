namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.AssetPacking;

/// <summary>
///     One secondary data folder to be searched for missing assets, with a flag
///     indicating whether its contents need on-the-fly 360→PC conversion.
/// </summary>
public sealed record SecondaryDataFolder
{
    public required string Path { get; init; }

    /// <summary>
    ///     If true, BSAs in this folder are Xbox 360 format and per-asset bytes
    ///     are passed through <c>PrototypeAssetConverter</c> on resolve.
    /// </summary>
    public bool IsXbox360Format { get; init; }
}

/// <summary>
///     Inputs to <c>AssetPackingService</c>.
/// </summary>
public sealed record AssetPackingOptions
{
    /// <summary>Path to the converted ESP whose record fields will be scanned.</summary>
    public required string ConvertedEspPath { get; init; }

    /// <summary>
    ///     Optional path to the source DMP. When set, <c>RuntimeBufferStringExtractor</c>
    ///     is run against the dump to collect raw asset path strings the ESP doesn't expose.
    /// </summary>
    public string? DmpPath { get; init; }

    /// <summary>
    ///     The user's primary FNV PC Data folder. Anything already resolvable here is
    ///     considered "already available to the runtime" and skipped — only assets the
    ///     baseline lacks are packed into the output BSA.
    /// </summary>
    public required string BaselineDataFolder { get; init; }

    /// <summary>
    ///     Secondary data folders to search in priority order. The first folder whose
    ///     index resolves the requested path wins.
    /// </summary>
    public required IReadOnlyList<SecondaryDataFolder> SecondaryDataFolders { get; init; }

    /// <summary>Output BSA path. Will be overwritten if it already exists.</summary>
    public required string OutputBsaPath { get; init; }

    /// <summary>
    ///     If true, fuzzy basename matches (path moved between builds) are packed.
    ///     If false, only exact path matches contribute. Defaults to true.
    /// </summary>
    public bool IncludeFuzzyMatches { get; init; } = true;

    /// <summary>
    ///     If true, every resolved/missing path emits its own progress event.
    ///     If false (default), only summary counts are emitted.
    /// </summary>
    public bool VerbosePerAsset { get; init; }

    /// <summary>
    ///     If true, write a human-reviewable per-asset audit text file at
    ///     <c>&lt;OutputBsaPath&gt;.missing.txt</c> with sections for missing, fuzzy-matched,
    ///     and conversion-failed paths. Defaults to false — opt-in so the CLI/GUI can offer
    ///     it as a deliberate choice.
    /// </summary>
    public bool WriteAuditFile { get; init; }

    /// <summary>
    ///     If true, the resolver consults secondary data folders before the FNV baseline.
    ///     When a secondary has an asset that the baseline also has, the secondary's bytes
    ///     are packed and the baseline copy is overridden at runtime. Defaults to false,
    ///     which preserves the safer "baseline wins; only pack what the runtime is missing"
    ///     behavior.
    /// </summary>
    public bool OverrideVanillaBaseline { get; init; }
}
