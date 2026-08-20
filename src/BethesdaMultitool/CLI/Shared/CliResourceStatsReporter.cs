using System.Diagnostics;
using System.Globalization;
using BethesdaMultitool.Core.Diagnostics;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Shared;

/// <summary>
///     End-of-run resource statistics for CLI commands: one table of every resource registered with
///     the <see cref="ResourceRegistry" /> plus a process summary line. Opt-in via
///     <c>--resource-stats</c> or <c>FALLOUT_RESOURCE_STATS=1</c>; printed once after the command
///     completes (a one-shot CLI process has no point-in-time to poll, so end-of-run is the useful
///     snapshot).
/// </summary>
internal static class CliResourceStatsReporter
{
    /// <summary>
    ///     Prints the resource table if anything registered during the run; otherwise stays silent.
    ///     Includes resources already disposed by command teardown (marked <c>(disposed)</c>) — their
    ///     final stats are what an end-of-run report is for.
    /// </summary>
    internal static void WriteIfAnyRegistered(ResourceRegistry registry)
    {
        var live = registry.GetSnapshot();
        var retired = registry.GetRetiredSnapshot();
        var snapshot = live
            .Concat(retired.Select(static r => r with
            {
                DisplayName = r.RunCount > 1
                    ? $"{r.DisplayName} (disposed ×{r.RunCount.ToString("N0", CultureInfo.InvariantCulture)})"
                    : r.DisplayName + " (disposed)"
            }))
            .ToList();
        if (snapshot.Count == 0)
        {
            return;
        }

        AnsiConsole.WriteLine();
        var table = new Table { Title = new TableTitle("Resource statistics") };
        table.AddColumn("Name");
        table.AddColumn("Category");
        table.AddColumn(new TableColumn("Bytes").RightAligned());
        table.AddColumn(new TableColumn("Entries").RightAligned());
        table.AddColumn(new TableColumn("Hit %").RightAligned());
        table.AddColumn(new TableColumn("Evict").RightAligned());
        table.AddColumn(new TableColumn("Depth").RightAligned());
        table.AddColumn(new TableColumn("Processed").RightAligned());

        foreach (var row in snapshot.OrderBy(static r => r.Category)
                     .ThenBy(static r => r.DisplayName, StringComparer.Ordinal))
        {
            var s = row.Stats;
            var isQueue = row.Category == ResourceCategory.Queue;
            table.AddRow(
                Markup.Escape(row.DisplayName),
                Markup.Escape(CategoryLabel(row.Category)),
                isQueue ? "" : FormatBytes(s.EstimatedBytes),
                isQueue ? "" : s.EntryCount.ToString("N0", CultureInfo.InvariantCulture),
                s.HitRate is { } rate ? (rate * 100).ToString("F1", CultureInfo.InvariantCulture) : "",
                isQueue ? "" : s.Evictions.ToString("N0", CultureInfo.InvariantCulture),
                isQueue ? s.QueueDepth.ToString("N0", CultureInfo.InvariantCulture) : "",
                isQueue ? s.Processed.ToString("N0", CultureInfo.InvariantCulture) : "");
        }

        AnsiConsole.Write(table);

        using var process = Process.GetCurrentProcess();
        var gcInfo = GC.GetGCMemoryInfo();
        AnsiConsole.MarkupLine(
            "[grey]Process: WS {0} · GC heap {1} · Gen0/1/2 {2}/{3}/{4} · registered CPU-cache total {5}[/]",
            FormatBytes(process.WorkingSet64),
            FormatBytes(gcInfo.HeapSizeBytes),
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
            FormatBytes(registry.TotalTrackedBytes(ResourceCategory.CpuCache)));
    }

    internal static string CategoryLabel(ResourceCategory category)
    {
        return category switch
        {
            ResourceCategory.CpuCache => "CPU cache",
            ResourceCategory.GpuResident => "GPU",
            ResourceCategory.GpuMeta => "GPU meta",
            ResourceCategory.DiskCache => "Disk cache",
            ResourceCategory.MappedFile => "Mapped file",
            ResourceCategory.Queue => "Queue",
            ResourceCategory.SessionScope => "Session",
            _ => category.ToString()
        };
    }

    internal static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            >= 1024L * 1024 * 1024 => (bytes / (1024.0 * 1024 * 1024)).ToString("F2", CultureInfo.InvariantCulture) +
                                      " GB",
            >= 1024L * 1024 => (bytes / (1024.0 * 1024)).ToString("F1", CultureInfo.InvariantCulture) + " MB",
            >= 1024L => (bytes / 1024.0).ToString("F1", CultureInfo.InvariantCulture) + " KB",
            _ => bytes.ToString("N0", CultureInfo.InvariantCulture) + " B"
        };
    }
}
