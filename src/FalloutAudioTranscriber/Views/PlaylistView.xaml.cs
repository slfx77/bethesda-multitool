using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Channels;
using FalloutAudioTranscriber.Models;
using FalloutAudioTranscriber.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Whisper.net;
using Windows.Storage.Pickers;

namespace FalloutAudioTranscriber.Views;

#pragma warning disable CA1001 // WinUI 3 UserControls don't implement IDisposable
public sealed partial class PlaylistView : UserControl
{
    private readonly AudioPlaybackService _playbackService = new();
    private readonly WhisperTranscriptionService _whisperService = new();
    private readonly ObservableCollection<VoiceFileEntry> _displayedEntries = [];
    private List<VoiceFileEntry> _allEntries = [];

    // Transcription state
    private TranscriptionProject? _project;
    private string? _dataDirectory;
    private CancellationTokenSource? _batchCts;
    private DispatcherTimer? _autoSaveTimer;
    private bool _hasUnsavedChanges;
    private bool _whisperInitialized;

    // Filter state
    private string _searchQuery = "";
    private bool _showEsmSubtitles;
    private bool _filtersInitialized;

    // Sort state
    private string _sortColumn = "Status";
    private bool _sortAscending = true;

    public PlaylistView()
    {
        InitializeComponent();
        FileListView.ItemsSource = _displayedEntries;
        AudioPlayer.SetPlaybackService(_playbackService);

        _autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _autoSaveTimer.Tick += AutoSaveTimer_Tick;

        DetailPanel.ApproveRequested += DetailPanel_ApproveRequested;
        DetailPanel.TranscribeRequested += DetailPanel_TranscribeRequested;
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
        ApplyFilters();

        // Enable buttons
        var hasUntranscribed = _allEntries.Any(e => e.Status == TranscriptionStatus.Untranscribed);
        BatchButton.IsEnabled = hasUntranscribed;
        ExportButton.IsEnabled = _project?.Entries.Count > 0 || _allEntries.Any(e2 => e2.SubtitleText != null);
        ClearWhisperButton.IsEnabled = _project?.Entries.Values.Any(e => e.Source == "whisper") == true;

        // Initialize Whisper in background
        if (hasUntranscribed)
        {
            _ = InitializeWhisperAsync();
        }
    }

    private void PopulateFilterDropdowns()
    {
        var source = ShowEsmCheck.IsChecked == true
            ? _allEntries
            : _allEntries.Where(e => e.Status != TranscriptionStatus.EsmSubtitle).ToList();

        var speakers = source
            .Select(e => e.SpeakerName ?? "(Unknown)")
            .Distinct()
            .OrderBy(s => s)
            .Prepend("All Speakers")
            .ToList();
        SpeakerFilter.ItemsSource = speakers;
        SpeakerFilter.SelectedIndex = 0;

        var quests = source
            .Select(e => e.QuestName ?? "(No Quest)")
            .Distinct()
            .OrderBy(q => q)
            .Prepend("All Quests")
            .ToList();
        QuestFilter.ItemsSource = quests;
        QuestFilter.SelectedIndex = 0;

        var voiceTypes = source
            .Select(e => e.VoiceType)
            .Where(v => !string.IsNullOrEmpty(v))
            .Distinct()
            .OrderBy(v => v)
            .Prepend("All Voice Types")
            .ToList();
        VoiceTypeFilter.ItemsSource = voiceTypes;
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

        // Repopulate filter dropdowns when ESM checkbox changes
        if (ReferenceEquals(sender, ShowEsmCheck))
        {
            _filtersInitialized = false;
            PopulateFilterDropdowns();
            _filtersInitialized = true;
        }

        ApplyFilters();
    }

    private void ApplyFilters()
    {
        if (!_filtersInitialized)
        {
            return;
        }

        _showEsmSubtitles = ShowEsmCheck.IsChecked == true;

        var filtered = _allEntries.AsEnumerable();

        // ESM subtitle checkbox
        if (!_showEsmSubtitles)
        {
            filtered = filtered.Where(e => e.Status != TranscriptionStatus.EsmSubtitle);
        }

        // Speaker filter
        var speakerSelection = SpeakerFilter.SelectedItem as string;
        if (speakerSelection != null && speakerSelection != "All Speakers")
        {
            var match = speakerSelection == "(Unknown)" ? null : speakerSelection;
            filtered = filtered.Where(e => e.SpeakerName == match);
        }

        // Quest filter
        var questSelection = QuestFilter.SelectedItem as string;
        if (questSelection != null && questSelection != "All Quests")
        {
            var match = questSelection == "(No Quest)" ? null : questSelection;
            filtered = filtered.Where(e => e.QuestName == match);
        }

        // Voice type filter
        var voiceTypeSelection = VoiceTypeFilter.SelectedItem as string;
        if (voiceTypeSelection != null && voiceTypeSelection != "All Voice Types")
        {
            filtered = filtered.Where(e => e.VoiceType == voiceTypeSelection);
        }

        // Search query
        if (!string.IsNullOrEmpty(_searchQuery))
        {
            var query = _searchQuery;
            filtered = filtered.Where(e =>
                e.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                e.VoiceType.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                e.FormId.ToString("X8").Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (e.SpeakerName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (e.QuestName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (e.SubtitleText?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        // Sort by selected column
        IOrderedEnumerable<VoiceFileEntry> sorted = _sortColumn switch
        {
            "Name" => _sortAscending
                ? filtered.OrderBy(e => e.TopicEditorId).ThenBy(e => e.FormId)
                : filtered.OrderByDescending(e => e.TopicEditorId).ThenByDescending(e => e.FormId),
            "Speaker" => _sortAscending
                ? filtered.OrderBy(e => e.SpeakerName ?? "\uffff").ThenBy(e => e.TopicEditorId)
                : filtered.OrderByDescending(e => e.SpeakerName ?? "").ThenBy(e => e.TopicEditorId),
            "Quest" => _sortAscending
                ? filtered.OrderBy(e => e.QuestName ?? "\uffff").ThenBy(e => e.TopicEditorId)
                : filtered.OrderByDescending(e => e.QuestName ?? "").ThenBy(e => e.TopicEditorId),
            _ => _sortAscending // "Status" default
                ? filtered.OrderBy(e => (int)e.Status).ThenBy(e => e.VoiceType).ThenBy(e => e.TopicEditorId)
                : filtered.OrderByDescending(e => (int)e.Status).ThenBy(e => e.VoiceType).ThenBy(e => e.TopicEditorId)
        };
        var results = sorted.ToList();

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
                selected.SubtitleText = text;
                selected.TranscriptionSource = "whisper";

                if (_project != null)
                {
                    var key = $"{selected.VoiceType}|{selected.FormId:X8}_{selected.ResponseIndex}";
                    _project.Entries[key] = new TranscriptionEntry
                    {
                        Text = text,
                        Source = "whisper",
                        VoiceType = selected.VoiceType,
                        SpeakerName = selected.SpeakerName,
                        QuestName = selected.QuestName,
                        TranscribedAt = DateTimeOffset.UtcNow
                    };

                    _hasUnsavedChanges = true;
                    _autoSaveTimer?.Start();
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

        // Save with "accepted" source
        var key = $"{selected.VoiceType}|{selected.FormId:X8}_{selected.ResponseIndex}";
        _project.Entries[key] = new TranscriptionEntry
        {
            Text = text,
            Source = "accepted",
            VoiceType = selected.VoiceType,
            SpeakerName = selected.SpeakerName,
            QuestName = selected.QuestName,
            TranscribedAt = DateTimeOffset.UtcNow
        };

        selected.SubtitleText = text;
        selected.TranscriptionSource = "accepted";

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
            if (_displayedEntries[i].Status is TranscriptionStatus.Untranscribed or TranscriptionStatus.Automatic)
            {
                FileListView.SelectedIndex = i;
                FileListView.ScrollIntoView(_displayedEntries[i]);
                return;
            }
        }

        // Wrap around
        for (var i = 0; i < currentIndex; i++)
        {
            if (_displayedEntries[i].Status is TranscriptionStatus.Untranscribed or TranscriptionStatus.Automatic)
            {
                FileListView.SelectedIndex = i;
                FileListView.ScrollIntoView(_displayedEntries[i]);
                return;
            }
        }
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

        // Re-apply ESM enrichment for entries that lost their whisper text
        // (ESM subtitles are set during initial load, just re-mark source)
        foreach (var entry in _allEntries)
        {
            if (entry.SubtitleText != null && entry.TranscriptionSource == null)
            {
                entry.TranscriptionSource = "esm";
            }
        }

        await TranscriptionFileService.SaveAsync(_dataDirectory, _project);

        ClearWhisperButton.IsEnabled = false;
        BatchButton.IsEnabled = _allEntries.Any(e2 => e2.Status == TranscriptionStatus.Untranscribed);
        ExportButton.IsEnabled = _project?.Entries.Count > 0 || _allEntries.Any(e2 => e2.SubtitleText != null);

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

    // ────────────────────────────────────────────────────
    // Batch transcription (multithreaded pipeline)
    // ────────────────────────────────────────────────────

    private readonly object _projectLock = new();

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

        var untranscribed = _allEntries
            .Where(e2 => e2.Status == TranscriptionStatus.Untranscribed)
            .ToList();

        var processed = 0;
        var errors = 0;
        var total = untranscribed.Count;
        var workerCount = Math.Max(1, Environment.ProcessorCount / 2);
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
                                    BatchDrawer.AddResult(new BatchProgressItem
                                    {
                                        DisplayName = entry.DisplayName,
                                        VoiceType = entry.VoiceType,
                                        ItemStatus = BatchItemStatus.Error,
                                        TranscriptionPreview = "extraction returned null"
                                    });
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
                                BatchDrawer.AddResult(new BatchProgressItem
                                {
                                    DisplayName = entry.DisplayName,
                                    VoiceType = entry.VoiceType,
                                    ItemStatus = BatchItemStatus.Error,
                                    TranscriptionPreview = ex.Message
                                });
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
                                    _project!.Entries[$"{entry.VoiceType}|{entry.FormId:X8}_{entry.ResponseIndex}"] = new TranscriptionEntry
                                    {
                                        Text = transcribedText,
                                        Source = "whisper",
                                        VoiceType = entry.VoiceType,
                                        SpeakerName = entry.SpeakerName,
                                        QuestName = entry.QuestName,
                                        TranscribedAt = DateTimeOffset.UtcNow
                                    };
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
                            BatchDrawer.AddResult(new BatchProgressItem
                            {
                                DisplayName = entry.DisplayName,
                                VoiceType = entry.VoiceType,
                                ItemStatus = capturedStatus,
                                TranscriptionPreview = capturedText
                            });
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

                            var projectSnapshot = new TranscriptionProject
                            {
                                GameName = _project!.GameName,
                                DataDirectory = _project.DataDirectory,
                                CreatedAt = _project.CreatedAt,
                                ModifiedAt = DateTimeOffset.UtcNow,
                                Entries = snapshotEntries
                            };

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
                    // Expected on cancellation — exit gracefully so the processor can be disposed
                }
            }, CancellationToken.None)).ToArray(); // CancellationToken.None: let consumers drain gracefully

            // Wait for all work to complete
            await producerTask;
            await Task.WhenAll(consumerTasks);

            // Final save
            stopwatch.Stop();
            await TranscriptionFileService.SaveAsync(_dataDirectory, _project, ct);
            BatchDrawer.CompleteBatch($"Done! {processed:N0} entries, {errors} errors, {stopwatch.Elapsed:m\\:ss}");
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();

            // Wait for consumers to finish their current item before disposing processors
            if (consumerTasks != null)
            {
                try { await Task.WhenAll(consumerTasks); } catch { /* consumers already handle their own errors */ }
            }

            await TranscriptionFileService.SaveAsync(_dataDirectory, _project);
            BatchDrawer.CompleteBatch($"Cancelled after {processed:N0} entries");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            if (consumerTasks != null)
            {
                try { await Task.WhenAll(consumerTasks); } catch { /* consumers already handle their own errors */ }
            }

            await TranscriptionFileService.SaveAsync(_dataDirectory, _project);
            BatchDrawer.CompleteBatch($"Error: {ex.Message}");
        }
        finally
        {
            // All consumers are done — safe to dispose processors
            foreach (var p in processors)
            {
                await p.DisposeAsync();
            }

            BatchButton.IsEnabled = _allEntries.Any(e2 => e2.Status == TranscriptionStatus.Untranscribed);
            ExportButton.IsEnabled = _project?.Entries.Count > 0 || _allEntries.Any(e2 => e2.SubtitleText != null);
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
        var hasTranscriptions = _project != null && _project.Entries.Count > 0;
        var hasEsm = _showEsmSubtitles && _allEntries.Any(e2 => e2.TranscriptionSource == "esm");

        if (!hasTranscriptions && !hasEsm)
        {
            return;
        }

        var picker = new FileSavePicker();
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeChoices.Add("CSV", [".csv"]);
        picker.FileTypeChoices.Add("Plain Text", [".txt"]);
        picker.SuggestedFileName = "transcriptions";

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance!);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

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
