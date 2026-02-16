namespace FalloutXbox360Utils.Core.Carving;

/// <summary>
///     Parameters for file write operations.
/// </summary>
internal sealed record WriteFileParams(
    string OutputFile,
    byte[] Data,
    long Offset,
    string SignatureId,
    int FileSize,
    string? OriginalPath,
    Dictionary<string, object>? Metadata,
    bool IsTruncated = false,
    double Coverage = 1.0);
