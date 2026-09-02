namespace BethesdaMultitool.Core.Formats.Esm.Conversion.Models;

/// <summary>
///     Extended record info with additional fields for analysis.
/// </summary>
public sealed record AnalyzerRecordInfo
{
    private const uint CompressedFlag = 0x00040000;

    public required string Signature { get; init; }
    public required uint FormId { get; init; }
    public required uint Flags { get; init; }
    public required uint DataSize { get; init; }
    public required uint Offset { get; init; }
    public required uint TotalSize { get; init; }

    /// <summary>
    ///     Size of the main-record header that precedes this record's data. Oblivion uses 20 bytes;
    ///     FO3 and later TES4-family plugins use 24. The default preserves compatibility for callers
    ///     that construct analysis records outside the format-aware file scanner.
    /// </summary>
    public int RecordHeaderSize { get; init; } = 24;

    /// <summary>
    ///     Checks if the record is compressed.
    /// </summary>
    public bool IsCompressed => (Flags & CompressedFlag) != 0;
}
