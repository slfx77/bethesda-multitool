namespace EsmAnalyzer.Core;

/// <summary>
///     Extended record info with additional fields for analysis.
/// </summary>
public sealed record AnalyzerRecordInfo
{
    public required string Signature { get; init; }
    public required uint FormId { get; init; }
    public required uint Flags { get; init; }
    public required uint DataSize { get; init; }
    public required uint Offset { get; init; }
    public required uint TotalSize { get; init; }

    /// <summary>
    ///     Checks if the record is compressed.
    /// </summary>
    public bool IsCompressed => (Flags & EsmConstants.CompressedFlag) != 0;
}