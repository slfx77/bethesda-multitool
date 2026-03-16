namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Verification;

internal sealed record NpcEgtBlendInvestigationResult
{
    public required uint FormId { get; init; }
    public required string PluginName { get; init; }
    public string? EditorId { get; init; }
    public string? FullName { get; init; }
    public required string ShippedTexturePath { get; init; }
    public string? BaseTexturePath { get; init; }
    public string? EgtPath { get; init; }
    public string? ComparisonMode { get; init; }
    public int ShippedWidth { get; init; }
    public int ShippedHeight { get; init; }
    public int AppliedDiffuseWidth { get; init; }
    public int AppliedDiffuseHeight { get; init; }
    public double CurrentVsRecoveredAppliedDiffuseMeanAbsoluteRgbError { get; init; }
    public double CurrentVsRecoveredAppliedDiffuseRootMeanSquareRgbError { get; init; }
    public int CurrentVsRecoveredAppliedDiffuseMaxAbsoluteRgbError { get; init; }
    public string? FailureReason { get; init; }

    public bool Verified => FailureReason == null;
}