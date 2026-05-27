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
    ///     Dialogue CSV matching also uses this to recover source INFO FormIDs for newly
    ///     allocated dialogue records.
    /// </summary>
    public string? DmpPath { get; init; }

    /// <summary>
    ///     Optional Fallout Audio Transcriber CSV exports. Rows whose FormID matches an
    ///     INFO in the converted ESP or source DMP contribute dialogue voice .ogg/.lip
    ///     requests to the packer. The referenced audio bytes must still be resolvable
    ///     through <see cref="SecondaryDataFolders" />.
    /// </summary>
    public IReadOnlyList<string> DialogueAudioCsvPaths { get; init; } = [];

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

    /// <summary>
    ///     Source-DMP FormID → emitted-ESP FormID alias map. When a CSV dialogue audio row's
    ///     FormID is in this map, the corresponding voice-file path gets rewritten so the
    ///     engine can find it: the master ESM directory (<c>sound\voice\falloutnv.esm\…</c>)
    ///     becomes the new ESP's directory, and the filename's source-FormID hex becomes the
    ///     emitted FormID's bottom 24 bits (matching the engine's runtime lookup pattern).
    ///     Empty by default — when empty, the asset packer falls back to the old verbatim
    ///     behavior (which only finds voice files for unchanged master overrides).
    /// </summary>
    public IReadOnlyDictionary<uint, uint> NewRecordSourceToAllocatedFormIds { get; init; } =
        new Dictionary<uint, uint>();

    /// <summary>
    ///     Per-emitted-INFO×response triple-key bindings (voice type EDID, parent DIAL
    ///     EDID, response number) used as a fallback when the CSV's FormID does not match
    ///     the converter's source FormIDs (build-era drift). The asset packer parses each
    ///     CSV row's <c>File</c> path to extract the same triple and looks up against this
    ///     index, then rewrites the pack path using the binding's allocated FormID so the
    ///     engine can find the audio at runtime. Empty by default.
    /// </summary>
    public IReadOnlyList<EmittedDialogueAudioBinding> EmittedDialogueAudioBindings { get; init; } = [];
}
