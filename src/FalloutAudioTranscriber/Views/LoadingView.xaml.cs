using FalloutAudioTranscriber.Models;
using FalloutAudioTranscriber.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using WinRT.Interop;

namespace FalloutAudioTranscriber.Views;

#pragma warning disable CA1001 // WinUI 3 UserControls don't implement IDisposable
public sealed partial class LoadingView : UserControl
{
    private CancellationTokenSource? _cts;

    public LoadingView()
    {
        InitializeComponent();
    }

    /// <summary>The loaded build result (available after BuildLoaded fires).</summary>
    public BuildLoadResult? LoadResult { get; private set; }

    /// <summary>The selected data directory path.</summary>
    public string? DataDirectory { get; private set; }

    /// <summary>User-selected ESM override path (null = auto-detect).</summary>
    public string? EsmOverridePath { get; private set; }

    /// <summary>Fires when a build is successfully loaded.</summary>
    public event EventHandler? BuildLoaded;

    // Pickers use the WindowsAppSDK Microsoft.Windows.Storage.Pickers namespace rather
    // than the legacy Windows.Storage.Pickers WinRT API. The legacy pickers rely on
    // InitializeWithWindow + a COM activation context that does not work reliably in
    // unpackaged WinUI 3 apps (1.7+), so the click would fire but the dialog never
    // appeared. The new pickers take the window handle directly in the constructor.
    private async void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker(GetWindowId())
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder
        };

        var result = await picker.PickSingleFolderAsync();
        if (result != null)
        {
            DataDirectory = result.Path;
            FolderPathBox.Text = result.Path;
            LoadButton.IsEnabled = true;
            ResultsPanel.Visibility = Visibility.Collapsed;
        }
    }

    private async void EsmBrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker(GetWindowId())
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder
        };
        picker.FileTypeFilter.Add(".esm");

        var result = await picker.PickSingleFileAsync();
        if (result != null)
        {
            EsmOverridePath = result.Path;
            EsmPathBox.Text = result.Path;
            EsmClearButton.Visibility = Visibility.Visible;
        }
    }

    private static WindowId GetWindowId()
    {
        var hwnd = WindowNative.GetWindowHandle(MainWindow.Instance!);
        return Win32Interop.GetWindowIdFromWindow(hwnd);
    }

    private void EsmClearButton_Click(object sender, RoutedEventArgs e)
    {
        EsmOverridePath = null;
        EsmPathBox.Text = "";
        EsmClearButton.Visibility = Visibility.Collapsed;
    }

    private async void LoadButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataDirectory == null)
        {
            return;
        }

        // Disable controls during loading
        BrowseButton.IsEnabled = false;
        LoadButton.IsEnabled = false;
        LoadProgressBar.Opacity = 1;
        LoadProgressBar.Value = 0;
        ResultsPanel.Visibility = Visibility.Collapsed;

        _cts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<(string message, double percent)>(p =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    MainWindow.Instance?.SetStatus(p.message);
                    LoadProgressBar.Value = p.percent;
                });
            });

            var result = await BuildDirectoryLoader.LoadAsync(
                DataDirectory, progress, EsmOverridePath, _cts.Token);

            LoadResult = result;
            var entries = result.Entries;

            // Show results
            var transcribed = entries.Count(e => e.HasSubtitle);
            var untranscribed = entries.Count - transcribed;
            var voiceTypes = entries.Select(e => e.VoiceType).Distinct().Count();

            ResultsHeader.Text = $"Loaded {entries.Count:N0} voice files";

            var detailLines = $"{transcribed:N0} with subtitles, {untranscribed:N0} untranscribed\n"
                              + $"{voiceTypes} voice types across {entries.Select(e => e.BsaFilePath).Distinct().Count()} BSAs";

            // ESM enrichment heuristics
            if (result.EsmInfoCount > 0)
            {
                static string Pct(int n, int total)
                {
                    return total > 0 ? $"{100.0 * n / total:F0}%" : "N/A";
                }

                var sourceTag = result.EsmSourceDescription ?? "unknown";
                detailLines += $"\nESM ({sourceTag}): {result.EsmInfoCount:N0} INFOs, {result.EsmNpcCount:N0} NPCs, "
                               + $"{result.EsmQuestCount:N0} quests, {result.EsmTopicCount:N0} topics";
                detailLines +=
                    $"\nMatch: {result.EnrichedSubtitleCount:N0} subtitles ({Pct(result.EnrichedSubtitleCount, entries.Count)}), "
                    + $"{result.EnrichedSpeakerCount:N0} speakers ({Pct(result.EnrichedSpeakerCount, entries.Count)}), "
                    + $"{result.EnrichedQuestCount:N0} quests ({Pct(result.EnrichedQuestCount, entries.Count)})";
            }
            else
            {
                detailLines += "\nNo ESM file found — speaker/quest data unavailable";
            }

            ResultsDetails.Text = detailLines;
            ResultsPanel.Visibility = Visibility.Visible;

            MainWindow.Instance?.SetStatus($"Loaded {entries.Count:N0} voice files from {DataDirectory}");

            BuildLoaded?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            MainWindow.Instance?.SetStatus("Cancelled.");
        }
        catch (Exception ex)
        {
            ResultsHeader.Text = "Error loading build";
            ResultsDetails.Text = ex.Message;
            ResultsPanel.Visibility = Visibility.Visible;
        }
        finally
        {
            BrowseButton.IsEnabled = true;
            LoadButton.IsEnabled = true;
            LoadProgressBar.Opacity = 0;
            _cts?.Dispose();
            _cts = null;
        }
    }
}
