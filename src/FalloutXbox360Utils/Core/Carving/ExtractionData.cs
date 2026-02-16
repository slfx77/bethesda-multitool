namespace FalloutXbox360Utils.Core.Carving;

/// <summary>
///     Data prepared for file extraction.
/// </summary>
internal readonly record struct ExtractionData(
    string OutputFile,
    byte[] Data,
    int FileSize,
    string? OriginalPath,
    Dictionary<string, object>? Metadata,
    bool IsTruncated = false,
    double Coverage = 1.0);
