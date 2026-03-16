namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Verification;

internal sealed record NpcEgtBlendInvestigationPolicyResult
{
    public required string PolicyKey { get; init; }
    public required string Description { get; init; }
    public required string CoefficientPolicy { get; init; }
    public required string AccumulationMode { get; init; }
    public required string EncodingMode { get; init; }
    public required string ComparisonMode { get; init; }
    public int TextureCoeffCount { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int AppliedDiffuseWidth { get; init; }
    public int AppliedDiffuseHeight { get; init; }
    public double MeanAbsoluteRgbError { get; init; }
    public double RootMeanSquareRgbError { get; init; }
    public int MaxAbsoluteRgbError { get; init; }
    public int PixelsWithAnyRgbDifference { get; init; }
    public int PixelsWithRgbErrorAbove1 { get; init; }
    public int PixelsWithRgbErrorAbove2 { get; init; }
    public int PixelsWithRgbErrorAbove4 { get; init; }
    public int PixelsWithRgbErrorAbove8 { get; init; }
}