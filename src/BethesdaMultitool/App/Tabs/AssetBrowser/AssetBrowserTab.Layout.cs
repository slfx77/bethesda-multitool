using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Ui;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Slfx77.Multitool.Core.Settings;
using Slfx77.Multitool.WinUI.Layout;
using Slfx77.Multitool.WinUI.Localization;

namespace BethesdaMultitool;

/// <summary>
///     Owns the three-column pane layout: the Shared collapsible side panels, the narrow-window width
///     clamp, the pane-toggle accelerators and the persisted widths and collapsed flags.
///     <para>
///         In-session the Shared controller restores the exact expanded width on expand. Across
///         sessions <see cref="AssetPaneLayout" /> is written to the workflow store, debounced after a
///         resize and immediately after a collapse or expand; the pending write is awaited at disposal.
///         Collapsing the preview pane hides its expanded card, an ancestor of every media host, so the
///         media hosts stop through their own presentation activity. The two Direct3D 12 surfaces (the
///         mesh viewer and the Shadowkey presenter) observe no ancestor visibility, so they are told here.
///     </para>
/// </summary>
public sealed partial class AssetBrowserTab
{
    private static readonly TimeSpan LayoutSaveDelay = TimeSpan.FromMilliseconds(500);

    private PanelWidthClamp? _paneClamp;
    private WorkflowSettingsStore? _layoutStore;
    private DispatcherQueueTimer? _layoutSaveTimer;
    private AssetPaneLayout _paneLayout = AssetPaneLayout.Default;
    private AssetPaneLayout? _savedPaneLayout;
    private Task _layoutSaveWork = Task.CompletedTask;
    private bool _paneLayoutRestored;
    private bool _applyingPaneLayout;
    private bool _previewCollapsed;

    /// <summary>Connects both side panels, the clamp and the save timer, then restores the persisted layout.</summary>
    /// <param name="localization">The window's localization owner, which names the panels and their commands.</param>
    private void InitializePaneLayout(LocalizationController localization)
    {
        ArgumentNullException.ThrowIfNull(localization);
        TreePanel.Initialize(TreeColumn, TreeSplitter, localization, "Assets_TreePanel", "Assets.TreePanel",
            collapseToRight: false, stripWidth: AssetPaneLayout.StripWidth);
        PreviewPanel.Initialize(PreviewColumn, PreviewSplitter, localization, "Assets_PreviewPanel", "Assets.PreviewPanel",
            collapseToRight: true, stripWidth: AssetPaneLayout.StripWidth);
        TreePanel.CollapsedChanged += TreePanel_CollapsedChanged;
        PreviewPanel.CollapsedChanged += PreviewPanel_CollapsedChanged;
        TreePanel.SizeChanged += Pane_SizeChanged;
        PreviewPanel.SizeChanged += Pane_SizeChanged;
        _paneClamp = new PanelWidthClamp(AssetColumns, TreeColumn, GalleryColumn, PreviewColumn);
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = LayoutSaveDelay;
        timer.IsRepeating = false;
        timer.Tick += LayoutSaveTimer_Tick;
        _layoutSaveTimer = timer;
        _ = InitializePaneLayoutAsync();
    }

    /// <summary>Reads the persisted layout off-thread once and applies widths before collapsed flags.</summary>
    /// <returns>Observed restoration; a failed read leaves the XAML defaults and logs.</returns>
    private async Task InitializePaneLayoutAsync()
    {
        try
        {
            var preferences = SettingsLocation.ForApplication("BethesdaMultitool");
            var store = new WorkflowSettingsStore(Path.Combine(Path.GetDirectoryName(preferences)!, "workflows.json"));
            _layoutStore = store;
            var layout = await Task.Run(() => AssetPaneLayout.Parse(store.Load()));
            if (_disposed) return;
            _savedPaneLayout = layout;
            ApplyPaneLayout(layout);
        }
        catch (Exception failure)
        {
            PaneLayoutFailed(failure);
        }
        finally
        {
            _paneLayoutRestored = true;
        }
    }

    /// <summary>Applies persisted widths to expanded columns, then the collapsed flags through the Shared panels.</summary>
    /// <param name="layout">The parsed, already clamped layout.</param>
    private void ApplyPaneLayout(AssetPaneLayout layout)
    {
        _applyingPaneLayout = true;
        try
        {
            _paneLayout = layout;
            if (!TreePanel.IsCollapsed) TreeColumn.Width = new GridLength(layout.TreeWidth);
            if (!PreviewPanel.IsCollapsed) PreviewColumn.Width = new GridLength(layout.PreviewWidth);
            TreePanel.SetCollapsed(layout.TreeCollapsed);
            PreviewPanel.SetCollapsed(layout.PreviewCollapsed);
        }
        finally { _applyingPaneLayout = false; }
    }

    /// <summary>Records the tree panel's collapsed flag; hiding the file list changes no presentation.</summary>
    /// <param name="sender">The tree panel.</param>
    /// <param name="args">The completed transition.</param>
    private void TreePanel_CollapsedChanged(object? sender, EventArgs args)
    {
        if (_disposed) return;
        _paneLayout = _paneLayout.WithTreeCollapsed(TreePanel.IsCollapsed);
        if (!_applyingPaneLayout) QueuePaneLayoutSave();
    }

    /// <summary>Records the preview panel's collapsed flag and pauses the two Direct3D 12 surfaces; the media hosts stop by themselves.</summary>
    /// <param name="sender">The preview panel.</param>
    /// <param name="args">The completed transition.</param>
    private void PreviewPanel_CollapsedChanged(object? sender, EventArgs args)
    {
        if (_disposed) return;
        _previewCollapsed = PreviewPanel.IsCollapsed;
        _meshViewer?.SetPresentationActive(!_previewCollapsed);
        _shadowkeyPresenter?.SetPresentationActive(_shadowkeyActive && !_previewCollapsed);
        _paneLayout = _paneLayout.WithPreviewCollapsed(PreviewPanel.IsCollapsed);
        if (!_applyingPaneLayout) QueuePaneLayoutSave();
    }

    /// <summary>Records an expanded panel's new width and restarts the debounced save.</summary>
    /// <param name="sender">The tree or preview panel.</param>
    /// <param name="args">The new size; a collapsed panel's strip width is never recorded.</param>
    /// <remarks>
    ///     A width the narrow-window clamp forced is not recorded either: the clamp only acts once the
    ///     gallery column sits at its minimum, so a gallery wider than that means the user dragged.
    /// </remarks>
    private void Pane_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (_disposed || _applyingPaneLayout || !_paneLayoutRestored) return;
        var width = args.NewSize.Width;
        if (!double.IsFinite(width) || width <= 0) return;
        if (GalleryColumn.ActualWidth <= AssetPaneLayout.GalleryMinWidth + 0.5) return;
        AssetPaneLayout next;
        if (ReferenceEquals(sender, TreePanel))
        {
            if (TreePanel.IsCollapsed) return;
            next = _paneLayout.WithTreeWidth(width);
        }
        else
        {
            if (PreviewPanel.IsCollapsed) return;
            next = _paneLayout.WithPreviewWidth(width);
        }

        if (next.Equals(_paneLayout)) return;
        _paneLayout = next;
        if (_layoutSaveTimer is { } timer)
        {
            timer.Stop();
            timer.Start();
        }
    }

    /// <summary>Flushes the debounced width change once the resize has settled.</summary>
    /// <param name="sender">The save timer.</param>
    /// <param name="args">The tick.</param>
    private void LayoutSaveTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        QueuePaneLayoutSave();
    }

    /// <summary>Queues one serialized write of the current layout unless it already matches the stored one.</summary>
    private void QueuePaneLayoutSave()
    {
        if (_disposed) return;
        QueuePaneLayoutSaveCore();
    }

    /// <summary>Queues the write without the disposal gate, so retirement can flush a pending change.</summary>
    private void QueuePaneLayoutSaveCore()
    {
        if (_layoutStore is not { } store) return;
        var layout = _paneLayout;
        if (layout.Equals(_savedPaneLayout)) return;
        var previous = _layoutSaveWork;
        _layoutSaveWork = SavePaneLayoutAsync(previous, store, layout);
    }

    /// <summary>Writes every layout setting off-thread after the preceding write has settled.</summary>
    /// <param name="previous">The preceding write; its failure was already logged.</param>
    /// <param name="store">The workflow store.</param>
    /// <param name="layout">The layout captured when the write was queued.</param>
    /// <returns>Completion of this write; failures are logged, never thrown.</returns>
    private async Task SavePaneLayoutAsync(Task previous, WorkflowSettingsStore store, AssetPaneLayout layout)
    {
        try { await previous; }
        catch (Exception)
        {
            // Reported by the write that failed.
        }

        try
        {
            await Task.Run(() =>
            {
                foreach (var (name, value) in layout.ToSettings()) store.Set(name, value);
            });
            _savedPaneLayout = layout;
        }
        catch (Exception failure)
        {
            PaneLayoutFailed(failure);
        }
    }

    /// <summary>Logs a layout read or write failure the same way a gallery setting failure is logged.</summary>
    /// <param name="failure">The persistence failure.</param>
    private static void PaneLayoutFailed(Exception failure) =>
        Logger.Instance.Warn("[AssetBrowser] Pane layout setting failed: {0}", failure.Message);

    /// <summary>Ctrl+Shift+T while focus is inside the pane: shows or hides the asset tree panel.</summary>
    /// <param name="sender">The accelerator.</param>
    /// <param name="args">The invocation, marked handled.</param>
    private void ToggleTreePane_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_disposed) return;
        TreePanel.SetCollapsed(!TreePanel.IsCollapsed);
        args.Handled = true;
    }

    /// <summary>Ctrl+Shift+P while focus is inside the pane: shows or hides the preview panel.</summary>
    /// <param name="sender">The accelerator.</param>
    /// <param name="args">The invocation, marked handled.</param>
    private void TogglePreviewPane_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_disposed) return;
        PreviewPanel.SetCollapsed(!PreviewPanel.IsCollapsed);
        args.Handled = true;
    }

    /// <summary>Stops the timer, flushes a pending write, awaits it, then retires the clamp and both panels.</summary>
    /// <returns>Completion after every stage has been attempted.</returns>
    /// <exception cref="AggregateException">One or more stages failed.</exception>
    private async Task DisposePaneLayoutAsync()
    {
        List<Exception> failures = [];
        TreePanel.CollapsedChanged -= TreePanel_CollapsedChanged;
        PreviewPanel.CollapsedChanged -= PreviewPanel_CollapsedChanged;
        TreePanel.SizeChanged -= Pane_SizeChanged;
        PreviewPanel.SizeChanged -= Pane_SizeChanged;
        if (_layoutSaveTimer is { } timer)
        {
            var pending = timer.IsRunning;
            try
            {
                timer.Stop();
                timer.Tick -= LayoutSaveTimer_Tick;
            }
            catch (Exception failure) { failures.Add(failure); }
            if (pending)
            {
                try { QueuePaneLayoutSaveCore(); }
                catch (Exception failure) { failures.Add(failure); }
            }
        }

        try { await _layoutSaveWork; }
        catch (Exception failure) { failures.Add(failure); }
        try { _paneClamp?.Dispose(); _paneClamp = null; }
        catch (Exception failure) { failures.Add(failure); }
        try { TreePanel.Dispose(); }
        catch (Exception failure) { failures.Add(failure); }
        try { PreviewPanel.Dispose(); }
        catch (Exception failure) { failures.Add(failure); }
        if (failures.Count != 0) throw new AggregateException("Asset browser pane layout retirement failed.", failures);
    }
}
