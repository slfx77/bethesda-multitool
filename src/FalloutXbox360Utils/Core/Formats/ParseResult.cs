namespace FalloutXbox360Utils.Core.Formats;

/// <summary>
///     Result from parsing a file header.
/// </summary>
public sealed class ParseResult
{
    /// <summary>
    ///     Format identifier (e.g., "DDS", "XMA2").
    /// </summary>
    public required string Format { get; init; }

    /// <summary>
    ///     Estimated size of the complete file in bytes.
    /// </summary>
    public int EstimatedSize { get; init; }

    /// <summary>
    ///     Optional filename extracted from file metadata.
    /// </summary>
    public string? FileName { get; init; }

    /// <summary>
    ///     Optional override for the output folder (e.g., "anims" vs "meshes" for NIF files).
    ///     If null, the format's default OutputFolder is used.
    /// </summary>
    public string? OutputFolderOverride { get; init; }

    /// <summary>
    ///     Optional override for the file extension (e.g., ".kf" for animations instead of ".nif").
    ///     If null, the format's default Extension is used.
    /// </summary>
    public string? ExtensionOverride { get; init; }

    /// <summary>
    ///     Additional metadata (dimensions, format details, flags, etc.).
    /// </summary>
    public Dictionary<string, object> Metadata { get; init; } = [];
}
