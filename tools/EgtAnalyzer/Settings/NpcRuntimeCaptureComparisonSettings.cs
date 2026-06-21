namespace EgtAnalyzer.Settings;

internal sealed record NpcRuntimeCaptureComparisonSettings
{
    public required string EsmPath { get; init; }
    public required string[] CaptureDirs { get; init; }
    public required string OutputDir { get; init; }
}
