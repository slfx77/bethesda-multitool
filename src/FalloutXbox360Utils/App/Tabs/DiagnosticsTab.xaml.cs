using System.Globalization;
using System.Text;
using FalloutXbox360Utils.CLI.Shared;
using FalloutXbox360Utils.Core.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Windows.ApplicationModel.DataTransfer;

namespace FalloutXbox360Utils;

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

    private readonly DispatcherQueueTimer _refreshTimer;
    private readonly Dictionary<string, ResourceStatRow> _rows = new(StringComparer.Ordinal);

    public DiagnosticsTab()
    {
        InitializeComponent();

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
        var registry = ResourceRegistry.Instance;
        var snapshot = registry.GetSnapshot();

        UpdateProcessStats(registry);

        // In-place updates when the resource set is unchanged; rebuild the grouped source only on
        // registration churn (new sessions, disposed caches).
        var setChanged = snapshot.Count != _rows.Count ||
                         snapshot.Any(row => !_rows.ContainsKey(row.DisplayName));
        if (setChanged)
        {
            _rows.Clear();
            foreach (var record in snapshot)
            {
                _rows[record.DisplayName] = new ResourceStatRow(record.DisplayName, record.Category);
            }

            var grouped = _rows.Values
                .OrderBy(static r => r.Category)
                .ThenBy(static r => r.Name, StringComparer.Ordinal)
                .GroupBy(static r => r.CategoryLabel)
                .ToList();
            ResourceListView.ItemsSource = new CollectionViewSource
            {
                IsSourceGrouped = true,
                Source = grouped,
            }.View;
        }

        foreach (var record in snapshot)
        {
            if (_rows.TryGetValue(record.DisplayName, out var row))
            {
                row.Update(record.Stats);
            }
        }
    }

    private void UpdateProcessStats(ResourceRegistry registry)
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var gcInfo = GC.GetGCMemoryInfo();
        WorkingSetText.Text = CliResourceStatsReporter.FormatBytes(process.WorkingSet64);
        GcHeapText.Text = CliResourceStatsReporter.FormatBytes(gcInfo.HeapSizeBytes);
        GcCollectionsText.Text = string.Create(
            CultureInfo.InvariantCulture,
            $"{GC.CollectionCount(0)} / {GC.CollectionCount(1)} / {GC.CollectionCount(2)}");
        TrackedTotalText.Text = CliResourceStatsReporter.FormatBytes(
            registry.TotalTrackedBytes(ResourceCategory.CpuCache));
    }

#pragma warning disable S2325 // XAML Click handlers must be instance methods for classic event wiring.
    private void CopySnapshot_Click(object sender, RoutedEventArgs e)
#pragma warning restore S2325
    {
        var snapshot = ResourceRegistry.Instance.GetSnapshot();
        var builder = new StringBuilder(snapshot.Count * 96 + 128);
        builder.AppendLine("Name\tCategory\tBytes\tEntries\tHits\tMisses\tEvictions\tQueueDepth\tInFlight\tProcessed\tFailures");
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
