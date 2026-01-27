namespace Xbox360MemoryCarver.Core.Converters.Esm;

/// <summary>
///     Record information for ESM conversion.
/// </summary>
internal sealed record ConverterRecordInfo
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
    public bool IsCompressed => (Flags & EsmConverterConstants.CompressedFlag) != 0;
}
