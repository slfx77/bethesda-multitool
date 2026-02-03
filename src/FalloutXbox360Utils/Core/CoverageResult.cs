namespace FalloutXbox360Utils.Core;

public sealed class CoverageResult
{
    public string? Error { get; init; }
    public long FileSize { get; init; }
    public int TotalMemoryRegions { get; init; }
    public long TotalRegionBytes { get; init; }
    public long MinidumpOverhead { get; init; }
    public long TotalRecognizedBytes { get; init; }
    public Dictionary<CoverageCategory, long> CategoryBytes { get; init; } = [];
    public List<CoverageGap> Gaps { get; init; } = [];

    /// <summary>PDB-guided analysis result (null if no PDB provided).</summary>
    public PdbAnalysisResult? PdbAnalysis { get; set; }

    public long TotalGapBytes => Gaps.Sum(g => g.Size);
    public double RecognizedPercent => TotalRegionBytes > 0 ? TotalRecognizedBytes * 100.0 / TotalRegionBytes : 0;
    public double GapPercent => TotalRegionBytes > 0 ? TotalGapBytes * 100.0 / TotalRegionBytes : 0;
}