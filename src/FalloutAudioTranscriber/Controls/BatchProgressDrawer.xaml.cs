using System.Collections.ObjectModel;
using FalloutAudioTranscriber.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FalloutAudioTranscriber.Controls;

public sealed partial class BatchProgressDrawer : UserControl
{
    private readonly ObservableCollection<LogEntry> _logEntries = new();

    public BatchProgressDrawer()
    {
        InitializeComponent();
        LogListView.ItemsSource = _logEntries;
    }

    public event EventHandler? CancelRequested;

    public void StartBatch(int total, int workerCount)
    {
        _logEntries.Clear();

        SpinnerRing.Visibility = Visibility.Visible;
        SpinnerRing.IsActive = true;
        CompleteIcon.Visibility = Visibility.Collapsed;
        CancelButton.IsEnabled = true;
        BatchBar.Value = 0;
        CountText.Text = $"0 / {total:N0}";
        ElapsedText.Text = "";
        ErrorText.Visibility = Visibility.Collapsed;
        Visibility = Visibility.Visible;
    }

    public void AddResult(BatchProgressItem item)
    {
        var entry = new LogEntry
        {
            DisplayName = item.DisplayName,
            VoiceType = item.VoiceType,
            Preview = item.ItemStatus switch
            {
                BatchItemStatus.Success => Truncate(item.TranscriptionPreview, 40) ?? "(transcribed)",
                BatchItemStatus.Empty => "(empty)",
                BatchItemStatus.Error => "(error)",
                BatchItemStatus.Skipped => "(skipped)",
                _ => ""
            },
            Tag = StatusBrush(item.ItemStatus)
        };

        _logEntries.Add(entry);

        // Auto-scroll to bottom
        if (_logEntries.Count > 0)
        {
            LogListView.ScrollIntoView(_logEntries[^1]);
        }
    }

    public void UpdateStats(int processed, int total, int errors, TimeSpan elapsed)
    {
        CountText.Text = $"{processed:N0} / {total:N0}";
        BatchBar.Value = total > 0 ? 100.0 * processed / total : 0;

        var elapsedStr = elapsed.TotalHours >= 1
            ? elapsed.ToString(@"h\:mm\:ss")
            : elapsed.ToString(@"m\:ss");

        // ETA
        if (processed > 0 && processed < total)
        {
            var remaining = TimeSpan.FromTicks(elapsed.Ticks * (total - processed) / processed);
            var etaStr = remaining.TotalHours >= 1
                ? remaining.ToString(@"h\:mm\:ss")
                : remaining.ToString(@"m\:ss");
            ElapsedText.Text = $"{elapsedStr}  ~{etaStr} left";
        }
        else
        {
            ElapsedText.Text = elapsedStr;
        }

        if (errors > 0)
        {
            ErrorText.Text = $"{errors} error{(errors == 1 ? "" : "s")}";
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    public void CompleteBatch(string message)
    {
        SpinnerRing.IsActive = false;
        SpinnerRing.Visibility = Visibility.Collapsed;
        CompleteIcon.Visibility = Visibility.Visible;
        CancelButton.IsEnabled = false;
        CountText.Text = message;
        BatchBar.Value = 100;
    }

    public void Hide()
    {
        Visibility = Visibility.Collapsed;
    }

    private void ExpandToggle_Checked(object sender, RoutedEventArgs e)
    {
        LogPanel.Visibility = Visibility.Visible;
        ChevronIcon.Glyph = "\uE70E"; // ChevronUp
    }

    private void ExpandToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        LogPanel.Visibility = Visibility.Collapsed;
        ChevronIcon.Glyph = "\uE70D"; // ChevronDown
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        CancelRequested?.Invoke(this, EventArgs.Empty);
        CancelButton.IsEnabled = false;
    }

    private static SolidColorBrush StatusBrush(BatchItemStatus status) => status switch
    {
        BatchItemStatus.Success => new SolidColorBrush(Colors.LimeGreen),
        BatchItemStatus.Empty => new SolidColorBrush(Colors.Goldenrod),
        BatchItemStatus.Error => new SolidColorBrush(Colors.Tomato),
        BatchItemStatus.Skipped => new SolidColorBrush(Colors.Gray),
        _ => new SolidColorBrush(Colors.Gray)
    };

    private static string? Truncate(string? text, int maxLen)
    {
        if (text == null)
        {
            return null;
        }

        return text.Length <= maxLen ? text : text[..maxLen] + "...";
    }

    private sealed class LogEntry
    {
        public string DisplayName { get; init; } = "";
        public string VoiceType { get; init; } = "";
        public string Preview { get; init; } = "";
        public SolidColorBrush Tag { get; init; } = new(Colors.Gray);
    }
}
