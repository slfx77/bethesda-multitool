namespace FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;

/// <summary>
///     Parsed INFO result-script block.
/// </summary>
public record DialogueResultScript
{
    /// <summary>Source text from SCTX, when present.</summary>
    public string? SourceText { get; init; }

    /// <summary>Decompiled bytecode from SCDA, when source text is unavailable.</summary>
    public string? DecompiledText { get; init; }

    /// <summary>Raw compiled bytecode from SCDA.</summary>
    public byte[]? CompiledData { get; init; }

    /// <summary>Referenced FormIDs from SCRO subrecords.</summary>
    public List<uint> ReferencedObjects { get; init; } = [];

    /// <summary>Whether this script block ended with a NEXT separator.</summary>
    public bool HasNextSeparator { get; init; }

    /// <summary>Whether any script content was recovered.</summary>
    public bool HasContent =>
        !string.IsNullOrEmpty(SourceText) ||
        !string.IsNullOrEmpty(DecompiledText) ||
        CompiledData is { Length: > 0 } ||
        ReferencedObjects.Count > 0;
}
