using BethesdaMultitool.Core.AssetBrowse;
using BethesdaMultitool.Core.Media.Audio.Dialogue;
using BethesdaMultitool.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Audio;
using Slfx77.Multitool.Core.Browsing;
using Slfx77.Multitool.WinUI.Playback;
using DialoguePluginIdentity = BethesdaMultitool.Core.Formats.Esm.Parsing.DialoguePluginIdentity;

namespace BethesdaMultitool;

/// <summary>Resolves and plays original Fallout voice responses from the current Explore snapshot.</summary>
public sealed class DialogueAudioControl : UserControl, IDisposable, IAsyncDisposable
{
    private readonly TextBlock _identityText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _plugin = new() { MinWidth = 0 };
    private readonly TextBox _voice = new() { MinWidth = 0 };
    private readonly ComboBox _matches = new() { MinWidth = 0, MaxWidth = 800, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _resolve = new();
    private readonly NativeMediaPreview _preview = new() { IsAudioOnly = true, Height = 72, Volume = 0.8 };
    private readonly NativeMediaSession _nativeSession;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<Task> _sourceWork = [];
    private readonly Button _inspectLip = new() { IsEnabled = false };
    private readonly Expander _expander;
    private BrowserSnapshot? _snapshot;
    private Task<DialogueAudioIndex>? _index;
    private DialogueAudioIdentity? _identity;
    private Task? _disposeTask;
    private int _audioFailureGeneration = -1;
    private int _generation;
    private bool _disposed;

    /// <summary>Creates explicit identity, candidate selection, and playback transport controls.</summary>
    public DialogueAudioControl()
    {
        _nativeSession = new NativeMediaSession(_preview);
        _nativeSession.Failed += Player_Failed;
        RuntimeLocalization.Set(_plugin, TextBox.HeaderProperty, "DialogueAudio_Plugin");
        RuntimeLocalization.Set(_voice, TextBox.HeaderProperty, "DialogueAudio_Voice");
        RuntimeLocalization.Set(_voice, TextBox.PlaceholderTextProperty, "DialogueAudio_AllVoices");
        RuntimeLocalization.Set(_resolve, ContentControl.ContentProperty, "DialogueAudio_Find");
        RuntimeLocalization.Set(_inspectLip, ContentControl.ContentProperty, "DialogueAudio_InspectLip");
        RuntimeLocalization.Set(_status, TextBlock.TextProperty, "DialogueAudio_ChooseResponse");
        AutomationProperties.SetAutomationId(_plugin, "DialogueAudio.Plugin");
        AutomationProperties.SetAutomationId(_voice, "DialogueAudio.Voice");
        AutomationProperties.SetAutomationId(_matches, "DialogueAudio.Matches");
        AutomationProperties.SetAutomationId(_preview, "DialogueAudio.Playback");
        AutomationProperties.SetAutomationId(_status, "DialogueAudio.Status");
        AutomationProperties.SetAutomationId(_identityText, "DialogueAudio.Identity");
        AutomationProperties.SetAutomationId(_resolve, "DialogueAudio.Find");
        AutomationProperties.SetAutomationId(_inspectLip, "DialogueAudio.InspectLip");
        RuntimeLocalization.Set(_matches, AutomationProperties.NameProperty, "DialogueAudio_Matches");
        _resolve.Click += Resolve_Click;
        _inspectLip.Click += InspectLip_Click;
        _matches.SelectionChanged += Match_Changed;
        var identity = new Grid { ColumnSpacing = 8 };
        identity.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        identity.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        identity.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_voice, 1);
        Grid.SetColumn(_resolve, 2);
        _resolve.VerticalAlignment = VerticalAlignment.Bottom;
        identity.Children.Add(_plugin);
        identity.Children.Add(_voice);
        identity.Children.Add(_resolve);
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(_identityText);
        body.Children.Add(identity);
        body.Children.Add(_matches);
        body.Children.Add(_preview);
        body.Children.Add(_inspectLip);
        body.Children.Add(_status);
        _expander = new Expander
        {
            Content = body,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        RuntimeLocalization.Set(_expander, Expander.HeaderProperty, "DialogueAudio_Title");
        Content = _expander;
        Visibility = Visibility.Collapsed;
    }

    /// <summary>Publishes a source reference while active work retains its own explicit lease.</summary>
    internal void SetSource(BrowserSnapshot? snapshot)
    {
        if (_disposed) return;
        ResetResponse();
        if (ReferenceEquals(_snapshot, snapshot)) return;
        _snapshot = snapshot;
        _index = null;
    }

    /// <summary>Stops obsolete playback and requests while retaining a same-source metadata index.</summary>
    internal void ResetResponse()
    {
        if (_disposed) return;
        _generation++;
        _ = ClearAudioAsync();
        _identity = null;
        _matches.Items.Clear();
        _inspectLip.IsEnabled = false;
        Visibility = Visibility.Collapsed;
    }

    /// <summary>Shows a selected original response and uses a complete plugin header to resolve its owner when possible.</summary>
    internal async Task ShowResponseAsync(uint originalInfo, byte response, string? recordPath,
        bool usePluginHeader, string? voiceType)
    {
        if (_disposed) return;
        ResetResponse();
        var generation = _generation;
        Visibility = Visibility.Visible;
        _expander.IsExpanded = true;
        _identity = new DialogueAudioIdentity(string.Empty, originalInfo, response, voiceType);
        RuntimeLocalization.Set(_identityText, TextBlock.TextProperty, "DialogueAudio_Identity", $"{originalInfo:X8}", response);
        _voice.Text = voiceType ?? string.Empty;
        RuntimeLocalization.Set(_status, TextBlock.TextProperty, "DialogueAudio_Loading");
        try
        {
            var plugin = usePluginHeader && recordPath is not null
                ? await TrackMetadataAsync(() => Task.Run(() => DialoguePluginIdentity.ResolveOwner(recordPath, originalInfo),
                    _lifetime.Token)) : null;
            if (generation != _generation || _disposed) return;
            _plugin.Text = plugin ?? string.Empty;
            if (string.IsNullOrEmpty(plugin))
            {
                RuntimeLocalization.Set(_status, TextBlock.TextProperty, "DialogueAudio_PluginRequired");
                return;
            }
            await ResolveAsync();
        }
        catch (OperationCanceledException)
        {
            // Closing the pane retires pending header work without publishing obsolete status.
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
        {
            if (generation == _generation && !_disposed) RuntimeLocalization.SetRaw(_status, TextBlock.TextProperty, exception.Message);
        }
    }

    /// <summary>Resolves the explicitly entered plugin and optional voice type for the selected response.</summary>
    private async void Resolve_Click(object sender, RoutedEventArgs args) => await ResolveAsync();

    /// <summary>Reuses one metadata index for a source and reports every remaining candidate.</summary>
    private async Task ResolveAsync()
    {
        if (_identity is null || _disposed) return;
        _ = ClearAudioAsync();
        _matches.Items.Clear();
        // Every explicit lookup retires the prior candidate, including invalid input.
        var generation = ++_generation;
        if (_snapshot is null)
        {
            RuntimeLocalization.Set(_status, TextBlock.TextProperty, "DialogueAudio_SourceRequired");
            return;
        }
        if (string.IsNullOrWhiteSpace(_plugin.Text))
        {
            RuntimeLocalization.Set(_status, TextBlock.TextProperty, "DialogueAudio_PluginRequired");
            return;
        }
        RuntimeLocalization.Set(_status, TextBlock.TextProperty, "DialogueAudio_Loading");
        var identity = _identity with { Plugin = _plugin.Text.Trim(), VoiceType = _voice.Text.Trim() };
        try
        {
            var snapshot = _snapshot;
            _index ??= TrackMetadataAsync(() => BuildIndexAsync(snapshot, _lifetime.Token));
            var index = await _index;
            if (generation != _generation || _disposed) return;
            var result = index.Resolve(identity);
            foreach (var candidate in result.Candidates)
            {
                _matches.Items.Add(new ComboBoxItem
                {
                    Content = $"{candidate.VoiceType}: {candidate.Audio.Path} [{candidate.Audio.Source}]",
                    Tag = candidate
                });
            }
            RuntimeLocalization.Set(_status, TextBlock.TextProperty, "DialogueAudio_" + result.State, result.Candidates.Count);
            if (result.State == DialogueAudioMatchState.Resolved) _matches.SelectedIndex = 0;
        }
        catch (OperationCanceledException)
        {
            // The retired source or pane owns no current metadata result.
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            if (generation == _generation && !_disposed)
            {
                _index = null;
                RuntimeLocalization.SetRaw(_status, TextBlock.TextProperty, exception.Message);
            }
        }
    }

    /// <summary>Retains the archive until metadata indexing finishes, including after source replacement.</summary>
    private static async Task<DialogueAudioIndex> BuildIndexAsync(BrowserSnapshot snapshot, CancellationToken cancellationToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(snapshot.CancellationToken, cancellationToken);
        await using var lease = snapshot.AcquireLease();
        if (snapshot.Source is not BethesdaBrowseSource source)
            throw new InvalidOperationException("The selected source does not provide Bethesda voice metadata.");
        return await Task.Run(() => DialogueAudioIndex.Create(source.Session.FileSystem.EnumerateFiles("sound/voice/"),
            cancellation.Token), cancellation.Token);
    }

    /// <summary>Tracks every active metadata owner, including indexes superseded by source replacement.</summary>
    /// <typeparam name="T">The resolved header or metadata index.</typeparam>
    /// <param name="start">Starts metadata work only after its lifetime has been registered.</param>
    /// <returns>The original result or failure after removing the completed operation from the active set.</returns>
    private async Task<T> TrackMetadataAsync<T>(Func<Task<T>> start)
    {
        Task<T>? work = null;
        await TrackSourceWorkAsync(() => work = start());
        return await work!;
    }

    /// <summary>Retains active header, index and LIP inspection operations until their owners have retired.</summary>
    /// <param name="start">Starts an operation after its lifetime is visible to any reentrant shutdown.</param>
    /// <returns>Completion or failure after removing the operation from the active set.</returns>
    private async Task TrackSourceWorkAsync(Func<Task> start)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _sourceWork.Add(completion.Task);
        try { await start(); }
        finally
        {
            _sourceWork.Remove(completion.Task);
            // The awaited caller observes errors; this marker only records completed resource retirement.
            completion.SetResult();
        }
    }

    /// <summary>Prepares only the explicitly selected supported candidate without starting native playback.</summary>
    private void Match_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (_disposed) return;
        var generation = ++_generation;
        _inspectLip.IsEnabled = _matches.SelectedItem is ComboBoxItem { Tag: DialogueAudioCandidate };
        if (_snapshot is not { } snapshot ||
            _matches.SelectedItem is not ComboBoxItem { Tag: DialogueAudioCandidate candidate })
        {
            _ = ClearAudioAsync();
            return;
        }
        if (Path.GetExtension(candidate.Audio.Path).Equals(".xma", StringComparison.OrdinalIgnoreCase))
        {
            _ = ClearAudioAsync();
            RuntimeLocalization.Set(_status, TextBlock.TextProperty, "DialogueAudio_UnsupportedContainer");
            return;
        }
        RuntimeLocalization.Set(_status, TextBlock.TextProperty, "DialogueAudio_Loading");
        _ = PrepareAudioAsync(snapshot, candidate, generation);
    }

    /// <summary>Inspects a selected voice candidate's LIP companions with explicit source ownership.</summary>
    private async void InspectLip_Click(object sender, RoutedEventArgs args)
    {
        if (_disposed || _snapshot is not { } snapshot ||
            _matches.SelectedItem is not ComboBoxItem { Tag: DialogueAudioCandidate candidate }) return;
        var generation = _generation;
        _inspectLip.IsEnabled = false;
        try { await TrackSourceWorkAsync(() => DialogueLipInspector.ShowAsync(XamlRoot, snapshot, candidate, _lifetime.Token)); }
        catch (OperationCanceledException)
        {
            // Source retirement closes the independent inspection dialog.
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            if (!_disposed && generation == _generation) RuntimeLocalization.SetRaw(_status, TextBlock.TextProperty, exception.Message);
        }
        finally { if (!_disposed && generation == _generation) _inspectLip.IsEnabled = true; }
    }

    /// <summary>Retains the exact source through bounded reading and transfers ownership with a unique native input.</summary>
    /// <param name="snapshot">The selected Explore opening, retained until native input disposal.</param>
    /// <param name="candidate">The original plugin/INFO/response/voice match and exact source provenance.</param>
    /// <param name="generation">The candidate generation allowed to publish status.</param>
    private async Task PrepareAudioAsync(BrowserSnapshot snapshot, DialogueAudioCandidate candidate, int generation)
    {
        try
        {
            await _nativeSession.ReplaceAsync(async cancellationToken =>
            {
                BrowserLease? lease = snapshot.AcquireLease();
                try
                {
                    var source = (BethesdaBrowseSource)snapshot.Source;
                    var payload = await Task.Run(() => source.Session.FileSystem.TryReadAllBytesBounded(
                        candidate.Audio.Path, 64L * 1024 * 1024)
                        ?? throw new InvalidDataException(Strings.Get("DialogueAudio_ReadFailed")), cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!string.Equals(payload.Entry.Source, candidate.Audio.Source, StringComparison.Ordinal) ||
                        !string.Equals(payload.Entry.Path.Replace('\\', '/'), candidate.Audio.Path.Replace('\\', '/'),
                            StringComparison.OrdinalIgnoreCase) || payload.Entry.Size != candidate.Audio.Size)
                    {
                        throw new InvalidDataException(Strings.Get("DialogueAudio_ProvenanceChanged"));
                    }
                    // Entry.Size can be the stored compressed BSA size, not the expanded payload length.
                    var input = await PreparedMediaInput.FromBytesAsync(payload.Data,
                        AudioContainerFormat.ExtensionFor(payload.Data), cancellationToken, lease);
                    lease = null;
                    return input;
                }
                finally
                {
                    if (lease is not null)
                    {
                        await lease.DisposeAsync();
                    }
                }
            }, autoPlay: false, cancellationToken: snapshot.CancellationToken);
            if (generation == _generation && !_disposed && ReferenceEquals(snapshot, _snapshot) &&
                _nativeSession.Player is not null && _audioFailureGeneration != generation)
            {
                RuntimeLocalization.SetRaw(_status, TextBlock.TextProperty, candidate.Audio.Source);
            }
        }
        catch (OperationCanceledException)
        {
            // A replaced candidate/source owns no playable result or status publication.
        }
        catch (Exception exception)
        {
            if (generation == _generation && !_disposed)
            {
                RuntimeLocalization.SetRaw(_status, TextBlock.TextProperty, exception.Message);
            }
        }
    }

    /// <summary>Detaches obsolete playback immediately and observes asynchronous input cleanup.</summary>
    private async Task ClearAudioAsync()
    {
        try { await _nativeSession.ClearAsync(); }
        catch (Exception exception) { Logger.Instance.Warn("[DialogueAudio] Cleanup failed: {0}", exception); }
    }

    /// <summary>Clears response/source identity and releases current native input before source replacement.</summary>
    /// <returns>Completion after current native media and its source lease have retired.</returns>
    internal async ValueTask ClearAsync()
    {
        ResetResponse();
        _snapshot = null;
        _index = null;
        await _nativeSession.ClearAsync();
    }

    /// <summary>Reports an actual failure only from the shared session's current native player.</summary>
    private void Player_Failed(object? sender, string message)
    {
        if (_disposed) return;
        _audioFailureGeneration = _generation;
        RuntimeLocalization.SetRaw(_status, TextBlock.TextProperty, message);
    }

    /// <summary>Begins observed cleanup for legacy synchronous owners.</summary>
    public void Dispose() => _ = DisposeAndReportAsync();

    /// <summary>Drains native input and preparation ownership before the window retires its source.</summary>
    /// <returns>The same cleanup operation for all callers.</returns>
    public ValueTask DisposeAsync()
    {
        if (_disposeTask is null)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = completion.Task;
            _ = CompleteDisposalAsync(completion);
        }
        return new ValueTask(_disposeTask);
    }

    /// <summary>Publishes one cleanup result after the retained task is visible to reentrant disposal callers.</summary>
    /// <param name="completion">The already-published completion marker shared by every disposal caller.</param>
    private async Task CompleteDisposalAsync(TaskCompletionSource completion)
    {
        try
        {
            await DisposeCoreAsync();
            completion.SetResult();
        }
        catch (Exception exception)
        {
            completion.SetException(exception);
        }
    }

    /// <summary>Logs cleanup failures when invoked through the legacy synchronous interface.</summary>
    private async Task DisposeAndReportAsync()
    {
        try { await DisposeAsync(); }
        catch (Exception exception) { Logger.Instance.Warn("[DialogueAudio] Teardown failed: {0}", exception); }
    }

    /// <summary>Retires callbacks and prepared owners before disposing the borrowed native presentation.</summary>
    private async Task DisposeCoreAsync()
    {
        _disposed = true;
        _generation++;
        _matches.SelectionChanged -= Match_Changed;
        _nativeSession.Failed -= Player_Failed;
        _snapshot = null;
        _index = null;
        List<Exception>? failures = null;
        try { await _lifetime.CancelAsync(); }
        catch (Exception exception) { (failures ??= []).Add(exception); }
        try { await _nativeSession.DisposeAsync(); }
        catch (Exception exception) { (failures ??= []).Add(exception); }
        try { await Task.WhenAll(_sourceWork.ToArray()); }
        catch (OperationCanceledException)
        {
            // Canceled metadata tasks are expected; WhenAll has still drained every retained owner.
        }
        catch (Exception exception) { (failures ??= []).Add(exception); }
        try { _preview.Dispose(); }
        catch (Exception exception) { (failures ??= []).Add(exception); }
        _lifetime.Dispose();
        if (failures is { Count: > 0 })
        {
            throw new AggregateException("Dialogue owner cleanup failed.", failures);
        }
    }
}
