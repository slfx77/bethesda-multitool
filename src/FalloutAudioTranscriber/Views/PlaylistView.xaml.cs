using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Channels;
using Windows.Storage.Pickers;
using FalloutAudioTranscriber.Models;
using FalloutAudioTranscriber.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Whisper.net;
using WinRT.Interop;

namespace FalloutAudioTranscriber.Views;

#pragma warning disable CA1001 // WinUI 3 UserControls don't implement IDisposable
public sealed partial class PlaylistView : UserControl
{
    private readonly DispatcherTimer? _autoSaveTimer;
    private readonly ObservableCollection<VoiceFileEntry> _displayedEntries = [];
    private readonly AudioPlaybackService _playbackService = new();

    // ────────────────────────────────────────────────────
    // Batch transcription (multithreaded pipeline)
    // ────────────────────────────────────────────────────

    private readonly object _projectLock = new();
    private readonly WhisperTranscriptionService _whisperService = new();
    private List<VoiceFileEntry> _allEntries = [];
    private CancellationTokenSource? _batchCts;
    private string? _dataDirectory;
    private bool _filtersInitialized;
    private bool _hasUnsavedChanges;

    // Transcription state
    private TranscriptionProject? _project;

    // Filter state
    private string _searchQuery = "";
    private bool _showEsmSubtitles;
    private bool _sortAscending = true;

    // Sort state
    private string _sortColumn = "Status";
    private bool _transcribeEsmLines;
    private bool _whisperInitialized;

    public PlaylistView()
    {
        InitializeComponent();
        FileListView.ItemsSource = _displayedEntries;
        AudioPlayer.SetPlaybackService(_playbackService);

        _autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _autoSaveTimer.Tick += AutoSaveTimer_Tick;

        DetailPanel.ApproveRequested += DetailPanel_ApproveRequested;
        DetailPanel.TranscribeRequested += DetailPanel_TranscribeRequested;
        DetailPanel.RejectRequested += DetailPanel_RejectRequested;
    }

    /// <summary>
    ///     Set the build result and initialize the transcription workflow.
    /// </summary>
    public async void SetBuildResult(BuildLoadResult result, string? dataDirectory = null)
    {
        _allEntries = result.Entries;
        _dataDirectory = dataDirectory;
        _playbackService.SetFileRecords(result.FileRecords);

        // Load transcription project
        if (dataDirectory != null)
        {
            _project = await TranscriptionFileService.LoadAsync(dataDirectory)
                       ?? new TranscriptionProject
                       {
                           DataDirectory = dataDirectory,
                           CreatedAt = DateTimeOffset.UtcNow
                       };

            TranscriptionFileService.ApplyToEntries(_project, _allEntries);
        }

        // Populate filter dropdowns
        PopulateFilterDropdowns();

        // Apply filters
        _filtersInitialized = true;
        DetailPanel.SetTranscribeEsmMode(_transcribeEsmLines);
        ApplyFilters();

        // Enable buttons
        UpdateBatchButtonState();
        ExportButton.IsEnabled = BatchOperationHelper.ShouldEnableExport(_project, _allEntries);
        ClearWhisperButton.IsEnabled = _project?.Entries.Values.Any(e => e.Source == "whisper") == true;

        // Initialize Whisper in background
        if (PlaylistFilterHelper.HasWorkItems(_allEntries, _transcribeEsmLines))
        {
            _ = InitializeWhisperAsync();
        }
    }

    private void PopulateFilterDropdowns()
    {
        var source = ShowEsmCheck.IsChecked == true
            ? _allEntries
            : _allEntries.Where(e => e.Status != TranscriptionStatus.EsmSubtitle).ToList();

        SpeakerFilter.ItemsSource = PlaylistFilterHelper.BuildSpeakerList(source);
        SpeakerFilter.SelectedIndex = 0;

        QuestFilter.ItemsSource = PlaylistFilterHelper.BuildQuestList(source);
        QuestFilter.SelectedIndex = 0;

        VoiceTypeFilter.ItemsSource = PlaylistFilterHelper.BuildVoiceTypeList(source);
        VoiceTypeFilter.SelectedIndex = 0;
    }

    private async Task InitializeWhisperAsync()
    {
        try
        {
            MainWindow.Instance?.SetStatus("Initializing Whisper model...");
            await _whisperService.InitializeAsync(
                new Progress<(string message, double percent)>(p =>
                    DispatcherQueue.TryEnqueue(() => MainWindow.Instance?.SetStatus(p.message))));
            _whisperInitialized = true;
            DetailPanel.SetWhisperAvailable(true);
            MainWindow.Instance?.SetStatus("Whisper ready");
        }
        catch (Exception ex)
        {
            MainWindow.Instance?.SetStatus($"Whisper init failed: {ex.Message}");
        }
    }

    // ────────────────────────────────────────────────────
    // Filtering
    // ────────────────────────────────────────────────────

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            _searchQuery = sender.Text.Trim();
            ApplyFilters();
        }
    }

    private void ColumnHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string column)
        {
            if (_sortColumn == column)
            {
                _sortAscending = !_sortAscending;
            }
            else
            {
                _sortColumn = column;
                _sortAscending = true;
            }

            ApplyFilters();
        }
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (!_filtersInitialized)
        {
            return;
        }

        // Prevent hiding ESM entries while transcribe-ESM mode is active
        if (ReferenceEquals(sender, ShowEsmCheck) && ShowEsmCheck.IsChecked != true && _transcribeEsmLines)
        {
            ShowEsmCheck.IsChecked = true;
            return;
        }

        // Repopulate filter dropdowns when ESM checkbox changes
        if (ReferenceEquals(sender, ShowEsmCheck))
        {
            _filtersInitialized = false;
            PopulateFilterDropdowns();
            _filtersInitialized = true;
        }

        ApplyFilters();
    }

    private void TranscribeEsmCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_filtersInitialized)
        {
            return;
        }

        _transcribeEsmLines = TranscribeEsmCheck.IsChecked == true;
        DetailPanel.SetTranscribeEsmMode(_transcribeEsmLines);

        // When enabling transcribe-ESM mode, auto-enable Show ESM subtitles
        if (_transcribeEsmLines && ShowEsmCheck.IsChecked != true)
        {
            ShowEsmCheck.IsChecked = true; // Triggers Filter_Changed -> ApplyFilters
        }

        UpdateBatchButtonState();

        // Re-show current entry with updated mode
        if (FileListView.SelectedItem is VoiceFileEntry selected)
        {
            DetailPanel.ShowEntry(selected);
        }
    }

    private void ApplyFilters()
    {
        if (!_filtersInitialized)
        {
            return;
        }

        _showEsmSubtitles = ShowEsmCheck.IsChecked == true;

        var results = PlaylistFilterHelper.ApplyFiltersAndSort(
            _allEntries,
            _showEsmSubtitles,
            SpeakerFilter.SelectedItem as string,
            QuestFilter.SelectedItem as string,
            VoiceTypeFilter.SelectedItem as string,
            _searchQuery,
            _sortColumn,
            _sortAscending);

        _displayedEntries.Clear();
        foreach (var entry in results)
        {
            _displayedEntries.Add(entry);
        }

        CountText.Text = $"{_displayedEntries.Count:N0} of {_allEntries.Count:N0} files";
    }

    // ────────────────────────────────────────────────────
    // Selection and playback
    // ────────────────────────────────────────────────────

    private void FileListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = FileListView.SelectedItem as VoiceFileEntry;
        DetailPanel.ShowEntry(selected);

        if (selected != null)
        {
            AudioPlayer.LoadEntry(selected);
        }
    }

    private void ItemPlay_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: VoiceFileEntry entry })
        {
            FileListView.SelectedItem = entry;
            _ = AudioPlayer.PlayFileAsync(entry);
        }
    }

    // ────────────────────────────────────────────────────
    // Transcription workflow
    // ────────────────────────────────────────────────────

    private async void DetailPanel_TranscribeRequested(object? sender, EventArgs e)
    {
        var selected = FileListView.SelectedItem as VoiceFileEntry;
        if (selected == null || !_whisperInitialized)
        {
            return;
        }

        DetailPanel.ShowWhisperProgress("Transcribing...");

        try
        {
            var wavData = await _playbackService.ExtractWavAsync(selected);
            if (wavData != null)
            {
                var text = await Task.Run(() => _whisperService.TranscribeAsync(wavData));

                // Only update if same entry is still selected
                if (FileListView.SelectedItem == selected)
                {
                    DetailPanel.TranscriptionText = text;
                }

                // Save as automatic (pending review)
                if (_project != null)
                {
                    BatchOperationHelper.ApplyTranscription(selected, text, "whisper", _project);
                    _hasUnsavedChanges = true;
                    _autoSaveTimer?.Start();
                }
                else
                {
                    selected.SubtitleText = text;
                    selected.TranscriptionSource = "whisper";
                }
            }
        }
        catch (Exception ex)
        {
            DetailPanel.ShowWhisperProgress($"Error: {ex.Message}");
            return;
        }

        DetailPanel.HideWhisperProgress();
    }

    private void DetailPanel_ApproveRequested(object? sender, EventArgs e)
    {
        _ = ApproveCurrentAsync();
    }

    private async Task ApproveCurrentAsync()
    {
        var selected = FileListView.SelectedItem as VoiceFileEntry;
        if (selected == null || _project == null)
        {
            return;
        }

        var text = DetailPanel.TranscriptionText.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        BatchOperationHelper.ApplyTranscription(selected, text, "accepted", _project);

        _hasUnsavedChanges = true;
        _autoSaveTimer?.Start();

        ExportButton.IsEnabled = true;

        // Refresh list and advance to next untranscribed entry
        ApplyFilters();
        SelectNextUntranscribed();
    }

    private void SelectNextUntranscribed()
    {
        var currentIndex = FileListView.SelectedIndex;

        // Search forward from current position
        for (var i = currentIndex + 1; i < _displayedEntries.Count; i++)
        {
            if (PlaylistFilterHelper.IsWorkItem(_displayedEntries[i], _transcribeEsmLines))
            {
                FileListView.SelectedIndex = i;
                FileListView.ScrollIntoView(_displayedEntries[i]);
                return;
            }
        }

        // Wrap around
        for (var i = 0; i < currentIndex; i++)
        {
            if (PlaylistFilterHelper.IsWorkItem(_displayedEntries[i], _transcribeEsmLines))
            {
                FileListView.SelectedIndex = i;
                FileListView.ScrollIntoView(_displayedEntries[i]);
                return;
            }
        }
    }

    private void DetailPanel_RejectRequested(object? sender, EventArgs e)
    {
        var selected = FileListView.SelectedItem as VoiceFileEntry;
        if (selected == null || _project == null)
        {
            return;
        }

        if (!BatchOperationHelper.RevertToEsm(selected, _project))
        {
            return;
        }

        _hasUnsavedChanges = true;
        _autoSaveTimer?.Start();

        // Refresh detail panel and list
        DetailPanel.ShowEntry(selected);
        ApplyFilters();
    }

    private void UpdateBatchButtonState()
    {
        BatchButton.IsEnabled = PlaylistFilterHelper.HasWorkItems(_allEntries, _transcribeEsmLines);
    }

    private void Approve_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _ = ApproveCurrentAsync();
    }

    // ────────────────────────────────────────────────────
    // Clear Whisper transcriptions
    // ────────────────────────────────────────────────────

    private async void ClearWhisper_Click(object sender, RoutedEventArgs e)
    {
        if (_project == null || _dataDirectory == null)
        {
            return;
        }

        ClearWhisperFlyout.Hide();

        var cleared = TranscriptionFileService.ClearBySource(_project, _allEntries, "whisper");

        await TranscriptionFileService.SaveAsync(_dataDirectory, _project);

        ClearWhisperButton.IsEnabled = false;
        UpdateBatchButtonState();
        ExportButton.IsEnabled = BatchOperationHelper.ShouldEnableExport(_project, _allEntries);

        ApplyFilters();
        MainWindow.Instance?.SetStatus($"Cleared {cleared} Whisper transcriptions");
    }

    // ────────────────────────────────────────────────────
    // Auto-save
    // ────────────────────────────────────────────────────

    private async void AutoSaveTimer_Tick(object? sender, object e)
    {
        _autoSaveTimer?.Stop();

        if (!_hasUnsavedChanges || _project == null || _dataDirectory == null)
        {
            return;
        }

        _hasUnsavedChanges = false;

        try
        {
            await TranscriptionFileService.SaveAsync(_dataDirectory, _project);
            MainWindow.Instance?.SetStatus($"Saved {_project.Entries.Count} transcriptions");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Playlist] Auto-save error: {ex.Message}");
        }
    }

    private async void Batch_Click(object sender, RoutedEventArgs e)
    {
        if (_project == null || _dataDirectory == null || !_whisperInitialized)
        {
            return;
        }

        _batchCts = new CancellationTokenSource();
        var ct = _batchCts.Token;

        // Wire drawer cancel
        BatchDrawer.CancelRequested += (_, _) => _batchCts?.Cancel();

        var untranscribed = BatchOperationHelper.GetBatchWorkItems(_allEntries, _transcribeEsmLines);
        var processed = 0;
        var errors = 0;
        var total = untranscribed.Count;
        var workerCount = BatchOperationHelper.GetWorkerCount();
        var stopwatch = Stopwatch.StartNew();

        // Show drawer
        BatchButton.IsEnabled = false;
        BatchDrawer.StartBatch(total, workerCount);

        // Bounded channel: producer fills ahead, consumers drain
        var channel = Channel.CreateBounded<(VoiceFileEntry entry, byte[] wavData)>(workerCount * 2);
        var processors = new List<WhisperProcessor>();
        Task? producerTask = null;
        Task[]? consumerTasks = null;

        try
        {
            // Create worker processors from shared factory
            for (var i = 0; i < workerCount; i++)
            {
                processors.Add(_whisperService.CreateProcessor());
            }

            // Producer: extract WAV data from BSA (I/O-bound)
            producerTask = Task.Run(async () =>
            {
                try
                {
                    foreach (var entry in untranscribed)
                    {
                        ct.ThrowIfCancellationRequested();

                        try
                        {
                            var wavData = await _playbackService.ExtractWavNoCacheAsync(entry, ct);
                            if (wavData != null)
                            {
                                await channel.Writer.WriteAsync((entry, wavData), ct);
                            }
                            else
                            {
                                var count = Interlocked.Increment(ref processed);
                                Interlocked.Increment(ref errors);
                                DispatcherQueue.TryEnqueue(() =>
                                {
                                    BatchDrawer.AddResult(
                                        BatchOperationHelper.CreateProgressItem(
                                            entry, BatchItemStatus.Error, "extraction returned null"));
                                    BatchDrawer.UpdateStats(count, total, errors, stopwatch.Elapsed);
                                });
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            var count = Interlocked.Increment(ref processed);
                            Interlocked.Increment(ref errors);
                            DispatcherQueue.TryEnqueue(() =>
                            {
                                BatchDrawer.AddResult(
                                    BatchOperationHelper.CreateProgressItem(
                                        entry, BatchItemStatus.Error, ex.Message));
                                BatchDrawer.UpdateStats(count, total, errors, stopwatch.Elapsed);
                            });
                        }
                    }
                }
                finally
                {
                    channel.Writer.Complete();
                }
            }, ct);

            // Consumers: run Whisper transcription (CPU-bound, one processor per worker)
            consumerTasks = processors.Select(processor => Task.Run(async () =>
            {
                try
                {
                    await foreach (var (entry, wavData) in channel.Reader.ReadAllAsync(ct))
                    {
                        string? transcribedText = null;
                        var itemStatus = BatchItemStatus.Empty;

                        try
                        {
                            var text = await WhisperTranscriptionService.TranscribeWithProcessorAsync(
                                processor, wavData, ct);

                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                transcribedText = text.Trim();
                                entry.SubtitleText = transcribedText;
                                entry.TranscriptionSource = "whisper";
                                itemStatus = BatchItemStatus.Success;

                                lock (_projectLock)
                                {
                                    var key = BatchOperationHelper.BuildProjectKey(entry);
                                    _project!.Entries[key] =
                                        BatchOperationHelper.CreateTranscriptionEntry(
                                            transcribedText, "whisper", entry);
                                }
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            itemStatus = BatchItemStatus.Error;
                            transcribedText = ex.Message;
                            Interlocked.Increment(ref errors);
                        }

                        var count = Interlocked.Increment(ref processed);
                        var capturedStatus = itemStatus;
                        var capturedText = transcribedText;
                        var capturedErrors = errors;

                        DispatcherQueue.TryEnqueue(() =>
                        {
                            BatchDrawer.AddResult(
                                BatchOperationHelper.CreateProgressItem(
                                    entry, capturedStatus, capturedText));
                            BatchDrawer.UpdateStats(count, total, capturedErrors, stopwatch.Elapsed);
                        });

                        // Auto-save every 10 entries
                        if (count % 10 == 0)
                        {
                            Dictionary<string, TranscriptionEntry> snapshotEntries;
                            lock (_projectLock)
                            {
                                snapshotEntries = new Dictionary<string, TranscriptionEntry>(_project!.Entries);
                            }

                            var projectSnapshot =
                                BatchOperationHelper.CreateProjectSnapshot(_project!, snapshotEntries);

                            try
                            {
                                await TranscriptionFileService.SaveAsync(_dataDirectory!, projectSnapshot, ct);
                            }
                            catch
                            {
                                // Save errors during batch are non-fatal
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected on cancellation -- exit gracefully so the processor can be disposed
                }
            }, CancellationToken.None)).ToArray(); // CancellationToken.None: let consumers drain gracefully

            // Wait for all work to complete
            await producerTask;
            await Task.WhenAll(consumerTasks);

            // Final save
            stopwatch.Stop();
            await TranscriptionFileService.SaveAsync(_dataDirectory, _project, ct);
            BatchDrawer.CompleteBatch(
                BatchOperationHelper.FormatCompletionMessage(processed, errors, stopwatch.Elapsed));
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            await BatchOperationHelper.DrainConsumersAsync(consumerTasks);
            await TranscriptionFileService.SaveAsync(_dataDirectory, _project);
            BatchDrawer.CompleteBatch(BatchOperationHelper.FormatCancellationMessage(processed));
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            await BatchOperationHelper.DrainConsumersAsync(consumerTasks);
            await TranscriptionFileService.SaveAsync(_dataDirectory, _project);
            BatchDrawer.CompleteBatch($"Error: {ex.Message}");
        }
        finally
        {
            // All consumers are done -- safe to dispose processors
            foreach (var p in processors)
            {
                await p.DisposeAsync();
            }

            UpdateBatchButtonState();
            ExportButton.IsEnabled = BatchOperationHelper.ShouldEnableExport(_project, _allEntries);
            _batchCts?.Dispose();
            _batchCts = null;

            ApplyFilters();
        }
    }

    // ────────────────────────────────────────────────────
    // Export
    // ────────────────────────────────────────────────────

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!BatchOperationHelper.HasExportableContent(_project, _showEsmSubtitles, _allEntries))
        {
            return;
        }

        var picker = new FileSavePicker();
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeChoices.Add("CSV", [".csv"]);
        picker.FileTypeChoices.Add("Plain Text", [".txt"]);
        picker.SuggestedFileName = "transcriptions";

        var hwnd = WindowNative.GetWindowHandle(MainWindow.Instance!);
        InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSaveFileAsync();
        if (file != null)
        {
            try
            {
                _project ??= new TranscriptionProject();

                if (file.FileType == ".csv")
                {
                    await TranscriptionFileService.ExportCsvAsync(file.Path, _project, _allEntries, _showEsmSubtitles);
                }
                else
                {
                    await TranscriptionFileService.ExportTextAsync(file.Path, _project, _allEntries, _showEsmSubtitles);
                }

                var esmNote = _showEsmSubtitles ? " (including ESM subtitles)" : "";
                MainWindow.Instance?.SetStatus($"Exported transcriptions to {file.Name}{esmNote}");
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.SetStatus($"Export error: {ex.Message}");
            }
        }
    }
}
