using BethesdaMultitool.CLI.Rendering.Nif;
using BethesdaMultitool.CLI;
using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.Storage.Pickers;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Character;
using BethesdaMultitool.Core.Ui;
using WinRT.Interop;

namespace BethesdaMultitool;

/// <summary>
///     NPC Browser tab: 3D viewer, render/export controls, batch operations.
/// </summary>
public sealed partial class SingleFileTab
{
    private readonly NpcBrowserController _npcBrowser = new();
    private CancellationTokenSource? _npcBatchCts;
    private NpcBrowserService? _npcBrowserService;
    private BethesdaViewerScene? _npcViewerScene;
    private CancellationTokenSource? _npcViewerLoadCts;
    private Task? _npcViewerLoadTask;
    private int _npcViewerLoadGeneration;
    private bool _npcViewerNativeReady;
    private TaskCompletionSource<BethesdaSceneViewerRenderState>? _npcViewerNativeOutcome;
    private BethesdaViewerScene? _npcViewerNativeOutcomeScene;
    private int _npcViewerNativeOutcomeGeneration;
    private bool _npcViewerDisposed;
    private CancellationTokenSource? _npcRenderOptionDebounce;
    private bool _webViewInitialized;
    private Task? _webViewInitializationTask;

    #region Cross-Tab Navigation

    private async void ViewNpc_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedBrowserNode?.DataObject is not NpcRecord npc)
        {
            return;
        }

        // Switch to NPC Browser tab
        TrySelectSubTab(AnalysisSubTab.Actors);

        // Ensure the NPC browser is populated
        if (!_session.NpcBrowserPopulated)
        {
            await _tasks.RunExclusiveAsync("populate-npcs", PopulateNpcBrowserAsync);
        }

        // Select the NPC in the list
        if (_npcBrowser.FilteredList.Count > 0)
        {
            var match = _npcBrowser.FindVisible(npc.FormId);
            if (match == null && _npcBrowser.FullList.Count > 0)
            {
                // NPC may be filtered out — clear filters and refresh
                NpcNamedOnlyCheckBox.IsChecked = false;
                NpcSearchBox.Text = "";
                RefreshNpcList();
                match = _npcBrowser.FindVisible(npc.FormId);
            }

            if (match != null)
            {
                NpcListView.SelectedItem = match;
                // Defer the scroll past layout: calling ScrollIntoView synchronously right after an
                // ItemsSource change forces a full synchronous measure of every intervening item (the
                // multi-second freeze). Running it on the dispatcher lets it scroll the realized viewport.
                var target = match;
                DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => NpcListView.ScrollIntoView(target));
            }
        }
    }

    #endregion

    #region Initialization

    private async Task PopulateNpcBrowserAsync(CancellationToken cancellationToken)
    {
        if (_session.NpcBrowserPopulated)
        {
            return;
        }

        var isDmp = _session.FileType == AnalysisFileType.Minidump;

        if (!isDmp && (!_session.HasEsmRecords || _session.FilePath == null))
        {
            NpcBrowserStatusText.Text = "Run analysis on an ESM to browse NPCs";
            return;
        }

        if (isDmp && _session.FilePath == null)
        {
            return;
        }

        // DMP files always need a game Data directory (contains both ESM and BSAs)
        if (isDmp && _session.NpcBsaDirectory == null)
        {
            NpcBrowserStatusText.Text =
                "Configure game Data directory (with ESM + BSA files) to browse NPCs from memory dump";
            NpcBsaPathPanel.Visibility = Visibility.Visible;
            return;
        }

        NpcBrowserProgressBar.Visibility = Visibility.Visible;
        NpcBrowserStatusText.Text = "Detecting archives...";

        try
        {
            var esmPath = _session.FilePath!;

            var bsaPaths = await NpcBrowserWorkflowService.DiscoverBsaPathsAsync(
                esmPath,
                _session.NpcBsaDirectory);
            cancellationToken.ThrowIfCancellationRequested();

            if (!bsaPaths.HasMeshes)
            {
                NpcBrowserProgressBar.Visibility = Visibility.Collapsed;
                NpcBrowserStatusText.Text = isDmp
                    ? "No meshes BSA found in configured directory. Point to a game Data directory."
                    : "No meshes BSA found alongside ESM. Configure BSA paths to browse NPCs.";
                NpcBsaPathPanel.Visibility = Visibility.Visible;
                return;
            }

            NpcBrowserService? service;

            if (isDmp)
            {
                service = await PopulateFromDmpAsync(bsaPaths);
            }
            else
            {
                NpcBrowserStatusText.Text = "Scanning NPC records...";

                var bigEndian = _session.AnalysisResult?.EsmRecords?.BigEndianRecords > 0;
                service = await NpcBrowserWorkflowService.CreateFromEsmAsync(esmPath, bigEndian, bsaPaths);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (service == null)
            {
                NpcBrowserProgressBar.Visibility = Visibility.Collapsed;
                NpcBrowserStatusText.Text = "Failed to initialize NPC browser.";
                return;
            }

            _npcBrowserService = service;
            _session.NpcBrowserPopulated = true;

            ApplyNpcListState(_npcBrowser.LoadList(
                service.GetNpcList(),
                NpcNamedOnlyCheckBox.IsChecked == true,
                NpcSearchBox.Text,
                NpcShowEditorIdCheckBox.IsChecked == true));

            NpcBrowserPlaceholder.Visibility = Visibility.Collapsed;
            NpcBrowserContent.Visibility = Visibility.Visible;
            // Keep the compatibility host cold until a selected actor fails native D3D12 setup.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            NpcBrowserProgressBar.Visibility = Visibility.Collapsed;
            NpcBrowserStatusText.Text = $"Error: {ex.Message}";
        }
    }

    private async Task<NpcBrowserService?> PopulateFromDmpAsync(BsaDiscoveryResult bsaPaths)
    {
        // Find ESM file in the configured game Data directory
        NpcBrowserStatusText.Text = "Locating ESM file...";
        var dataDir = _session.NpcBsaDirectory!;
        var esmFile = NpcBrowserWorkflowService.DiscoverEsmFile(dataDir);

        if (esmFile == null)
        {
            NpcBrowserProgressBar.Visibility = Visibility.Collapsed;
            NpcBrowserStatusText.Text = "No ESM file found in configured directory.";
            NpcBsaPathPanel.Visibility = Visibility.Visible;
            return null;
        }

        var scanResult = _session.AnalysisResult?.EsmRecords;
        var minidumpInfo = _session.AnalysisResult?.MinidumpInfo;
        if (scanResult == null || minidumpInfo == null || _session.Accessor == null)
        {
            NpcBrowserProgressBar.Visibility = Visibility.Collapsed;
            NpcBrowserStatusText.Text = "DMP analysis data not available. Run analysis first.";
            return null;
        }

        NpcBrowserStatusText.Text = "Reading ESM and resolving NPC appearances from memory dump...";

        return await NpcBrowserWorkflowService.CreateFromDmpAsync(
            dataDir,
            _session.Accessor,
            _session.FileSize,
            minidumpInfo,
            scanResult,
            bsaPaths);
    }

    private async Task InitializeWebViewAsync()
    {
        if (_npcViewerNativeReady || _webViewInitialized)
        {
            return;
        }

        if (_webViewInitializationTask is { } pending)
        {
            await pending;
            return;
        }

        var initialization = InitializeWebViewCoreAsync();
        _webViewInitializationTask = initialization;
        try
        {
            await initialization;
        }
        finally
        {
            if (ReferenceEquals(_webViewInitializationTask, initialization))
            {
                _webViewInitializationTask = null;
            }
        }
    }

    private async Task InitializeWebViewCoreAsync()
    {
        try
        {
            await NpcModelViewer.EnsureCoreWebView2Async();

            // Chromium startup is not cancellable. Native Ready or tab disposal can win while
            // this await is in flight, so establish ownership first and immediately tear the
            // fallback back down instead of making it visible after the native promotion.
            _webViewInitialized = true;
            if (_npcViewerNativeReady || _npcViewerDisposed)
            {
                CloseNpcViewerCompatibilityHost();
                return;
            }

            // Serve local assets via virtual host mapping
            var assetsDir = Path.Combine(AppContext.BaseDirectory, "App", "Assets");
            NpcModelViewer.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "npc-viewer-assets",
                assetsDir,
                CoreWebView2HostResourceAccessKind.Allow);

            NpcModelViewer.CoreWebView2.Navigate(
#pragma warning disable S1075 // URIs should not be hardcoded
                "https://npc-viewer-assets/npc-viewer.html"
#pragma warning restore S1075
            );

            if (_npcViewerNativeReady || _npcViewerDisposed)
            {
                CloseNpcViewerCompatibilityHost();
                return;
            }

            NpcModelViewer.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            if (_npcViewerDisposed) return;

            CloseNpcViewerCompatibilityHost();
            NpcBrowserStatusText.Text = $"WebView2 init failed: {ex.Message}";
            NpcNativeViewerWarning.Message =
                $"Compatibility preview failed to initialize; the native renderer will continue preparing. {ex.Message}";
            NpcNativeViewerWarning.IsOpen = true;
            // Compatibility failure must not hide the native viewer or the rest of the browser.
            // The native control reports its own preparing/faulted state in-place.
            NpcBrowserContent.Visibility = Visibility.Visible;
        }
    }

    #endregion

    #region NPC List

    private void RefreshNpcList()
    {
        if (_npcBrowser.FullList.Count == 0)
        {
            return;
        }

        ApplyNpcListState(_npcBrowser.Refresh(
            NpcNamedOnlyCheckBox.IsChecked == true,
            NpcSearchBox.Text,
            NpcShowEditorIdCheckBox.IsChecked == true));
    }

    private void ApplyNpcListState(NpcListState state)
    {
        NpcListView.ItemsSource = state.Items;
        if (state.RestoredSelection != null)
        {
            NpcListView.SelectedItem = state.RestoredSelection;
        }

        NpcCountText.Text = state.CountText;
    }

    private void NpcSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshNpcList();
    }

    private void NpcNamedOnlyCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        RefreshNpcList();
    }

    private void NpcShowEditorIdCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        RefreshNpcList();
    }

    private async void NpcListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CancelNpcRenderOptionDebounce();

        if (NpcListView.SelectedItem is not NpcListItem npc || _npcBrowserService == null)
        {
            var emptyGeneration = unchecked(++_npcViewerLoadGeneration);
            await CancelNpcViewerLoadAndDrainAsync();
            if (emptyGeneration != _npcViewerLoadGeneration ||
                NpcListView.SelectedItem is NpcListItem)
            {
                return;
            }

            _npcViewerScene = null;
            NpcSceneViewer.ClearScene();
            ApplyNpcSelectionState(NpcSelectionState.Empty);
            return;
        }

        ApplyNpcSelectionState(_npcBrowser.Select(npc));

        var load = LoadNpcIntoViewerAsync(npc);
        _npcViewerLoadTask = load;
        await load;
    }

    private void ApplyNpcSelectionState(NpcSelectionState state)
    {
        NpcDetailName.Text = state.Name;
        NpcDetailInfo.Text = state.DetailText;
        NpcFullBodyCheckBox.IsEnabled = state.CanToggleHumanoidOptions;
        NpcArmorCheckBox.IsEnabled = state.CanToggleHumanoidOptions;
        NpcWeaponCheckBox.IsEnabled = state.CanToggleHumanoidOptions;
        NpcIdlePoseCheckBox.IsEnabled = state.CanToggleHumanoidOptions;
        NpcExportGlbButton.IsEnabled = state.CanExportGlb;
        NpcRenderPngButton.IsEnabled = state.CanRenderPng;
        NpcCaptureNativePngButton.IsEnabled =
            state.CanRenderPng &&
            _npcViewerScene is not null &&
            NpcSceneViewer.RenderState == BethesdaSceneViewerRenderState.Ready;
    }

    #endregion

    #region 3D Viewer

    private async Task LoadNpcIntoViewerAsync(NpcListItem npc)
    {
        await CancelNpcViewerLoadAndDrainAsync();
        var service = _npcBrowserService;
        if (service == null) return;

        var options = BuildNpcRenderOptions();
        var loadCts = new CancellationTokenSource();
        _npcViewerLoadCts = loadCts;
        var cancellationToken = loadCts.Token;
        var generation = unchecked(++_npcViewerLoadGeneration);
        TaskCompletionSource<BethesdaSceneViewerRenderState>? nativeOutcome = null;

        NpcModelLoadingRing.Visibility = Visibility.Visible;

        try
        {
            var scene = await NpcBrowserWorkflowService.BuildViewerSceneAsync(
                service,
                npc,
                options,
                cancellationToken);
            if (!IsCurrentNpcViewerLoad(service, npc, options, generation, cancellationToken))
            {
                return;
            }

            if (scene == null)
            {
                var label = npc.IsCreature ? "creature" : "NPC";
                _npcViewerScene = null;
                NpcSceneViewer.ClearScene();
                await SetNpcViewerFallbackStatusAsync($"No geometry for this {label}");
                return;
            }

            if (!_npcViewerNativeReady)
            {
                nativeOutcome = new TaskCompletionSource<BethesdaSceneViewerRenderState>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _npcViewerNativeOutcome = nativeOutcome;
                _npcViewerNativeOutcomeScene = scene;
                _npcViewerNativeOutcomeGeneration = generation;
            }

            _npcViewerScene = scene;
            NpcSceneViewer.SetScene(scene);
            NpcSceneViewer.FrameScene();
            NpcSceneViewer.InvalidateViewport();

            // Let CompositionTarget produce one real native frame before deciding whether a
            // compatibility process is needed. The fallback serializes this exact assembled scene;
            // it never recomposes the actor.
            if (nativeOutcome is not null)
            {
                if (NpcSceneViewer.RenderState == BethesdaSceneViewerRenderState.Faulted)
                {
                    nativeOutcome.TrySetResult(BethesdaSceneViewerRenderState.Faulted);
                }

                var nativeState = await nativeOutcome.Task.WaitAsync(cancellationToken);
                if (!IsCurrentNpcViewerLoad(service, npc, options, generation, cancellationToken))
                {
                    return;
                }

                if (nativeState == BethesdaSceneViewerRenderState.Faulted && !_npcViewerNativeReady)
                {
                    await InitializeWebViewAsync();
                    if (!IsCurrentNpcViewerLoad(service, npc, options, generation, cancellationToken) ||
                        !ReferenceEquals(scene, _npcViewerScene))
                    {
                        return;
                    }

                    if (!_npcViewerNativeReady && _webViewInitialized)
                    {
                        try
                        {
                            await NpcModelViewer.ExecuteScriptAsync("setStatus('Building compatibility model...')");
                            var glbBytes = await Task.Run(
                                () => service.ExportViewerSceneToGlb(scene),
                                cancellationToken);
                            if (!IsCurrentNpcViewerLoad(service, npc, options, generation, cancellationToken) ||
                                !ReferenceEquals(scene, _npcViewerScene) ||
                                _npcViewerNativeReady)
                            {
                                return;
                            }

                            var base64 = Convert.ToBase64String(glbBytes);
                            await NpcModelViewer.ExecuteScriptAsync($"loadModel('{base64}')");
                        }
                        catch (Exception ex) when (_npcViewerNativeReady)
                        {
                            // Promotion closes the compatibility host. A racing script completion must not
                            // clear the already-valid native scene through the outer failure path.
                            Logger.Instance.Warn(
                                "NPC Viewer: compatibility load ended during native promotion: {0}",
                                ex.Message);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A newer actor/options state owns the visible preview.
        }
        catch (Exception ex)
        {
            if (generation == _npcViewerLoadGeneration && ReferenceEquals(service, _npcBrowserService))
            {
                if (!_npcViewerNativeReady)
                {
                    _npcViewerScene = null;
                    NpcSceneViewer.ClearScene();
                    await SetNpcViewerFallbackStatusAsync($"Error: {ex.Message}");
                }
                else
                {
                    Logger.Instance.Warn("NPC Viewer: post-promotion UI update failed: {0}", ex.Message);
                }
            }
        }
        finally
        {
            if (ReferenceEquals(_npcViewerNativeOutcome, nativeOutcome))
            {
                _npcViewerNativeOutcome = null;
                _npcViewerNativeOutcomeScene = null;
            }

            if (ReferenceEquals(_npcViewerLoadCts, loadCts))
            {
                _npcViewerLoadCts = null;
                NpcModelLoadingRing.Visibility = Visibility.Collapsed;
            }

            loadCts.Dispose();
        }
    }

    private async void NpcRenderOption_Changed(object sender, RoutedEventArgs e)
    {
        if (NpcListView.SelectedItem is not NpcListItem npc || _npcBrowserService == null)
        {
            return;
        }

        // Debounce rapid toggling
        CancelNpcRenderOptionDebounce();
        var debounce = new CancellationTokenSource();
        _npcRenderOptionDebounce = debounce;
        var token = debounce.Token;

        try
        {
            // Publish the latest-wins token before awaiting the active viewer load. A second option
            // event or actor selection can now cancel this operation while the drain is in flight.
            await CancelNpcViewerLoadAndDrainAsync();
            token.ThrowIfCancellationRequested();
            await Task.Delay(300, token);
            if (!token.IsCancellationRequested &&
                NpcListView.SelectedItem is NpcListItem selectedNpc &&
                selectedNpc.FormId == npc.FormId &&
                selectedNpc.IsCreature == npc.IsCreature)
            {
                var load = LoadNpcIntoViewerAsync(selectedNpc);
                _npcViewerLoadTask = load;
                await load;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Expected on rapid toggling
        }
        finally
        {
            if (ReferenceEquals(_npcRenderOptionDebounce, debounce))
            {
                _npcRenderOptionDebounce = null;
            }

            debounce.Dispose();
        }
    }

    #endregion

    #region Export & Render

    private async void NpcExportGlb_Click(object sender, RoutedEventArgs e)
    {
        if (_npcBrowser.SelectedFormId == null || _npcBrowserService == null)
        {
            return;
        }

        var picker = new FileSavePicker();
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeChoices.Add("GLB File", [".glb"]);
        var npc = NpcListView.SelectedItem as NpcListItem;
        if (npc == null)
        {
            return;
        }

        picker.SuggestedFileName = NpcBrowserController.BuildDefaultFileName(npc, ".glb");
        InitializeWithWindow.Initialize(picker, NpcGetWindowHandle());

        var file = await picker.PickSaveFileAsync();
        if (file == null)
        {
            return;
        }

        NpcExportGlbButton.IsEnabled = false;
        try
        {
            var glbBytes = await NpcBrowserWorkflowService.BuildGlbAsync(
                _npcBrowserService,
                npc,
                BuildNpcRenderOptions());

            if (glbBytes != null)
            {
                await File.WriteAllBytesAsync(file.Path, glbBytes);
                StatusTextBlock.Text = $"Exported: {file.Name}";
            }
            else
            {
                StatusTextBlock.Text = $"No geometry for this {(npc.IsCreature ? "creature" : "NPC")}";
            }
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Export failed: {ex.Message}";
        }
        finally
        {
            NpcExportGlbButton.IsEnabled = true;
        }
    }

    private async void NpcRenderPng_Click(object sender, RoutedEventArgs e)
    {
        if (_npcBrowser.SelectedFormId == null || _npcBrowserService == null)
        {
            return;
        }

        var picker = new FileSavePicker();
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeChoices.Add("PNG Image", [".png"]);
        var npc = NpcListView.SelectedItem as NpcListItem;
        picker.SuggestedFileName = NpcBrowserController.BuildDefaultFileName(npc, ".png");
        InitializeWithWindow.Initialize(picker, NpcGetWindowHandle());

        var file = await picker.PickSaveFileAsync();
        if (file == null)
        {
            return;
        }

        NpcRenderPngButton.IsEnabled = false;
        try
        {
            var options = BuildNpcRenderOptions();
            var spriteSize = GetSelectedSpriteSize();
            var camera = BuildCameraConfig();
            var viewCount = await NpcBrowserWorkflowService.RenderPngViewsAsync(
                _npcBrowserService,
                _npcBrowser.SelectedFormId.Value,
                file.Path,
                options,
                spriteSize,
                camera);

            StatusTextBlock.Text = NpcBrowserController.FormatRenderStatus(viewCount, file.Name);
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Render failed: {ex.Message}";
        }
        finally
        {
            NpcRenderPngButton.IsEnabled = true;
        }
    }

    #endregion

    #region Batch Operations

    private async void NpcBatchExportGlb_Click(object sender, RoutedEventArgs e)
    {
        var outputDir = await PickOutputFolderAsync();
        if (outputDir == null || _npcBrowserService == null)
        {
            return;
        }

        var selectedIds = _npcBrowser.GetSelectedVisibleFormIds();
        await RunBatchOperationAsync("Exporting GLBs", async (progress, ct) =>
        {
            var options = BuildNpcRenderOptions();

            await _npcBrowserService.BatchExportGlbAsync(
                outputDir, options.HeadOnly, options.NoEquip, options.NoWeapon, progress, ct, selectedIds);
        });
    }

    private async void NpcBatchRenderPng_Click(object sender, RoutedEventArgs e)
    {
        var outputDir = await PickOutputFolderAsync();
        if (outputDir == null || _npcBrowserService == null)
        {
            return;
        }

        var selectedIds = _npcBrowser.GetSelectedVisibleFormIds();
        await RunBatchOperationAsync("Rendering PNGs", async (progress, ct) =>
        {
            var options = BuildNpcRenderOptions();
            var spriteSize = GetSelectedSpriteSize();
            var camera = BuildCameraConfig();

            await _npcBrowserService.BatchRenderPngAsync(
                outputDir,
                options.HeadOnly,
                options.NoEquip,
                options.NoWeapon,
                spriteSize,
                camera,
                progress,
                ct,
                selectedIds);
        });
    }

    private async Task RunBatchOperationAsync(
        string operationName,
        Func<IProgress<(int Done, int Total, string Name)>, CancellationToken, Task> work)
    {
        SetNpcBatchButtonsEnabled(false);
        NpcBatchProgressBar.Visibility = Visibility.Visible;
        NpcBatchProgressBar.Value = 0;
        NpcBatchStatusText.Text = $"{operationName}...";

        _npcBatchCts = new CancellationTokenSource();
        var progress = new Progress<(int Done, int Total, string Name)>(p =>
        {
            NpcBatchProgressBar.Maximum = p.Total;
            NpcBatchProgressBar.Value = p.Done;
            NpcBatchStatusText.Text = NpcBrowserController.FormatBatchProgress(
                operationName,
                p.Done,
                p.Total,
                p.Name);
        });

        try
        {
            await work(progress, _npcBatchCts.Token);
            NpcBatchStatusText.Text = NpcBrowserController.FormatBatchCompleted(operationName);
        }
        catch (OperationCanceledException)
        {
            NpcBatchStatusText.Text = NpcBrowserController.FormatBatchCancelled(operationName);
        }
        catch (Exception ex)
        {
            NpcBatchStatusText.Text = NpcBrowserController.FormatBatchFailed(operationName, ex);
        }
        finally
        {
            NpcBatchProgressBar.Visibility = Visibility.Collapsed;
            SetNpcBatchButtonsEnabled(true);
            _npcBatchCts?.Dispose();
            _npcBatchCts = null;
        }
    }

    private void SetNpcBatchButtonsEnabled(bool enabled)
    {
        NpcBatchExportGlbButton.IsEnabled = enabled;
        NpcBatchRenderPngButton.IsEnabled = enabled;
    }

    #endregion

    #region Selection

    private void NpcSelectAll_Click(object sender, RoutedEventArgs e)
    {
        SetAllNpcSelected(true);
    }

    private void NpcDeselectAll_Click(object sender, RoutedEventArgs e)
    {
        SetAllNpcSelected(false);
    }

    private void NpcItemCheckBox_Click(object sender, RoutedEventArgs e)
    {
        UpdateNpcSelectionCountText();
    }

    private void SetAllNpcSelected(bool selected)
    {
        if (_npcBrowser.FilteredList.Count == 0)
        {
            return;
        }

        _npcBrowser.SetAllVisibleSelected(selected);
        UpdateNpcSelectionCountText();
    }

    private void UpdateNpcSelectionCountText()
    {
        if (_npcBrowser.FilteredList.Count == 0 && _npcBrowser.FullList.Count == 0)
        {
            return;
        }

        NpcCountText.Text = _npcBrowser.BuildSelectionCountText();
    }

    #endregion

    #region BSA Configuration

    private async void NpcBrowserConfigureBsa_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, NpcGetWindowHandle());

        var folder = await picker.PickSingleFolderAsync();
        if (folder == null)
        {
            return;
        }

        NpcBsaPathTextBox.Text = folder.Path;
    }

    private void NpcBsaPathTextBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            NpcBsaLoadButton_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private async void NpcBsaLoadButton_Click(object sender, RoutedEventArgs e)
    {
        var bsaDir = NpcBsaPathTextBox.Text?.Trim();
        if (string.IsNullOrEmpty(bsaDir) || !Directory.Exists(bsaDir))
        {
            NpcBrowserStatusText.Text = "Directory does not exist.";
            return;
        }

        var esmPath = _session.FilePath;
        if (esmPath == null)
        {
            return;
        }

        var pseudoEsmPath = Path.Combine(bsaDir, Path.GetFileName(esmPath));
        var bsaPaths = BsaDiscovery.Discover(pseudoEsmPath);

        if (!bsaPaths.HasMeshes)
        {
            NpcBrowserStatusText.Text = "No meshes archive found in selected directory.";
            return;
        }

        _session.NpcBsaDirectory = bsaDir;
        NpcBsaPathPanel.Visibility = Visibility.Collapsed;
        _session.NpcBrowserPopulated = false;
        await _tasks.RunExclusiveAsync("populate-npcs", PopulateNpcBrowserAsync);
    }

    #endregion

    #region Helpers

    private void NpcElevationSlider_ValueChanged(object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (NpcElevationLabel != null)
        {
            NpcElevationLabel.Text = $"{(int)e.NewValue}";
        }
    }

    private int GetSelectedSpriteSize()
    {
        return NpcBrowserController.ClampSpriteSize(NpcSizeNumberBox.Value);
    }

    private NpcRenderOptions BuildNpcRenderOptions()
    {
        return NpcBrowserController.BuildRenderOptions(
            NpcFullBodyCheckBox.IsChecked == true,
            NpcArmorCheckBox.IsChecked == true,
            NpcWeaponCheckBox.IsChecked == true,
            NpcIdlePoseCheckBox.IsChecked == true);
    }

    private CameraConfig BuildCameraConfig()
    {
        var perspective = "front";
        if (NpcPerspectiveComboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            perspective = tag;
        }

        return NpcBrowserController.BuildCameraConfig(perspective, NpcElevationSlider.Value);
    }

    private static async Task<string?> PickOutputFolderAsync()
    {
        var picker = new FolderPicker();
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, NpcGetWindowHandle());

        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    private static nint NpcGetWindowHandle()
    {
        return WindowNative.GetWindowHandle(FalloutApp.Current.MainWindow);
    }

    private bool IsCurrentNpcViewerLoad(
        NpcBrowserService service,
        NpcListItem npc,
        NpcRenderOptions options,
        int generation,
        CancellationToken cancellationToken)
    {
        return !cancellationToken.IsCancellationRequested &&
               generation == _npcViewerLoadGeneration &&
               ReferenceEquals(service, _npcBrowserService) &&
               NpcListView.SelectedItem is NpcListItem selected &&
               selected.FormId == npc.FormId &&
               selected.IsCreature == npc.IsCreature &&
               Equals(options, BuildNpcRenderOptions());
    }

    private async Task SetNpcViewerFallbackStatusAsync(string message)
    {
        if (_npcViewerNativeReady || !_webViewInitialized) return;

        try
        {
            await NpcModelViewer.ExecuteScriptAsync($"setStatus('{EscapeJsString(message)}')");
        }
        catch
        {
            NpcBrowserStatusText.Text = message;
        }
    }

    private async void NpcCaptureNativePng_Click(object sender, RoutedEventArgs e)
    {
        if (_npcViewerScene is null ||
            NpcSceneViewer.RenderState != BethesdaSceneViewerRenderState.Ready)
        {
            return;
        }

        var npc = NpcListView.SelectedItem as NpcListItem;
        var outputPath = EnvironmentVariables.Get(EnvironmentVariables.Viewer.NativeCaptureOutput);
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            var picker = new FileSavePicker();
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeChoices.Add("PNG Image", [".png"]);
            picker.SuggestedFileName = Path.ChangeExtension(
                NpcBrowserController.BuildDefaultFileName(npc, ".png"),
                ".native.png");
            InitializeWithWindow.Initialize(picker, NpcGetWindowHandle());

            var file = await picker.PickSaveFileAsync();
            if (file == null) return;
            outputPath = file.Path;
        }
        else
        {
            outputPath = Path.GetFullPath(outputPath);
            var outputDirectory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }
        }

        NpcCaptureNativePngButton.IsEnabled = false;
        try
        {
            var pngBytes = await NpcSceneViewer.CapturePngAsync();
            await File.WriteAllBytesAsync(outputPath, pngBytes);
            StatusTextBlock.Text = $"Captured native viewport: {Path.GetFileName(outputPath)}";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Native capture failed: {ex.Message}";
        }
        finally
        {
            NpcCaptureNativePngButton.IsEnabled =
                _npcViewerScene is not null &&
                NpcSceneViewer.RenderState == BethesdaSceneViewerRenderState.Ready;
        }
    }

    private async Task CancelNpcViewerLoadAndDrainAsync()
    {
        // Snapshot the previous operation before the first await. The selection/options handler
        // stores the newly returned task as soon as CancelAsync yields; reading the field afterward
        // would make this operation await itself forever.
        var load = _npcViewerLoadTask;
        var cancellation = _npcViewerLoadCts;
        if (cancellation is not null && !cancellation.IsCancellationRequested)
        {
            await cancellation.CancelAsync();
        }

        if (load is null || load.IsCompleted) return;

        try
        {
            await load;
        }
        catch (OperationCanceledException)
        {
            // Expected when selection, options, or source supersedes the load.
        }
    }

    private void CancelNpcRenderOptionDebounce()
    {
        var debounce = _npcRenderOptionDebounce;
        _npcRenderOptionDebounce = null;
        if (debounce is null) return;

        if (!debounce.IsCancellationRequested)
        {
            debounce.Cancel();
        }

        debounce.Dispose();
    }

    private void NpcSceneViewer_RenderStateChanged(
        object? sender,
        BethesdaSceneViewerRenderStateChangedEventArgs e)
    {
        CompleteNpcViewerNativeOutcome(e.State);
        NpcCaptureNativePngButton.IsEnabled =
            e.State == BethesdaSceneViewerRenderState.Ready && _npcViewerScene is not null;

        if (e.State == BethesdaSceneViewerRenderState.Faulted)
        {
            NpcNativeViewerWarning.Message = _npcViewerNativeReady
                ? $"The native renderer could not display this assembled actor. {e.Message}"
                : $"Opening the compatibility preview. {e.Message}";
            NpcNativeViewerWarning.IsOpen = true;
            return;
        }

        if (e.State != BethesdaSceneViewerRenderState.Ready || _npcViewerNativeReady) return;

        _npcViewerNativeReady = true;
        NpcNativeViewerWarning.IsOpen = false;
        NpcModelViewer.Visibility = Visibility.Collapsed;
        CloseNpcViewerCompatibilityHost();
        if (_npcViewerScene is not null)
        {
            NpcSceneViewer.SetScene(_npcViewerScene);
            NpcSceneViewer.FrameScene();
            NpcSceneViewer.InvalidateViewport();
        }
    }

    private void CompleteNpcViewerNativeOutcome(BethesdaSceneViewerRenderState state)
    {
        if (state is not (BethesdaSceneViewerRenderState.Ready or BethesdaSceneViewerRenderState.Faulted) ||
            _npcViewerNativeOutcome is not { } outcome ||
            _npcViewerNativeOutcomeGeneration != _npcViewerLoadGeneration ||
            !ReferenceEquals(_npcViewerNativeOutcomeScene, _npcViewerScene))
        {
            return;
        }

        outcome.TrySetResult(state);
    }

    private void CloseNpcViewerCompatibilityHost()
    {
        if (!_webViewInitialized) return;

        try
        {
            // Close releases the Chromium process, decoded GLB, and WebView compositor resources;
            // merely hiding the compatibility control would keep all of them resident.
            NpcModelViewer.Close();
        }
        catch (Exception ex)
        {
            Logger.Instance.Warn("NPC Viewer: compatibility WebView close failed: {0}", ex.Message);
        }
        finally
        {
            _webViewInitialized = false;
            NpcModelViewer.Visibility = Visibility.Collapsed;
        }
    }

    private void DisposeNpcViewerResources()
    {
        if (_npcViewerDisposed) return;
        _npcViewerDisposed = true;

        unchecked { _npcViewerLoadGeneration++; }
        _npcViewerLoadCts?.Cancel();
        _npcViewerLoadCts = null;
        _npcRenderOptionDebounce?.Cancel();
        _npcRenderOptionDebounce?.Dispose();
        _npcRenderOptionDebounce = null;

        NpcSceneViewer.RenderStateChanged -= NpcSceneViewer_RenderStateChanged;
        NpcSceneViewer.ClearScene();
        NpcSceneViewer.Dispose();
        CloseNpcViewerCompatibilityHost();
        _npcViewerScene = null;

        var service = _npcBrowserService;
        _npcBrowserService = null;
        var load = _npcViewerLoadTask;
        if (service is not null && load is { IsCompleted: false })
        {
            _ = load.ContinueWith(
                static (_, state) => ((NpcBrowserService)state!).Dispose(),
                service,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        else
        {
            service?.Dispose();
        }
    }

    private void ResetNpcBrowser()
    {
        _npcBatchCts?.Cancel();
        _npcBatchCts?.Dispose();
        _npcBatchCts = null;

        _npcRenderOptionDebounce?.Cancel();
        _npcRenderOptionDebounce?.Dispose();
        _npcRenderOptionDebounce = null;

        unchecked { _npcViewerLoadGeneration++; }
        _npcViewerLoadCts?.Cancel();
        _npcViewerLoadCts = null;
        _npcViewerScene = null;
        NpcSceneViewer.ClearScene();

        var service = _npcBrowserService;
        _npcBrowserService = null;
        var load = _npcViewerLoadTask;
        if (service is not null && load is { IsCompleted: false })
        {
            _ = load.ContinueWith(
                static (_, state) => ((NpcBrowserService)state!).Dispose(),
                service,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        else
        {
            service?.Dispose();
        }

        _npcBrowser.Reset();

        if (_webViewInitialized)
        {
            _ = NpcModelViewer.ExecuteScriptAsync("clearModel()");
        }

        NpcBrowserPlaceholder.Visibility = Visibility.Visible;
        NpcBrowserContent.Visibility = Visibility.Collapsed;
        NpcBrowserProgressBar.Visibility = Visibility.Collapsed;
        NpcBsaPathPanel.Visibility = Visibility.Collapsed;
        NpcBrowserStatusText.Text = "Run analysis on an ESM to browse NPCs";
        NpcNativeViewerWarning.IsOpen = false;
        NpcBatchProgressBar.Visibility = Visibility.Collapsed;
        NpcBatchStatusText.Text = "";
    }

    private static string EscapeJsString(string s)
    {
        return s.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\n", "\\n").Replace("\r", "");
    }

    #endregion
}
