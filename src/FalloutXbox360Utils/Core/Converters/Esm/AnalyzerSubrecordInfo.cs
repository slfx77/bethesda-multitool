namespace FalloutXbox360Utils.Core.Converters.Esm;

/// <summary>
///     Subrecord information for analysis.
/// </summary>
internal sealed record AnalyzerSubrecordInfo
{
    public required string Signature { get; init; }
    public required byte[] Data { get; init; }
    public required int Offset { get; init; }
}
