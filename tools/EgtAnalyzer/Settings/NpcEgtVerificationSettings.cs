namespace EgtAnalyzer.Settings;

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
    public string? VariantReportPath { get; init; }
    public string? ImageOutputDir { get; init; }
    public float RmsClampThreshold { get; init; }
    public bool RawDeltaCoefficientFit { get; init; }
    public bool RawFitProvenancePca { get; init; }
    public bool ResidualProjection { get; init; }
    public int[]? ResidualSubspaceIndices { get; init; }
    public int[]? InspectMorphIndices { get; init; }
    public bool InspectMorphSummaryOnly { get; init; }
    public bool MorphStructure { get; init; }
}
