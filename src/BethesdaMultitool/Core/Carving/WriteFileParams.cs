namespace BethesdaMultitool.Core.Carving;

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
    CarveResidency? Residency = null)
{
    /// <summary>Residency of the carved bytes; a missing value means fully resident.</summary>
    public CarveResidency Coverage => Residency ?? CarveResidency.Complete;
}
