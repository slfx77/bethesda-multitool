namespace BethesdaMultitool.Core.Coverage;

/// <summary>A half-open file-offset range [Start, End) tagged with the coverage category that claimed it.</summary>
public record CoverageInterval(long Start, long End, CoverageCategory Category);
