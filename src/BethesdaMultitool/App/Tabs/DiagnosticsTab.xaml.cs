using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using BethesdaMultitool.CLI.Shared;
using BethesdaMultitool.Core.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Windows.ApplicationModel.DataTransfer;

namespace BethesdaMultitool;

/// <summary>
///     Live diagnostics panel: every cache, queue, and session scope registered with the
///     <see cref="ResourceRegistry" />, grouped by category, plus process-level memory stats.
///     Refreshes once per second while visible; the timer stops when the tab is hidden or paused,
///     so the panel costs nothing while unobserved. Row values update in place (scroll position is
///     preserved); the grouped source is rebuilt only when resources register or unregister.
/// </summary>
public sealed partial class DiagnosticsTab : UserControl
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);
    private readonly CollectionViewSource _groupedSource;

    // Persistent grouped source, bound to the ListView ONCE. Each refresh reconciles rows and
    // groups in place (add new, remove vanished, update values) instead of replacing ItemsSource —
    // so the scroll position survives. Transient ParallelWork rows register/unregister every tick,
    // which previously churned the row set and triggered a full ItemsSource rebuild (scroll-to-top)
    // on every refresh. The CollectionViewSource is held in a field so it is not collected.
    private readonly ObservableCollection<DiagnosticsGroup> _groups = [];

    private readonly DispatcherQueueTimer _refreshTimer;
    private readonly Dictionary<string, ResourceStatRow> _rows = new(StringComparer.Ordinal);

    public DiagnosticsTab()
    {
        InitializeComponent();

        _groupedSource = new CollectionViewSource { IsSourceGrouped = true, Source = _groups };
        ResourceListView.ItemsSource = _groupedSource.View;

        _refreshTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _refreshTimer.Interval = RefreshInterval;
        _refreshTimer.IsRepeating = true;
        _refreshTimer.Tick += (_, _) => Refresh();

        // Start/stop with visibility so a hidden tab costs nothing.
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => UpdateTimerState());
        UpdateTimerState();
    }

    private void UpdateTimerState()
    {
        var shouldRun = Visibility == Visibility.Visible && PauseRefreshToggle?.IsChecked != true;
        if (shouldRun)
        {
            Refresh();
            _refreshTimer.Start();
        }
        else
        {
            _refreshTimer.Stop();
        }
    }

    private void PauseRefresh_Changed(object sender, RoutedEventArgs e) => UpdateTimerState();

    private void Refresh()
    {
        // One walk per tick: the rows and the category totals come from the same snapshot. Taking
        // them separately called every ITrackableResource.GetStats() twice a second.
        var snapshot = ResourceRegistry.Instance.Snapshot();

        UpdateProcessStats(snapshot);

        // Reconcile in place: update existing rows' values, add new rows into their (sorted) group,
        // remove vanished rows (and their group when it empties). No ItemsSource replacement, so the
        // scroll position is preserved even as transient ParallelWork rows come and go.
        var seen = new HashSet<string>(_rows.Count, StringComparer.Ordinal);
        foreach (var record in snapshot.Rows)
        {
            seen.Add(record.DisplayName);
            if (_rows.TryGetValue(record.DisplayName, out var row))
            {
                row.Update(record.Stats);
            }
            else
            {
                row = new ResourceStatRow(record.DisplayName, record.Category);
                row.Update(record.Stats);
                _rows[record.DisplayName] = row;
                InsertRow(row);
            }
        }

        // Sweep unconditionally. This used to be guarded by `_rows.Count != seen.Count`, which fails
        // on the routine case of one resource registering while another unregisters in the same tick
        // (transient ParallelWork rows churn constantly): the counts stay equal, the sweep is
        // skipped, and the dead row never leaves — it sits there showing frozen values forever.
        foreach (var name in _rows.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            RemoveRow(name);
        }
    }

    /// <summary>Inserts a new row into its category group (creating the group in category order), kept name-sorted.</summary>
    private void InsertRow(ResourceStatRow row)
    {
        var group = FindOrCreateGroup(row.Category, row.CategoryLabel);
        var index = 0;
        while (index < group.Count && string.CompareOrdinal(group[index].Name, row.Name) < 0)
        {
            index++;
        }

        group.Insert(index, row);
    }

    private DiagnosticsGroup FindOrCreateGroup(ResourceCategory category, string label)
    {
        var index = 0;
        foreach (var existing in _groups)
        {
            if (existing.Category == category)
            {
                return existing;
            }

            if (existing.Category < category)
            {
                index++;
            }
        }

        var group = new DiagnosticsGroup(label, category);
        _groups.Insert(index, group);
        return group;
    }

    private void RemoveRow(string name)
    {
        if (!_rows.TryGetValue(name, out var row))
        {
            return;
        }

        _rows.Remove(name);
        foreach (var group in _groups)
        {
            if (group.Category == row.Category && group.Remove(row))
            {
                if (group.Count == 0)
                {
                    _groups.Remove(group);
                }

                return;
            }
        }
    }

    private void UpdateProcessStats(RegistrySnapshot snapshot)
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var gcInfo = GC.GetGCMemoryInfo();
        WorkingSetText.Text = CliResourceStatsReporter.FormatBytes(process.WorkingSet64);
        GcHeapText.Text = CliResourceStatsReporter.FormatBytes(gcInfo.HeapSizeBytes);
        GcCollectionsText.Text = string.Create(
            CultureInfo.InvariantCulture,
            $"{GC.CollectionCount(0)} / {GC.CollectionCount(1)} / {GC.CollectionCount(2)}");
        // Totals ride along on the snapshot already taken by Refresh — no second registry walk.
        TrackedTotalText.Text = CliResourceStatsReporter.FormatBytes(
            snapshot.TotalBytes(ResourceCategory.CpuCache));
    }

#pragma warning disable S2325 // XAML Click handlers must be instance methods for classic event wiring.
    private void CopySnapshot_Click(object sender, RoutedEventArgs e)
#pragma warning restore S2325
    {
        var snapshot = ResourceRegistry.Instance.GetSnapshot();
        var builder = new StringBuilder(snapshot.Count * 96 + 128);
        builder.AppendLine(
            "Name\tCategory\tBytes\tEntries\tHits\tMisses\tEvictions\tQueueDepth\tInFlight\tProcessed\tFailures");
        foreach (var record in snapshot)
        {
            var s = record.Stats;
            builder.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{record.DisplayName}\t{record.Category}\t{s.EstimatedBytes}\t{s.EntryCount}\t{s.Hits}\t{s.Misses}\t{s.Evictions}\t{s.QueueDepth}\t{s.InFlight}\t{s.Processed}\t{s.Failures}"));
        }

        var package = new DataPackage();
        package.SetText(builder.ToString());
        Clipboard.SetContent(package);
        MainWindow.Instance?.SetStatus($"Copied {snapshot.Count} resource rows to clipboard.");
    }
}

/// <summary>
///     One category group in the Diagnostics ListView: an observable collection of rows whose
///     <see cref="Key" /> labels the group header (bound in XAML) and whose <see cref="Category" />
///     orders the groups. Mutated in place by <see cref="DiagnosticsTab" />'s refresh so the grouped
///     ListView updates without losing scroll position.
/// </summary>
public sealed class DiagnosticsGroup : ObservableCollection<ResourceStatRow>
{
    internal DiagnosticsGroup(string key, ResourceCategory category)
    {
        Key = key;
        Category = category;
    }

    public string Key { get; }

    internal ResourceCategory Category { get; }
}
