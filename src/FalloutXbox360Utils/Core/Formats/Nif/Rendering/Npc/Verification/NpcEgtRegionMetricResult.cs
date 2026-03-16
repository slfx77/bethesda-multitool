namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Verification;

internal sealed record NpcEgtRegionMetricResult
{
    public required string RegionName { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public double MeanAbsoluteRgbError { get; init; }
    public double RootMeanSquareRgbError { get; init; }
    public int MaxAbsoluteRgbError { get; init; }
    public double MeanSignedRedError { get; init; }
    public double MeanSignedGreenError { get; init; }
    public double MeanSignedBlueError { get; init; }
}