namespace FalloutXbox360Utils.Core.Formats.Esm.Conversion;

/// <summary>
///     Subrecord information for analysis.
/// </summary>
public sealed record AnalyzerSubrecordInfo
{
    public required string Signature { get; init; }
    public required byte[] Data { get; init; }
    public required int Offset { get; init; }
}
