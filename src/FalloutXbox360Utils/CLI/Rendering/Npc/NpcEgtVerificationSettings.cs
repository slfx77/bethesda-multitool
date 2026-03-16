namespace FalloutXbox360Utils.CLI.Rendering.Npc;

internal sealed class NpcEgtVerificationSettings
{
    public required string MeshesBsaPath { get; init; }
    public string[]? ExtraMeshesBsaPaths { get; init; }
    public required string EsmPath { get; init; }
    public string[]? ExplicitTexturesBsaPaths { get; init; }
    public string[]? NpcFilters { get; init; }
    public int? Limit { get; init; }
    public int TopCount { get; init; } = 10;
    public string? ReportPath { get; init; }
    public string? ImageOutputDir { get; init; }
    public float RmsClampThreshold { get; init; }
}
