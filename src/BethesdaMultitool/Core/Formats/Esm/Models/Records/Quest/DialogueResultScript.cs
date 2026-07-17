namespace BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;

/// <summary>
///     Parsed INFO result-script block.
/// </summary>
public record DialogueResultScript
{
    /// <summary>Source text from SCTX, when present.</summary>
    public string? SourceText { get; init; }

    /// <summary>Where the recovered SCTX came from within the current dump.</summary>
    public ScriptSourceTextOrigin SourceTextOrigin { get; init; }

    /// <summary>
    ///     True for inline script material recovered from a minidump. Clean on-disk ESM
    ///     source is intentionally exempt from same-dump source/bytecode proof.
    /// </summary>
    public bool IsDmpDerived { get; init; }

    /// <summary>Decompiled bytecode from SCDA, when source text is unavailable.</summary>
    public string? DecompiledText { get; init; }

    /// <summary>Raw compiled bytecode from SCDA.</summary>
    public byte[]? CompiledData { get; init; }

    /// <summary>Ordered local-variable table from SLSD/SCVR pairs.</summary>
    public List<ScriptVariableInfo> Variables { get; init; } = [];

    /// <summary>
    ///     Ordered SCRO/SCRV table. SCRV entries carry the high-bit marker used by
    ///     <see cref="ScriptRecord.ReferencedObjects" />; bytecode indices address this
    ///     mixed table, so duplicates and ordering are significant.
    /// </summary>
    public List<uint> ReferencedObjects { get; init; } = [];

    /// <summary>Whether this script block ended with a NEXT separator.</summary>
    public bool HasNextSeparator { get; init; }

    /// <summary>
    ///     True when <see cref="CompiledData" /> holds Xbox 360 (big-endian) bytecode and
    ///     must be byte-swapped before being emitted to a PC ESP. Set by parsers from the
    ///     containing record's endianness flag; false by default for tests and any LE source.
    /// </summary>
    public bool IsBigEndianBytecode { get; init; }

    /// <summary>
    ///     True when the runtime object declared executable content but its SCDA/local/
    ///     reference bundle could not be captured atomically. The owning INFO/PACK/TERM
    ///     must be retained from master or suppressed; source text alone is not a safe
    ///     replacement for a partially captured executable script.
    /// </summary>
    public bool IsIncompleteExecutableBundle { get; init; }

    /// <summary>Whether any script content was recovered.</summary>
    public bool HasContent =>
        !string.IsNullOrEmpty(SourceText) ||
        !string.IsNullOrEmpty(DecompiledText) ||
        CompiledData is { Length: > 0 } ||
        Variables.Count > 0 ||
        ReferencedObjects.Count > 0 ||
        IsIncompleteExecutableBundle;
}
