using BethesdaMultitool.Core.AssetBrowse;
using BethesdaMultitool.Core.Formats.Travels.Shadowkey;
using BethesdaMultitool.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Slfx77.Multitool.Core.Browsing;
using Slfx77.Multitool.WinUI.Direct3D12;
using Slfx77.Multitool.WinUI.Direct3D12.Scenes;
using Slfx77.Multitool.WinUI.Workflows;

namespace BethesdaMultitool;

/// <summary>Owns pack-slot selection and tracked native presentation within the existing Assets pane.</summary>
public sealed partial class AssetBrowserTab
{
    private NativeScenePresenter? _shadowkeyPresenter;
    private IWindowWorkflowServices? _shadowkeyWindow;
    private ShadowkeyPackPreviewSource? _shadowkeyCatalog;
    private ShadowkeyPackSelection? _shadowkeySelection;
    private CancellationTokenSource? _shadowkeyCancellation;
    private Task _shadowkeyWork = Task.CompletedTask;
    private Task _sourceCloseWork = Task.CompletedTask;
    private long _shadowkeyRevision;
    private bool _shadowkeyActive;
    private bool _shadowkeyUpdatingOptions;
    private bool _shadowkeySourceClosing;
    private NativeSceneCameraState? _shadowkeyRetainedCamera;

    /// <summary>Proves that this pane no longer borrows window graphics or native localization bindings.</summary>
    /// <remarks>True before configuration and only after successful asynchronous native retirement thereafter.</remarks>
    public bool NativeResourcesRetired { get; private set; } = true;

    /// <summary>Connects the retained preview to the existing window owners without opening a source.</summary>
    /// <param name="graphics">Window graphics kept alive until native retirement succeeds.</param>
    /// <param name="window">The same window's localization, picker and diagnostic services.</param>
    public void EnableShadowkeyPreview(Direct3D12Graphics graphics, IWindowWorkflowServices window)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(graphics);
        ArgumentNullException.ThrowIfNull(window);
        if (_shadowkeyPresenter is not null) throw new InvalidOperationException("Shadowkey preview is already configured.");
        NativeResourcesRetired = false;
        _shadowkeyWindow = window;
        _shadowkeyPresenter = new NativeScenePresenter(ShadowkeyNativePreview, graphics, window);
        _shadowkeyPresenter.PresentationFailed += ShadowkeyPresentationFailed;
        _shadowkeyPresenter.SetPresentationActive(_shadowkeyActive && !_previewCollapsed);
    }

    /// <summary>Suspends shared presentation when Explore or its Assets pane is hidden, or the preview pane is collapsed.</summary>
    /// <param name="active">Whether this exact pane is visible in the containing window.</param>
    public void SetShadowkeyPresentationActive(bool active)
    {
        _shadowkeyActive = active;
        _shadowkeyPresenter?.SetPresentationActive(active && !_previewCollapsed);
    }

    /// <summary>Selects only a real current models.huge leaf without changing its tree or export checks.</summary>
    /// <param name="node">The original selected tree object.</param>
    /// <returns>Whether this selection belongs to the configured Shadowkey pack workflow.</returns>
    private bool SelectShadowkeyPack(AssetNode node)
    {
        ResetShadowkeyCatalog();
        if (_shadowkeyPresenter is null) return false;
        var snapshot = _sourceSnapshot;
        if (snapshot is null || !IsCurrentAssetNode(node) || !ShadowkeyPackPreviewSource.IsCandidate(node))
        {
            QueueShadowkey(null, (_, _) => Task.CompletedTask);
            return false;
        }

        _meshLoad.Cancel();
        _meshViewer?.ClearScene();
        if (_meshViewer is not null) _meshViewer.Visibility = Visibility.Collapsed;
        ShadowkeyPanel.Visibility = Visibility.Visible;
        RuntimeLocalization.SetText(ShadowkeyStatus, "Shadowkey_Opening", node.VirtualPath);
        var generation = _sourceGeneration;
        QueueShadowkey(snapshot, async (revision, token) =>
        {
            var catalog = await ShadowkeyPackPreviewSource.OpenAsync(snapshot, node, token);
            RequireShadowkeyCurrent(snapshot, generation, revision, token);
            if (!IsCurrentAssetNode(node)) throw new OperationCanceledException(token);
            _shadowkeyCatalog = catalog;
            ShadowkeySlots.ItemsSource = catalog.Entries;
            RuntimeLocalization.SetText(ShadowkeyStatus, "Shadowkey_SelectSlot", catalog.Entries.Count);
            if (catalog.Entries.Count != 0) ShadowkeySlots.SelectedIndex = 0;
        });
        return true;
    }

    /// <summary>Invalidates catalog and selection authority before clearing the retained slot controls.</summary>
    private void ResetShadowkeyCatalog()
    {
        _shadowkeyCatalog = null;
        _shadowkeySelection = null;
        _shadowkeyRetainedCamera = null;
        _shadowkeyUpdatingOptions = true;
        try
        {
            ShadowkeySlots.ItemsSource = null;
            ShadowkeyFrame.Value = 0;
            ShadowkeySkin.Value = 0;
            ShadowkeyMagenta.IsChecked = false;
            SetShadowkeyRanges(0, 0);
        }
        finally { _shadowkeyUpdatingOptions = false; }
        ShadowkeyPanel.Visibility = Visibility.Collapsed;
    }

    /// <summary>Resets explicit sampling options for a newly selected original pack slot.</summary>
    /// <param name="sender">The retained typed entry list.</param>
    /// <param name="args">The completed selection notification.</param>
    private void ShadowkeySlots_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_shadowkeyUpdatingOptions || _disposed || _shadowkeySourceClosing) return;
        if (_shadowkeyCatalog is null || ShadowkeySlots.SelectedItem is not ShadowkeyModelPackEntry)
        {
            _shadowkeySelection = null;
            if (_shadowkeyPresenter is not null) QueueShadowkey(_sourceSnapshot, (_, _) => Task.CompletedTask);
            return;
        }
        _shadowkeyUpdatingOptions = true;
        try
        {
            ShadowkeyFrame.Value = 0;
            ShadowkeySkin.Value = 0;
            ShadowkeyMagenta.IsChecked = false;
            SetShadowkeyRanges(0, 0);
        }
        finally { _shadowkeyUpdatingOptions = false; }
        PrepareShadowkeySelection();
    }

    /// <summary>Reprepares the exact slot after a whole-number frame or skin edit.</summary>
    /// <param name="sender">The frame or skin control.</param>
    /// <param name="args">The committed numeric value.</param>
    private void ShadowkeyOption_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) =>
        PrepareShadowkeySelection();

    /// <summary>Applies color keying only when the user explicitly changes the default-off option.</summary>
    /// <param name="sender">The retained color-key option.</param>
    /// <param name="args">The completed check notification.</param>
    private void ShadowkeyMagenta_Changed(object sender, RoutedEventArgs args) => PrepareShadowkeySelection();

    /// <summary>Captures immutable source, catalog, slot and sampling options before background preparation.</summary>
    private void PrepareShadowkeySelection()
    {
        if (_shadowkeyUpdatingOptions || _disposed || _shadowkeySourceClosing || _shadowkeyPresenter is null ||
            _shadowkeyCatalog is not { } catalog || ShadowkeySlots.SelectedItem is not ShadowkeyModelPackEntry entry ||
            !ReferenceEquals(catalog.Snapshot, _sourceSnapshot) || catalog.Snapshot.CancellationToken.IsCancellationRequested ||
            (uint)entry.Index >= (uint)catalog.Entries.Count || !ReferenceEquals(catalog.Entries[entry.Index], entry)) return;
        var frame = ShadowkeyFrame.Value;
        var skin = ShadowkeySkin.Value;
        if (!double.IsInteger(frame) || !double.IsInteger(skin) ||
            frame < 0 || skin < 0 || frame > int.MaxValue || skin > int.MaxValue)
        {
            _shadowkeySelection = null;
            QueueShadowkey(catalog.Snapshot, (_, _) => Task.CompletedTask);
            RuntimeLocalization.SetText(ShadowkeyStatus, "Shadowkey_WholeNumbers");
            return;
        }

        ShadowkeyPackSelection selection;
        try { selection = catalog.CreateSelection(entry.Index, (int)frame, (int)skin, ShadowkeyMagenta.IsChecked == true); }
        catch (OperationCanceledException)
        {
            _shadowkeySelection = null;
            QueueShadowkey(null, (_, _) => Task.CompletedTask);
            return;
        }
        // Recomposition retains the same slot's camera; a different catalog or slot starts with fresh framing.
        if (_shadowkeySelection is { } previous && ReferenceEquals(previous.Source, catalog) && previous.Slot == selection.Slot)
            _shadowkeyRetainedCamera = _shadowkeyPresenter.CaptureCameraState() ?? _shadowkeyRetainedCamera;
        else
            _shadowkeyRetainedCamera = null;
        var retainedCamera = _shadowkeyRetainedCamera;
        _shadowkeySelection = selection;
        var generation = _sourceGeneration;
        RuntimeLocalization.SetText(ShadowkeyStatus, "Shadowkey_Preparing", selection.Slot);
        QueueShadowkey(catalog.Snapshot, (revision, token) =>
            AdoptShadowkeyAsync(selection, generation, revision, retainedCamera, token));
    }

    /// <summary>Adopts only the exact admitted document and retains shared source ownership through presentation.</summary>
    /// <param name="selection">The immutable catalog and selected options.</param>
    /// <param name="generation">The browser source generation captured by the UI.</param>
    /// <param name="revision">The serialized preview request.</param>
    /// <param name="retainedCamera">A same-slot camera captured before cancellation, or null for fresh framing.</param>
    /// <param name="token">Linked source and selection cancellation.</param>
    /// <returns>Completion of background preparation and shared native adoption, including explicit decline clearing.</returns>
    private async Task AdoptShadowkeyAsync(ShadowkeyPackSelection selection, int generation, long revision,
        NativeSceneCameraState? retainedCamera, CancellationToken token)
    {
        var catalog = selection.Source;
        var preview = await Task.Run(() => catalog.Prepare(selection, token), token);
        RequireShadowkeySelection(selection, generation, revision, token);
        _shadowkeyUpdatingOptions = true;
        try { SetShadowkeyRanges(preview.FrameCount, preview.SkinCount); }
        finally { _shadowkeyUpdatingOptions = false; }
        if (preview.Scene is null)
        {
            await _shadowkeyPresenter!.ClearAsync();
            RequireShadowkeySelection(selection, generation, revision, token);
            RuntimeLocalization.SetText(ShadowkeyStatus, "Shadowkey_Unsupported", selection.Slot, preview.UnsupportedReason);
            return;
        }

        ShadowkeyNativePreview.Visibility = Visibility.Visible;
        var adopted = await _shadowkeyPresenter!.TryShowAsync(async cancellation =>
        {
            var presentation = await Task.Run(() => ShadowkeyNativePresentation.Create(preview, cancellation), cancellation);
            RequireShadowkeySelection(selection, generation, revision, cancellation);
            return presentation;
        }, catalog.Snapshot, preserveCamera: retainedCamera is not null, eyeHeight: null,
            retainedCamera: retainedCamera, cancellationToken: token);
        RequireShadowkeySelection(selection, generation, revision, token);
        if (!adopted)
        {
            await _shadowkeyPresenter.ClearAsync();
            RequireShadowkeySelection(selection, generation, revision, token);
            RuntimeLocalization.SetText(ShadowkeyStatus, "Shadowkey_NativeUnavailable");
            return;
        }
        _shadowkeyPresenter.SetPresentationActive(_shadowkeyActive && !_previewCollapsed);
        RuntimeLocalization.SetText(ShadowkeyStatus, "Shadowkey_Ready", selection.Slot, selection.Frame, selection.Skin);
    }

    /// <summary>Updates only the decoded slot's selectable range without inventing animation playback.</summary>
    /// <param name="frames">Actual decoded frame count; zero for an unavailable record.</param>
    /// <param name="skins">Actual decoded alternative skin count; zero for an unavailable record.</param>
    private void SetShadowkeyRanges(int frames, int skins)
    {
        ShadowkeyFrame.Maximum = Math.Max(0, frames - 1);
        ShadowkeySkin.Maximum = Math.Max(0, skins - 1);
        ShadowkeyFrame.IsEnabled = frames > 1;
        ShadowkeySkin.IsEnabled = skins > 1;
        ShadowkeyMagenta.IsEnabled = frames > 0 && skins > 0;
    }

    /// <summary>Publishes one tracked task before callbacks and serializes retirement before replacement adoption.</summary>
    /// <param name="snapshot">The exact source whose cancellation owns this request, or null for clearing.</param>
    /// <param name="work">The observed open, prepare or failure operation running on the UI context.</param>
    private void QueueShadowkey(BrowserSnapshot? snapshot, Func<long, CancellationToken, Task> work)
    {
        if (_disposed || _shadowkeySourceClosing) return;
        var previous = _shadowkeyWork;
        var previousCancellation = _shadowkeyCancellation;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(snapshot?.CancellationToken ?? CancellationToken.None);
        _shadowkeyCancellation = cancellation;
        var revision = ++_shadowkeyRevision;
        _shadowkeyWork = RunShadowkeyAsync(previous, previousCancellation, revision, work, cancellation.Token);
        try { previousCancellation?.Cancel(); }
        catch (Exception failure) { ReportShadowkeyFailure(failure); }
    }

    /// <summary>Observes every request, clears stale native state, and reports failures without poisoning later slots.</summary>
    /// <param name="previous">The preceding fully observed request.</param>
    /// <param name="previousCancellation">Canceled predecessor lifetime, retired only after its task completes.</param>
    /// <param name="revision">The current queue revision.</param>
    /// <param name="work">Preparation and adoption performed only after the preceding native view clears.</param>
    /// <param name="token">Source and selection lifetime retained after successful adoption.</param>
    /// <returns>The tracked request including cancellation cleanup.</returns>
    private async Task RunShadowkeyAsync(Task previous, CancellationTokenSource? previousCancellation, long revision,
        Func<long, CancellationToken, Task> work, CancellationToken token)
    {
        await Task.Yield();
        try { await previous; }
        catch (Exception failure) { ReportShadowkeyFailure(failure); }
        try { previousCancellation?.Dispose(); }
        catch (Exception failure) { ReportShadowkeyFailure(failure); }
        try
        {
            await _shadowkeyPresenter!.ClearAsync();
            token.ThrowIfCancellationRequested();
            if (_disposed || revision != _shadowkeyRevision) return;
            await work(revision, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || revision != _shadowkeyRevision || _disposed)
        {
            await ClearShadowkeyAfterFailureAsync();
        }
        catch (Exception failure)
        {
            try
            {
                if (!_disposed && !_shadowkeySourceClosing && revision == _shadowkeyRevision)
                {
                    _shadowkeyUpdatingOptions = true;
                    try { SetShadowkeyRanges(0, 0); }
                    finally { _shadowkeyUpdatingOptions = false; }
                    RuntimeLocalization.SetText(ShadowkeyStatus, "Shadowkey_Failed", failure.Message);
                }
            }
            catch (Exception displayFailure) { ReportShadowkeyFailure(displayFailure); }
            ReportShadowkeyFailure(failure);
            await ClearShadowkeyAfterFailureAsync();
        }
    }

    /// <summary>Attempts immediate native cleanup after failed preparation while preserving later shutdown verification.</summary>
    /// <returns>The observed cleanup attempt; full source close and disposal independently require successful retirement.</returns>
    private async Task ClearShadowkeyAfterFailureAsync()
    {
        try { if (_shadowkeyPresenter is not null) await _shadowkeyPresenter.ClearAsync(); }
        catch (Exception failure) { ReportShadowkeyFailure(failure); }
    }

    /// <summary>Reports diagnostics without allowing a reporting failure to orphan the request chain.</summary>
    /// <param name="failure">The original request or cleanup failure.</param>
    private void ReportShadowkeyFailure(Exception failure)
    {
        try { _shadowkeyWindow?.ReportFailure(nameof(AssetBrowserTab), failure); }
        catch (Exception reportingFailure)
        {
            Core.Diagnostics.Logger.Instance.Warn("[AssetBrowser] Shadowkey failure: {0}; reporting: {1}",
                failure.Message, reportingFailure.Message);
        }
    }

    /// <summary>Rejects replaced sources and stale asynchronous completions before any UI publication.</summary>
    /// <param name="snapshot">The originally selected source.</param>
    /// <param name="generation">The captured browser generation.</param>
    /// <param name="revision">The exact queued request.</param>
    /// <param name="token">Linked cancellation checked before identity.</param>
    private void RequireShadowkeyCurrent(BrowserSnapshot snapshot, int generation, long revision, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_disposed || _shadowkeySourceClosing || generation != _sourceGeneration || revision != _shadowkeyRevision ||
            !ReferenceEquals(snapshot, _sourceSnapshot) || snapshot.CancellationToken.IsCancellationRequested)
            throw new OperationCanceledException(token);
    }

    /// <summary>Also verifies the exact catalog, selection object and selected slot after asynchronous work.</summary>
    /// <param name="selection">The immutable options whose document may be adopted.</param>
    /// <param name="generation">The original source generation.</param>
    /// <param name="revision">The current request revision.</param>
    /// <param name="token">The source and selection lifetime.</param>
    private void RequireShadowkeySelection(ShadowkeyPackSelection selection, int generation, long revision,
        CancellationToken token)
    {
        RequireShadowkeyCurrent(selection.Source.Snapshot, generation, revision, token);
        if (!ReferenceEquals(selection.Source, _shadowkeyCatalog) || !ReferenceEquals(selection, _shadowkeySelection) ||
            ShadowkeySlots.SelectedItem is not ShadowkeyModelPackEntry entry || entry.Index != selection.Slot)
            throw new OperationCanceledException(token);
    }

    /// <summary>Queues observed retirement only for the exact source and native request that failed.</summary>
    /// <param name="identity">The immutable selection supplied with the adopted document.</param>
    /// <param name="snapshot">The presenter's original borrowed source.</param>
    /// <param name="request">The actual shared native request identity.</param>
    /// <param name="failure">The asynchronous presentation failure.</param>
    private void ShadowkeyPresentationFailed(object identity, BrowserSnapshot? snapshot, long request, Exception failure)
    {
        if (_disposed || _shadowkeySourceClosing || identity is not ShadowkeyPackSelection selection ||
            !ReferenceEquals(selection, _shadowkeySelection) || !ReferenceEquals(selection.Source, _shadowkeyCatalog) ||
            !ReferenceEquals(snapshot, _sourceSnapshot) || _shadowkeyPresenter?.IsCurrent(identity, snapshot, request) != true)
            return;
        ReportShadowkeyFailure(failure);
        QueueShadowkey(snapshot, (_, _) =>
        {
            RuntimeLocalization.SetText(ShadowkeyStatus, "Shadowkey_Failed", failure.Message);
            return Task.CompletedTask;
        });
    }

    /// <summary>Revokes all selection authority and drains preparation/adoption before releasing the source lease.</summary>
    /// <returns>Successful native clearing, or a failure that keeps the existing source owner retained.</returns>
    private async Task ClearShadowkeySourceAsync()
    {
        ++_shadowkeyRevision;
        var cancellation = _shadowkeyCancellation;
        var pending = _shadowkeyWork;
        if (cancellation is not null) await cancellation.CancelAsync();
        ResetShadowkeyCatalog();
        await pending;
        if (_shadowkeyPresenter is not null) await _shadowkeyPresenter.ClearAsync();
        cancellation?.Dispose();
        if (ReferenceEquals(cancellation, _shadowkeyCancellation)) _shadowkeyCancellation = null;
    }

    /// <summary>Drains all caller tasks before the shared presenter retires its controls, source and graphics borrows.</summary>
    /// <returns>Successful retirement proof; failures remain observable by the window close gate.</returns>
    private async Task DisposeShadowkeyAsync()
    {
        ++_shadowkeyRevision;
        List<Exception> failures = [];
        try { if (_shadowkeyCancellation is { } cancellation) await cancellation.CancelAsync(); }
        catch (Exception failure) { failures.Add(failure); }
        try { ResetShadowkeyCatalog(); }
        catch (Exception failure) { failures.Add(failure); }
        try { await _shadowkeyWork; }
        catch (Exception failure) { failures.Add(failure); }
        // The serialized chain is settled even when its last task faulted; no caller work remains.
        _shadowkeyWork = Task.CompletedTask;
        if (_shadowkeyPresenter is { } presenter)
        {
            presenter.PresentationFailed -= ShadowkeyPresentationFailed;
            try
            {
                await presenter.DisposeAsync();
                _shadowkeyPresenter = null;
                NativeResourcesRetired = true;
            }
            catch (Exception failure) { failures.Add(failure); }
        }
        else if (!NativeResourcesRetired)
        {
            failures.Add(new InvalidOperationException("Shadowkey native configuration did not complete; retirement is unproven."));
        }
        try { _shadowkeyCancellation?.Dispose(); }
        catch (Exception failure) { failures.Add(failure); }
        _shadowkeyCancellation = null;
        if (failures.Count != 0) throw new AggregateException("Shadowkey preview retirement failed.", failures);
    }
}
