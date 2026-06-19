using System.ComponentModel;
using System.Globalization;
using BethesdaMultitool.CLI.Shared;
using BethesdaMultitool.Core.Diagnostics;

namespace BethesdaMultitool;

/// <summary>
///     Display row for one registered resource in the Diagnostics tab. Mutable with change
///     notification so the 1 s refresh can update values in place — the grouped ListView source is
///     rebuilt only when the set of registered resources changes, preserving scroll position during
///     steady-state observation.
/// </summary>
public sealed class ResourceStatRow : INotifyPropertyChanged
{
    private string _bytesDisplay = "";
    private string _entriesDisplay = "";
    private string _hitRateDisplay = "";
    private string _evictionsDisplay = "";
    private string _queueDisplay = "";
    private string _processedDisplay = "";

    internal ResourceStatRow(string name, ResourceCategory category)
    {
        Name = name;
        Category = category;
        CategoryLabel = CliResourceStatsReporter.CategoryLabel(category);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; }

    internal ResourceCategory Category { get; }

    public string CategoryLabel { get; }

    public string BytesDisplay
    {
        get => _bytesDisplay;
        private set => SetField(ref _bytesDisplay, value, nameof(BytesDisplay));
    }

    public string EntriesDisplay
    {
        get => _entriesDisplay;
        private set => SetField(ref _entriesDisplay, value, nameof(EntriesDisplay));
    }

    public string HitRateDisplay
    {
        get => _hitRateDisplay;
        private set => SetField(ref _hitRateDisplay, value, nameof(HitRateDisplay));
    }

    public string EvictionsDisplay
    {
        get => _evictionsDisplay;
        private set => SetField(ref _evictionsDisplay, value, nameof(EvictionsDisplay));
    }

    public string QueueDisplay
    {
        get => _queueDisplay;
        private set => SetField(ref _queueDisplay, value, nameof(QueueDisplay));
    }

    public string ProcessedDisplay
    {
        get => _processedDisplay;
        private set => SetField(ref _processedDisplay, value, nameof(ProcessedDisplay));
    }

    internal void Update(ResourceStats stats)
    {
        var isQueue = Category == ResourceCategory.Queue;
        BytesDisplay = isQueue ? "" : CliResourceStatsReporter.FormatBytes(stats.EstimatedBytes);
        EntriesDisplay = isQueue ? "" : stats.EntryCount.ToString("N0", CultureInfo.InvariantCulture);
        HitRateDisplay = stats.HitRate is { } rate
            ? (rate * 100).ToString("F1", CultureInfo.InvariantCulture) + " %"
            : "";
        EvictionsDisplay = isQueue ? "" : stats.Evictions.ToString("N0", CultureInfo.InvariantCulture);
        QueueDisplay = isQueue
            ? string.Create(CultureInfo.InvariantCulture, $"{stats.QueueDepth} / {stats.InFlight}")
            : "";
        ProcessedDisplay = isQueue ? stats.Processed.ToString("N0", CultureInfo.InvariantCulture) : "";
    }

    private void SetField(ref string field, string value, string propertyName)
    {
        if (!string.Equals(field, value, StringComparison.Ordinal))
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
