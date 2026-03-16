namespace FalloutXbox360Utils.Core.Minidump;

/// <summary>
///     Aggregated RTTI census entry across multiple DMP files.
///     Keyed by class name since vtable addresses differ across builds.
/// </summary>
public sealed class AggregatedCensusEntry
{
    public required string ClassName { get; init; }
    public bool IsTesForm { get; set; }
    public int TotalInstances { get; set; }
    public int DumpsPresent { get; set; }

    /// <summary>Per-build-type instance counts (e.g., "Release Beta" → 150000).</summary>
    public Dictionary<string, int> InstancesByBuildType { get; } = new();

    /// <summary>Base classes from the first successful resolution.</summary>
    public List<string>? BaseClassNames { get; set; }

    /// <summary>Per-dump breakdown: file name → instance count.</summary>
    public Dictionary<string, int> InstancesByDump { get; } = new();
}