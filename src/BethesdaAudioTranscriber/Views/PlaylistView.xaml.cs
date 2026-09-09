using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Channels;
using BethesdaAudioTranscriber.Models;
using BethesdaAudioTranscriber.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.Storage.Pickers;
using Whisper.net;
using WinRT.Interop;

namespace BethesdaAudioTranscriber.Views;

#pragma warning disable CA1001 // PrepareForCloseAsync releases owned resources after pending work completes.
public sealed partial class PlaylistView : UserControl
{
    private readonly DispatcherTimer? _autoSaveTimer;
    private readonly ObservableCollection<VoiceFileEntry> _displayedEntries = [];
    private readonly AudioPlaybackService _playbackService = new();

    // ────────────────────────────────────────────────────
    // Batch transcription (multithreaded pipeline)
    // ────────────────────────────────────────────────────

    private readonly object _projectLock = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly WhisperTranscriptionService _whisperService = new();
    private List<VoiceFileEntry> _allEntries = [];
    private bool _autoSaveInProgress;
    private bool _batchInProgress;
    private CancellationTokenSource? _batchCts;
    private bool _clearInProgress;
    private string? _dataDirectory;
    private bool _disposed;
    private bool _exportInProgress;
    private bool _filtersInitialized;
    private bool _hasUnsavedChanges;
    private int _projectGeneration;
    private bool _singleTranscriptionInProgress;
    private bool _switchingProject;

    // Transcription state
    private TranscriptionProject? _project;
    private bool _reviewDirty;

    // Suspected-typo review sidecar (.fnvreview.json)
    private ReviewFile? _reviewFile;

    // Filter state
    private string _searchQuery = "";
    private bool _showEsmSubtitles;
    private bool _sortAscending = true;

    // Sort state
    private string _sortColumn = "Status";
    private bool _transcribeEsmLines;
    private bool _whisperInitialized;
    private Task? _whisperInitializationTask;

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
        DetailPanel.DismissReviewRequested += DetailPanel_DismissReviewRequested;
        BatchDrawer.CancelRequested += BatchDrawer_CancelRequested;
    }

    /// <summary>
    ///     Set the build result and initialize the transcription workflow.
    /// </summary>
    public async Task SetBuildResultAsync(BuildLoadResult result, string? dataDirectory = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_switchingProject || _batchInProgress || _singleTranscriptionInProgress ||
            _clearInProgress || _autoSaveInProgress || _exportInProgress)
        {
            throw new InvalidOperationException(
                "Finish or cancel the current transcription/save operation before opening another project.");
        }

        _switchingProject = true;
        IsEnabled = false;
        _autoSaveTimer?.Stop();
        try
        {
            // Flush to the old project's directory before reading another project, including a
            // reload of the same path. A failed save/load leaves the existing session available.
            await SavePendingChangesAsync();
            TranscriptionProject? project = null;
            ReviewFile? review = null;
            var pendingReviews = 0;
            if (dataDirectory != null)
            {
                project = await TranscriptionFileService.LoadAsync(dataDirectory)
                          ?? new TranscriptionProject
                          {
                              DataDirectory = dataDirectory,
                              CreatedAt = DateTimeOffset.UtcNow
                          };
                review = await ReviewFileService.LoadAsync(dataDirectory);
                TranscriptionFileService.ApplyToEntries(project, result.Entries);
                if (review != null)
                {
                    pendingReviews = ReviewFileService.ApplyToEntries(review, result.Entries);
                }
            }

            _projectGeneration++;
            _project = project;
            _reviewFile = review;
            _allEntries = result.Entries;
            _dataDirectory = dataDirectory;
            _hasUnsavedChanges = false;
            _reviewDirty = false;
            _playbackService.Stop();
            _playbackService.SetFileRecords(result.FileRecords);
            AudioPlayer.SetPlaybackService(_playbackService);
            _filtersInitialized = false;
            FlaggedOnlyCheck.IsChecked = false;
            FlaggedOnlyCheck.Visibility = review != null ? Visibility.Visible : Visibility.Collapsed;
            if (review != null)
            {
                MainWindow.Instance?.SetStatus($"{pendingReviews:N0} suspected typos flagged for review");
            }

            PopulateFilterDropdowns();

            _filtersInitialized = true;
            DetailPanel.SetTranscribeEsmMode(_transcribeEsmLines);
            ApplyFilters();

            UpdateBatchButtonState();
            ExportButton.IsEnabled = BatchOperationHelper.ShouldEnableExport(_project, _allEntries);
            ClearWhisperButton.IsEnabled = _project?.Entries.Values.Any(e => e.Source == "whisper") == true;

            if (!_whisperInitialized && _whisperInitializationTask?.IsCompleted != false &&
                PlaylistFilterHelper.HasWorkItems(_allEntries, _transcribeEsmLines))
            {
                _whisperInitializationTask = InitializeWhisperAsync();
            }
        }
        finally
        {
            _switchingProject = false;
            IsEnabled = true;
            DetailPanel.SetWhisperAvailable(_whisperInitialized);
        }
    }

    public async Task PrepareForCloseAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (_switchingProject || _batchInProgress || _singleTranscriptionInProgress ||
            _clearInProgress || _autoSaveInProgress || _exportInProgress)
        {
            throw new InvalidOperationException(
                "Finish or cancel the current transcription/save operation before closing.");
        }

        _switchingProject = true;
        IsEnabled = false;
        _autoSaveTimer?.Stop();
        try
        {
            await SavePendingChangesAsync();
            if (_whisperInitializationTask != null)
            {
                await _whisperInitializationTask;
            }

            _disposed = true;
            _projectGeneration++;
            if (_autoSaveTimer != null)
            {
                _autoSaveTimer.Tick -= AutoSaveTimer_Tick;
            }
            DetailPanel.ApproveRequested -= DetailPanel_ApproveRequested;
            DetailPanel.TranscribeRequested -= DetailPanel_TranscribeRequested;
            DetailPanel.RejectRequested -= DetailPanel_RejectRequested;
            DetailPanel.DismissReviewRequested -= DetailPanel_DismissReviewRequested;
            BatchDrawer.CancelRequested -= BatchDrawer_CancelRequested;
            AudioPlayer.ClearPlaybackService();
            try
            {
                _playbackService.Dispose();
            }
            finally
            {
                try
                {
                    _whisperService.Dispose();
                }
                finally
                {
                    _saveGate.Dispose();
                }
            }
        }
        finally
        {
            if (!_disposed)
            {
                _switchingProject = false;
                IsEnabled = true;
                DetailPanel.SetWhisperAvailable(_whisperInitialized);
            }
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
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (!_switchingProject && !_disposed)
                        {
                            MainWindow.Instance?.SetStatus(p.message);
                        }
                    })));
            _whisperInitialized = true;
            if (!_switchingProject && !_disposed)
            {
                DetailPanel.SetWhisperAvailable(true);
                MainWindow.Instance?.SetStatus("Whisper ready");
            }
        }
        catch (Exception ex)
        {
            if (!_switchingProject && !_disposed)
            {
                MainWindow.Instance?.SetStatus($"Whisper init failed: {ex.Message}");
            }
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
            _sortAscending,
            FlaggedOnlyCheck.IsChecked == true);

        _displayedEntries.Clear();
        foreach (var entry in results)
        {
            _displayedEntries.Add(entry);
        }

        var flaggedNote = _reviewFile != null
            ? $" • {ReviewFileService.CountPending(_allEntries):N0} flagged"
            : "";
        CountText.Text = $"{_displayedEntries.Count:N0} of {_allEntries.Count:N0} files{flaggedNote}";
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
        if (selected == null || !_whisperInitialized || _switchingProject || _singleTranscriptionInProgress ||
            _batchInProgress || _clearInProgress)
        {
            return;
        }

        _singleTranscriptionInProgress = true;
        try
        {
            DetailPanel.ShowWhisperProgress("Transcribing...");
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
                    lock (_projectLock)
                    {
                        BatchOperationHelper.ApplyTranscription(selected, text, "whisper", _project);
                    }
                    _hasUnsavedChanges = true;
                    _autoSaveTimer?.Start();
                }
                else
                {
                    selected.SubtitleText = text;
                    selected.TranscriptionSource = "whisper";
                }
            }

            DetailPanel.HideWhisperProgress();
        }
        catch (Exception ex)
        {
            DetailPanel.ShowWhisperProgress($"Error: {ex.Message}");
            return;
        }
        finally
        {
            _singleTranscriptionInProgress = false;
        }
    }

    private void DetailPanel_ApproveRequested(object? sender, EventArgs e)
    {
        ApproveCurrent();
    }

    private void ApproveCurrent()
    {
        var selected = FileListView.SelectedItem as VoiceFileEntry;
        if (selected == null || _project == null || _switchingProject)
        {
            return;
        }

        var text = DetailPanel.TranscriptionText.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        lock (_projectLock)
        {
            BatchOperationHelper.ApplyTranscription(selected, text, "accepted", _project);
        }
        ResolveReviewFlag(selected);

        _hasUnsavedChanges = true;
        _autoSaveTimer?.Start();

        ExportButton.IsEnabled = true;

        RefreshListAndSelect(selected, true);
    }

    /// <summary>
    ///     Re-apply filters, then restore selection relative to the given entry.
    ///     With <paramref name="advance" />, select the next pending entry after the
    ///     entry's old position (wrapping); otherwise stay on the entry itself.
    ///     Rebuilding the list resets ListView selection and scroll, so without this
    ///     the view jumps to the top after every Approve.
    /// </summary>
    private void RefreshListAndSelect(VoiceFileEntry current, bool advance)
    {
        // Candidate order comes from the display order before the refresh:
        // everything after the current position first, then wrap around.
        var currentIndex = _displayedEntries.IndexOf(current);
        var candidates = new List<VoiceFileEntry>();
        for (var i = currentIndex + 1; i < _displayedEntries.Count; i++)
        {
            candidates.Add(_displayedEntries[i]);
        }

        for (var i = 0; i < currentIndex; i++)
        {
            candidates.Add(_displayedEntries[i]);
        }

        ApplyFilters();

        var displayed = new HashSet<VoiceFileEntry>(_displayedEntries);
        VoiceFileEntry? next = null;
        if (advance)
        {
            next = candidates.FirstOrDefault(e =>
                displayed.Contains(e) && PlaylistFilterHelper.IsWorkItem(e, _transcribeEsmLines));
        }

        // Fall back to the current entry (if still displayed), then to the nearest
        // former neighbor — e.g. when the last flagged entry was just resolved
        // under the "Flagged only" filter.
        next ??= displayed.Contains(current) ? current : candidates.FirstOrDefault(displayed.Contains);

        if (next != null)
        {
            FileListView.SelectedItem = next;
            FileListView.ScrollIntoView(next);
        }
    }

    /// <summary>
    ///     Mark the entry's suspected-typo flag as resolved (if any) and queue a sidecar save.
    /// </summary>
    private void ResolveReviewFlag(VoiceFileEntry entry)
    {
        if (entry.Review is not { Resolved: false })
        {
            return;
        }

        entry.Review.Resolved = true;
        _reviewDirty = true;
        _autoSaveTimer?.Start();
    }

    private void DetailPanel_DismissReviewRequested(object? sender, EventArgs e)
    {
        var selected = FileListView.SelectedItem as VoiceFileEntry;
        if (selected == null || _switchingProject)
        {
            return;
        }

        ResolveReviewFlag(selected);
        RefreshListAndSelect(selected, true);
    }

    private void DetailPanel_RejectRequested(object? sender, EventArgs e)
    {
        var selected = FileListView.SelectedItem as VoiceFileEntry;
        if (selected == null || _project == null || _switchingProject)
        {
            return;
        }

        lock (_projectLock)
        {
            if (!BatchOperationHelper.RevertToEsm(selected, _project))
            {
                return;
            }
        }

        ResolveReviewFlag(selected);
        _hasUnsavedChanges = true;
        _autoSaveTimer?.Start();

        // Re-selecting fires SelectionChanged, which refreshes the detail panel
        RefreshListAndSelect(selected, false);
    }

    private void UpdateBatchButtonState()
    {
        BatchButton.IsEnabled = PlaylistFilterHelper.HasWorkItems(_allEntries, _transcribeEsmLines);
    }

    private void Approve_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ApproveCurrent();
    }

    // ────────────────────────────────────────────────────
    // Clear Whisper transcriptions
    // ────────────────────────────────────────────────────

    private async void ClearWhisper_Click(object sender, RoutedEventArgs e)
    {
        if (_project == null || _dataDirectory == null || _switchingProject || _clearInProgress ||
            _batchInProgress || _singleTranscriptionInProgress)
        {
            return;
        }

        _clearInProgress = true;
        try
        {
            ClearWhisperFlyout.Hide();

            int cleared;
            lock (_projectLock)
            {
                cleared = TranscriptionFileService.ClearBySource(_project, _allEntries, "whisper");
            }
            _hasUnsavedChanges = true;

            ClearWhisperButton.IsEnabled = false;
            UpdateBatchButtonState();
            ExportButton.IsEnabled = BatchOperationHelper.ShouldEnableExport(_project, _allEntries);
            ApplyFilters();

            await SavePendingChangesAsync();
            MainWindow.Instance?.SetStatus($"Cleared {cleared} Whisper transcriptions");
        }
        catch (Exception ex)
        {
            _hasUnsavedChanges = true;
            _autoSaveTimer?.Start();
            MainWindow.Instance?.SetStatus($"Could not save cleared transcriptions: {ex.Message}");
        }
        finally
        {
            _clearInProgress = false;
            if (!_switchingProject && (_hasUnsavedChanges || _reviewDirty))
            {
                _autoSaveTimer?.Start();
            }
        }
    }

    // ────────────────────────────────────────────────────
    // Auto-save
    // ────────────────────────────────────────────────────

    private async void AutoSaveTimer_Tick(object? sender, object e)
    {
        _autoSaveTimer?.Stop();

        if (_dataDirectory == null || _autoSaveInProgress || _switchingProject || _clearInProgress)
        {
            return;
        }

        _autoSaveInProgress = true;
        var saveSucceeded = false;
        try
        {
            await SavePendingChangesAsync();
            saveSucceeded = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Playlist] Auto-save error: {ex.Message}");
            MainWindow.Instance?.SetStatus($"Auto-save failed: {ex.Message}");
        }
        finally
        {
            _autoSaveInProgress = false;
            if (saveSucceeded && !_switchingProject &&
                ((_hasUnsavedChanges && _project != null) || (_reviewDirty && _reviewFile != null)))
            {
                _autoSaveTimer?.Start();
            }
        }
    }

    private async Task SavePendingChangesAsync()
    {
        var directory = _dataDirectory;
        var project = _project;
        var review = _reviewFile;
        if (directory == null)
        {
            return;
        }

        var transcriptionWritePending = false;
        var reviewWritePending = false;
        try
        {
            if (_hasUnsavedChanges && project != null)
            {
                transcriptionWritePending = true;
                _hasUnsavedChanges = false;
                await SaveProjectAsync(directory, project);
                transcriptionWritePending = false;
                MainWindow.Instance?.SetStatus($"Saved {project.Entries.Count} transcriptions");
            }

            if (_reviewDirty && review != null)
            {
                reviewWritePending = true;
                _reviewDirty = false;
                await _saveGate.WaitAsync();
                try
                {
                    await ReviewFileService.SaveAsync(directory, review);
                }
                finally
                {
                    _saveGate.Release();
                }
                reviewWritePending = false;
            }
        }
        catch
        {
            // Preserve edits that arrived during a write and restore only failed write attempts.
            _hasUnsavedChanges |= transcriptionWritePending;
            _reviewDirty |= reviewWritePending;
            throw;
        }
    }

    private async Task SaveProjectAsync(
        string directory, TranscriptionProject project, CancellationToken ct = default)
    {
        await _saveGate.WaitAsync(ct);
        try
        {
            TranscriptionProject snapshot;
            lock (_projectLock)
            {
                // Snapshot after acquiring the write gate: a queued older checkpoint must not
                // overwrite a later save with state captured before it started waiting.
                snapshot = BatchOperationHelper.CreateProjectSnapshot(
                    project, new Dictionary<string, TranscriptionEntry>(project.Entries));
            }
            await TranscriptionFileService.SaveAsync(directory, snapshot, ct);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private void BatchDrawer_CancelRequested(object? sender, EventArgs e)
    {
        _batchCts?.Cancel();
    }

    private async void Batch_Click(object sender, RoutedEventArgs e)
    {
        if (_batchInProgress || _switchingProject)
        {
            return;
        }

        _batchInProgress = true;
        try
        {
            await RunBatchAsync();
        }
        catch (Exception ex)
        {
            if (_project != null && _dataDirectory != null)
            {
                _hasUnsavedChanges = true;
                _autoSaveTimer?.Start();
            }

            MainWindow.Instance?.SetStatus($"Batch failed: {ex.Message}");
            BatchDrawer.CompleteBatch($"Error: {ex.Message}");
        }
        finally
        {
            // Cover setup failures that occur before RunBatchAsync enters its pipeline try/finally.
            _batchCts?.Dispose();
            _batchCts = null;
            _batchInProgress = false;
        }
    }

    private async Task RunBatchAsync()
    {
        if (_project == null || _dataDirectory == null || !_whisperInitialized || _batchCts != null ||
            _switchingProject || _singleTranscriptionInProgress || _clearInProgress)
        {
            return;
        }

        _batchCts = new CancellationTokenSource();
        var ct = _batchCts.Token;
        var project = _project;
        var directory = _dataDirectory;
        var generation = _projectGeneration;
        _hasUnsavedChanges = true;

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
        Task[]? consumerTasks = null;

        try
        {
            // Create worker processors from shared factory
            for (var i = 0; i < workerCount; i++)
            {
                processors.Add(_whisperService.CreateProcessor());
            }

            // Producer: extract WAV data from BSA (I/O-bound)
            var producerTask = Task.Run(async () =>
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
                                var errorCount = Interlocked.Increment(ref errors);
                                DispatcherQueue.TryEnqueue(() =>
                                {
                                    if (generation != _projectGeneration || _switchingProject)
                                    {
                                        return;
                                    }

                                    BatchDrawer.AddResult(
                                        BatchOperationHelper.CreateProgressItem(
                                            entry, BatchItemStatus.Error, "extraction returned null"));
                                    BatchDrawer.UpdateStats(count, total, errorCount, stopwatch.Elapsed);
                                });
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            var count = Interlocked.Increment(ref processed);
                            var errorCount = Interlocked.Increment(ref errors);
                            DispatcherQueue.TryEnqueue(() =>
                            {
                                if (generation != _projectGeneration || _switchingProject)
                                {
                                    return;
                                }

                                BatchDrawer.AddResult(
                                    BatchOperationHelper.CreateProgressItem(
                                        entry, BatchItemStatus.Error, ex.Message));
                                BatchDrawer.UpdateStats(count, total, errorCount, stopwatch.Elapsed);
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
                                    project.Entries[key] =
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
                        var capturedErrors = Volatile.Read(ref errors);

                        DispatcherQueue.TryEnqueue(() =>
                        {
                            if (generation != _projectGeneration || _switchingProject)
                            {
                                return;
                            }

                            BatchDrawer.AddResult(
                                BatchOperationHelper.CreateProgressItem(
                                    entry, capturedStatus, capturedText));
                            BatchDrawer.UpdateStats(count, total, capturedErrors, stopwatch.Elapsed);
                        });

                        // Auto-save every 10 entries
                        if (count % 10 == 0)
                        {
                            try
                            {
                                await SaveProjectAsync(directory, project, ct);
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
            await SaveProjectAsync(directory, project, ct);
            BatchDrawer.CompleteBatch(
                BatchOperationHelper.FormatCompletionMessage(processed, errors, stopwatch.Elapsed));
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            await BatchOperationHelper.DrainConsumersAsync(consumerTasks);
            await SaveProjectAsync(directory, project);
            BatchDrawer.CompleteBatch(BatchOperationHelper.FormatCancellationMessage(processed));
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            await BatchOperationHelper.DrainConsumersAsync(consumerTasks);
            await SaveProjectAsync(directory, project);
            BatchDrawer.CompleteBatch($"Error: {ex.Message}");
        }
        finally
        {
            try
            {
                // All consumers are done -- safe to dispose processors
                foreach (var p in processors)
                {
                    await p.DisposeAsync();
                }
            }
            finally
            {
                _batchCts?.Dispose();
                _batchCts = null;
                UpdateBatchButtonState();
                ExportButton.IsEnabled = BatchOperationHelper.ShouldEnableExport(_project, _allEntries);
                ApplyFilters();
            }
        }
    }

    // ────────────────────────────────────────────────────
    // Export
    // ────────────────────────────────────────────────────

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_switchingProject || _exportInProgress ||
            !BatchOperationHelper.HasExportableContent(_project, _showEsmSubtitles, _allEntries))
        {
            return;
        }

        var project = _project;
        var entries = _allEntries;
        var includeEsm = _showEsmSubtitles;
        var generation = _projectGeneration;
        var exportStarted = false;
        try
        {
            var hwnd = WindowNative.GetWindowHandle(MainWindow.Instance!);
            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            var picker = new FileSavePicker(windowId)
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = "transcriptions"
            };
            picker.FileTypeChoices.Add("CSV", [".csv"]);
            picker.FileTypeChoices.Add("Plain Text", [".txt"]);

            var result = await picker.PickSaveFileAsync();
            if (result != null && generation == _projectGeneration && !_switchingProject && !_exportInProgress)
            {
                exportStarted = true;
                _exportInProgress = true;
                TranscriptionProject snapshot;
                lock (_projectLock)
                {
                    snapshot = project == null
                        ? new TranscriptionProject()
                        : BatchOperationHelper.CreateProjectSnapshot(
                            project, new Dictionary<string, TranscriptionEntry>(project.Entries));
                }

                var path = result.Path;
                var ext = Path.GetExtension(path);
                if (string.Equals(ext, ".csv", StringComparison.OrdinalIgnoreCase))
                {
                    await TranscriptionFileService.ExportCsvAsync(path, snapshot, entries, includeEsm);
                }
                else
                {
                    await TranscriptionFileService.ExportTextAsync(path, snapshot, entries, includeEsm);
                }

                if (generation == _projectGeneration && !_switchingProject)
                {
                    var esmNote = includeEsm ? " (including ESM subtitles)" : "";
                    MainWindow.Instance?.SetStatus($"Exported transcriptions to {Path.GetFileName(path)}{esmNote}");
                }
            }
        }
        catch (Exception ex)
        {
            if (generation == _projectGeneration && !_switchingProject)
            {
                MainWindow.Instance?.SetStatus($"Export error: {ex.Message}");
            }
        }
        finally
        {
            if (exportStarted)
            {
                _exportInProgress = false;
            }
        }
    }
}
