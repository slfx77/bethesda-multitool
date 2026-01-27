namespace Xbox360MemoryCarver.Core.Converters.Esm;

/// <summary>
///     Subrecord information for ESM conversion.
/// </summary>
internal sealed record ConverterSubrecordInfo
{
    public required string Signature { get; init; }
    public required byte[] Data { get; init; }
    public required int Offset { get; init; }
}
