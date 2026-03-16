namespace FalloutXbox360Utils.Core.Minidump;

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
