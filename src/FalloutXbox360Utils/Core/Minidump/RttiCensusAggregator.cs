namespace FalloutXbox360Utils.Core.Minidump;

/// <summary>
///     Per-dump census result with metadata about the dump file.
/// </summary>
public sealed class DumpCensusResult
{
    public required string FileName { get; init; }
    public required string FilePath { get; init; }
    public string? BuildType { get; init; }
    public uint? PeTimestamp { get; init; }
    public int ClassCount { get; init; }
    public int TotalInstances { get; init; }
    public required List<CensusEntry> Entries { get; init; }
}

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

/// <summary>
///     Top-level aggregated census report across all DMP files.
/// </summary>
public sealed class AggregatedCensusReport
{
    public int TotalDumps { get; init; }
    public int TotalClasses { get; init; }
    public long TotalInstances { get; init; }
    public int TesFormClasses { get; init; }
    public required List<AggregatedCensusEntry> Classes { get; init; }
    public required List<DumpCensusResult> Dumps { get; init; }
}

/// <summary>
///     Aggregates per-dump RTTI census results into a single cross-dump report.
///     Groups by demangled class name (build-independent) since vtable addresses
///     differ across builds.
/// </summary>
public static class RttiCensusAggregator
{
    public static AggregatedCensusReport Aggregate(List<DumpCensusResult> dumpResults)
    {
        var byClassName = new Dictionary<string, AggregatedCensusEntry>();

        foreach (var dump in dumpResults)
        {
            var buildType = dump.BuildType ?? "Unknown";

            // Track which classes we've already counted for this dump
            // (a class may appear multiple times due to MI secondary vtables)
            var seenInDump = new HashSet<string>();

            foreach (var entry in dump.Entries)
            {
                var className = entry.Rtti.ClassName;

                if (!byClassName.TryGetValue(className, out var agg))
                {
                    agg = new AggregatedCensusEntry
                    {
                        ClassName = className,
                        IsTesForm = entry.IsTesForm
                    };
                    byClassName[className] = agg;
                }

                // Only count primary vtable entries (offset 0) or if this is the first
                // occurrence of this class in this dump, to avoid double-counting MI vtables
                if (!seenInDump.Add(className))
                {
                    continue;
                }

                agg.TotalInstances += entry.InstanceCount;
                agg.DumpsPresent++;
                agg.InstancesByDump[dump.FileName] = entry.InstanceCount;

                if (agg.InstancesByBuildType.TryGetValue(buildType, out var count))
                {
                    agg.InstancesByBuildType[buildType] = count + entry.InstanceCount;
                }
                else
                {
                    agg.InstancesByBuildType[buildType] = entry.InstanceCount;
                }

                if (entry.IsTesForm)
                {
                    agg.IsTesForm = true;
                }

                if (agg.BaseClassNames == null && entry.Rtti.BaseClasses is { Count: > 1 })
                {
                    agg.BaseClassNames = entry.Rtti.BaseClasses
                        .Skip(1)
                        .Select(b => b.ClassName)
                        .ToList();
                }
            }
        }

        var sorted = byClassName.Values
            .OrderByDescending(e => e.TotalInstances)
            .ToList();

        return new AggregatedCensusReport
        {
            TotalDumps = dumpResults.Count,
            TotalClasses = sorted.Count,
            TotalInstances = sorted.Sum(e => (long)e.TotalInstances),
            TesFormClasses = sorted.Count(e => e.IsTesForm),
            Classes = sorted,
            Dumps = dumpResults
        };
    }
}
