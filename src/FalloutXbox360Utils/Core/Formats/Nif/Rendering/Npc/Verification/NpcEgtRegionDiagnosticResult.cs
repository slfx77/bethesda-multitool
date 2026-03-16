namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Verification;

internal sealed record NpcEgtRegionDiagnosticResult
{
    public required uint FormId { get; init; }
    public required string PluginName { get; init; }
    public string? EditorId { get; init; }
    public string? FullName { get; init; }
    public required string ShippedTexturePath { get; init; }
    public string? BaseTexturePath { get; init; }
    public string? EgtPath { get; init; }
    public string? ComparisonMode { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public double MeanAbsoluteRgbError { get; init; }
    public double RootMeanSquareRgbError { get; init; }
    public int MaxAbsoluteRgbError { get; init; }
    public string? FailureReason { get; init; }

    public bool Verified => FailureReason == null;
}