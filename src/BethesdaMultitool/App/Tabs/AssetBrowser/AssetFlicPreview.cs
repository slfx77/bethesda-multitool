using BethesdaMultitool.Core.AssetBrowse;
using Microsoft.UI.Xaml.Media;
using Slfx77.Multitool.Core.Browsing;
using Slfx77.Multitool.Core.Media;
using Slfx77.Multitool.WinUI.Playback;
using Windows.Media.Playback;

namespace BethesdaMultitool;

/// <summary>Owns one retained native FLC preview without changing the browser's existing dispatch.</summary>
/// <remarks>Use on the view dispatcher. Selection preparation, native retirement and the borrowed view have
/// distinct lifetimes. The application policy loops finite FLC input; the native probe verifies that behavior.
/// Shared owns the transferred decoded session and re-wraps that same session in a new bridge and player whenever
/// the view's whole-scale fit changes (a resize or the Filtering toggle), so <see cref="Player" /> must be re-read
/// after <see cref="PlayerReplaced" />; the stored geometry published here does not change across that.</remarks>
internal sealed class AssetFlicPreview : IAsyncDisposable
{
    /// <summary>Two decoded samples ahead of the clock, the default enlarged-frame ceiling, and looping on every player Shared creates.</summary>
    private static readonly VideoPresentationOptions Options = new(MaximumBufferedSamples: 2, Loop: true);
    private readonly NativeMediaPreview _view;
    private readonly NativeMediaSession _media;
    private readonly List<object> _failedOwners = [];
    private CancellationTokenSource? _selection;
    private Task _work = Task.CompletedTask;
    private Task? _shutdown;
    private Exception? _retirementFailure;
    private long _revision;
    private int _adoptions;
    private bool _disposed;
    private bool _nativeRetired;

    /// <summary>Borrows the retained view, configured with exact canvas fit, the Filtering toggle and no invented audio presentation.</summary>
    internal AssetFlicPreview(NativeMediaPreview view)
    {
        ArgumentNullException.ThrowIfNull(view);
        _view = view;
        _view.Stretch = Stretch.Uniform;
        _view.IsAudioOnly = false;
        _view.ShowAudioWaveform = false;
        _view.ShowFilteringToggle = true; // Shared shows the pixel-perfect/filtering row only for hosts that present decoded video.
        _media = new NativeMediaSession(view);
        _media.Failed += OnFailed;
        _media.PlayerReplaced += OnPlayerReplaced;
    }

    /// <summary>The borrowed current player, for native observations rather than direct playback commands.</summary>
    /// <remarks>Re-read after <see cref="PlayerReplaced" />: a re-presentation replaces the player without any call here.</remarks>
    internal MediaPlayer? Player => _media.Player;
    /// <summary>The whole factor by which Shared enlarges each stored frame for the current player; 1 while nothing is presented.</summary>
    internal int PresentationScale => _media.PresentationScale;
    /// <summary>Exact adopted source metadata; no session remains owned by this transferred result.</summary>
    internal FlicPreviewPreparation? Current { get; private set; }
    /// <summary>The stored geometry of the current native movie, captured before transfer because the transferred result holds no session.</summary>
    internal DecodedVideoFormat? CurrentVideoFormat { get; private set; }
    /// <summary>The known length of the current native movie, or null while nothing is presented or the decoder states none.</summary>
    internal TimeSpan? CurrentDuration { get; private set; }
    /// <summary>Successful session retirement is required before the caller disposes its borrowed view.</summary>
    internal bool NativeResourcesRetired => _nativeRetired && _retirementFailure is null && _failedOwners.Count == 0;
    /// <summary>Reports only the current native player's actual failure on the owning dispatcher.</summary>
    internal event EventHandler<string>? Failed;
    /// <summary>Raised on the owning dispatcher after every player adoption, including a re-presentation of the same session.</summary>
    internal event EventHandler? PlayerReplaced;

    /// <summary>Replaces exact source selection, returning detached legacy metadata only when still current.</summary>
    /// <param name="snapshot">Exact source opening retained through preparation and adopted decoded samples.</param>
    /// <param name="node">Builder-owned source leaf.</param>
    /// <param name="cancellationToken">The owning pane's selected-source lifetime.</param>
    /// <returns>Current prepared metadata (possibly legacy), or null after replacement/cancellation.</returns>
    internal Task<FlicPreviewPreparation?> ShowAsync(BrowserSnapshot snapshot, AssetNode node,
        CancellationToken cancellationToken = default)
    {
        VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowRetirementFailure();
        var predecessor = _work;
        var previousCancellation = _selection;
        var selection = CancellationTokenSource.CreateLinkedTokenSource(snapshot.CancellationToken, cancellationToken);
        var revision = ++_revision;
        var completion = new TaskCompletionSource<FlicPreviewPreparation?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _selection = selection;
        _work = completion.Task; // Publish every owner before any cancellation callback can reenter.
        Exception? cancellationFailure = null;
        try { previousCancellation?.Cancel(); }
        catch (Exception failure) { cancellationFailure = failure; }
        _ = ShowCoreAsync(predecessor, previousCancellation, selection, revision, snapshot, node,
            cancellationFailure, completion);
        return completion.Task;
    }

    /// <summary>Uses Shared's coordinated native playback intent; initial adoption remains paused.</summary>
    internal void Play() { VerifyAccess(); _media.Play(); }
    /// <summary>Cancels pending native seek-resume intent and pauses the current player.</summary>
    internal void Pause() { VerifyAccess(); _media.Pause(); }
    /// <summary>Uses the actual decoded seek coordinator for explicit seek and replay.</summary>
    internal void Seek(TimeSpan position, bool resumePlayback = false)
    {
        VerifyAccess();
        _media.Seek(position, resumePlayback);
    }

    /// <summary>Cancels the current selection and drains its CPU/native work before releasing a browser source.</summary>
    internal Task ClearAsync()
    {
        VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        var predecessor = _work;
        var selection = _selection;
        _selection = null;
        ++_revision;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _work = completion.Task;
        Exception? failure = null;
        try { selection?.Cancel(); }
        catch (Exception error) { failure = error; }
        _ = ClearCoreAsync(predecessor, selection, failure, completion);
        return completion.Task;
    }

    /// <summary>Closes admission synchronously and observes one terminal cleanup without an implicit retry.</summary>
    public ValueTask DisposeAsync()
    {
        VerifyAccess();
        if (_shutdown is not null) { return new ValueTask(_shutdown); }
        _disposed = true;
        ++_revision;
        var pending = _work;
        var selection = _selection;
        _selection = null;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _shutdown = completion.Task;
        Exception? failure = null;
        try { selection?.Cancel(); }
        catch (Exception error) { failure = error; }
        _ = DisposeCoreAsync(pending, selection, failure, completion);
        return new ValueTask(completion.Task);
    }

    /// <summary>Serializes replacements while the latest published identity independently cancels obsolete preparation.</summary>
    private async Task ShowCoreAsync(Task predecessor, CancellationTokenSource? previousCancellation,
        CancellationTokenSource selection, long revision, BrowserSnapshot snapshot, AssetNode node,
        Exception? cancellationFailure, TaskCompletionSource<FlicPreviewPreparation?> completion)
    {
        FlicPreviewPreparation? prepared = null;
        var nativeAttempted = false;
        try
        {
            try { await predecessor; } catch { /* The prior caller owns its preparation error. Retirement faults stay below. */ }
            previousCancellation?.Dispose();
            if (cancellationFailure is not null) { throw cancellationFailure; }
            ThrowRetirementFailure();
            if (!IsCurrent(revision, selection, snapshot)) { completion.TrySetResult(null); return; }
            await ClearNativeAsync();
            if (!IsCurrent(revision, selection, snapshot)) { completion.TrySetResult(null); return; }
            prepared = await FlicPreviewPreparation.OpenAsync(snapshot, node, selection.Token);
            if (!IsCurrent(revision, selection, snapshot)) { await RetirePreparationAsync(prepared); completion.TrySetResult(null); return; }
            if (prepared.DecodedSession is { } session)
            {
                // Captured before transfer: after TakeDecodedSession the result holds no session to read geometry from.
                var format = session.Description.Tracks[0].Video!;
                var duration = session.Description.Duration;
                nativeAttempted = true;
                // PlayerReplaced is raised inside Shared's adoption, before the synchronous re-plan that ends
                // PresentDecodedAsync; a re-presentation begun there has already bumped this count while Player reads null.
                var adoptions = _adoptions;
                await _media.PresentDecodedAsync(token =>
                {
                    token.ThrowIfCancellationRequested();
                    if (!IsCurrent(revision, selection, snapshot)) { throw new OperationCanceledException(selection.Token); }
                    // Shared owns the session, and with it the entire source-lease chain, from the moment this returns.
                    return Task.FromResult(prepared.TakeDecodedSession());
                }, Options, autoPlay: false, cancellationToken: selection.Token);
                if (!IsCurrent(revision, selection, snapshot))
                {
                    await ClearNativeAsync();
                    await RetirePreparationAsync(prepared);
                    completion.TrySetResult(null);
                    return;
                }
                if (_adoptions == adoptions) { throw new InvalidOperationException("FLC native adoption produced no player."); }
                Current = prepared;
                CurrentVideoFormat = format;
                CurrentDuration = duration;
            }
            await RetirePreparationAsync(prepared); // Transferred or legacy results contain no live prerequisites.
            completion.TrySetResult(prepared);
        }
        catch (Exception failure)
        {
            // Preserve the original failed preparation before additional native cleanup wraps its exception.
            if (failure.Data["RetainedFlicPreparation"] is { } retained)
            {
                _failedOwners.Add(retained);
                _retirementFailure ??= failure;
            }
            if (nativeAttempted && _retirementFailure is null)
            {
                try { await ClearNativeAsync(); }
                catch (Exception cleanup) { failure = new AggregateException(failure, cleanup); }
            }
            if (prepared is not null)
            {
                try { await RetirePreparationAsync(prepared); }
                catch (Exception cleanup) { failure = new AggregateException(failure, cleanup); }
            }
            if (failure is OperationCanceledException && selection.IsCancellationRequested && _retirementFailure is null)
            {
                completion.TrySetResult(null);
            }
            else { completion.TrySetException(failure); }
        }
    }

    /// <summary>Checks the exact published selection and source cancellation after every asynchronous boundary.</summary>
    private bool IsCurrent(long revision, CancellationTokenSource selection, BrowserSnapshot snapshot) =>
        !_disposed && revision == _revision && ReferenceEquals(selection, _selection) &&
        !selection.IsCancellationRequested && !snapshot.CancellationToken.IsCancellationRequested;

    /// <summary>Clears native resources and retains any failed native owner as a permanent admission barrier.</summary>
    private async Task ClearNativeAsync()
    {
        ThrowRetirementFailure();
        try { await _media.ClearAsync(); ClearCurrent(); }
        catch (Exception failure) { _retirementFailure = failure; throw; }
    }

    /// <summary>Forgets the transferred result and its published geometry once nothing native is current.</summary>
    private void ClearCurrent()
    {
        Current = null;
        CurrentVideoFormat = null;
        CurrentDuration = null;
    }

    /// <summary>Observes one preparation's terminal cleanup and retains failures without retrying its prerequisite.</summary>
    private async Task RetirePreparationAsync(FlicPreviewPreparation prepared)
    {
        try { await prepared.DisposeAsync(); }
        catch (Exception failure)
        {
            if (!_failedOwners.Contains(prepared)) { _failedOwners.Add(prepared); }
            _retirementFailure ??= failure;
            throw;
        }
    }

    /// <summary>Drains the published predecessor before native clear, including when ordinary preparation failed.</summary>
    private async Task ClearCoreAsync(Task predecessor, CancellationTokenSource? selection, Exception? failure,
        TaskCompletionSource completion)
    {
        try
        {
            try { await predecessor; } catch { /* Preserve terminal retirement failures separately. */ }
            selection?.Dispose();
            await ClearNativeAsync();
            if (failure is not null) { throw failure; }
            completion.TrySetResult();
        }
        catch (Exception error) { completion.TrySetException(error); }
    }

    /// <summary>Attempts session shutdown independently of preparation failure, retaining all failed owners.</summary>
    private async Task DisposeCoreAsync(Task pending, CancellationTokenSource? selection, Exception? failure,
        TaskCompletionSource completion)
    {
        try { await pending; } catch (Exception error) { failure = Combine(failure, error); }
        selection?.Dispose();
        try { await _media.DisposeAsync(); _nativeRetired = true; }
        catch (Exception error) { _retirementFailure ??= error; failure = Combine(failure, error); }
        if (_nativeRetired)
        {
            _media.Failed -= OnFailed;
            _media.PlayerReplaced -= OnPlayerReplaced;
            ClearCurrent();
        }
        if (_retirementFailure is { } retained) { failure = Combine(failure, retained); }
        if (failure is not null) { completion.TrySetException(failure); return; }
        completion.TrySetResult();
    }

    /// <summary>Preserves both independent cleanup failures instead of replacing the first failure.</summary>
    private static Exception Combine(Exception? previous, Exception next) =>
        previous is null ? next : new AggregateException(previous, next);

    /// <summary>Blocks replacement after actual retirement failure without starting cleanup again.</summary>
    private void ThrowRetirementFailure()
    {
        if (_retirementFailure is { } failure) { throw new InvalidOperationException("FLC prerequisites remain retained after failed retirement.", failure); }
    }

    /// <summary>Forwards only current-player failures supplied by Shared on the owning dispatcher.</summary>
    private void OnFailed(object? sender, string message) { if (!_disposed) { Failed?.Invoke(this, message); } }

    /// <summary>Counts every adoption Shared performs and forwards it, so observers move to the replacement player.</summary>
    private void OnPlayerReplaced(object? sender, EventArgs args)
    {
        _adoptions++;
        if (!_disposed) { PlayerReplaced?.Invoke(this, EventArgs.Empty); }
    }

    /// <summary>Rejects foreign-thread mutation of native controls and selection ownership.</summary>
    private void VerifyAccess()
    {
        if (!_view.DispatcherQueue.HasThreadAccess) { throw new InvalidOperationException("FLC preview ownership requires its dispatcher."); }
    }
}
