using FalloutXbox360Utils.CLI;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.Storage.Pickers;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using WinRT.Interop;

namespace FalloutXbox360Utils;

/// <summary>
///     NPC Browser tab: 3D viewer, render/export controls, batch operations.
/// </summary>
public sealed partial class SingleFileTab
{
    private CancellationTokenSource? _npcBatchCts;
    private NpcBrowserService? _npcBrowserService;
    private readonly NpcBrowserController _npcBrowser = new();
    private CancellationTokenSource? _npcRenderOptionDebounce;
    private bool _webViewInitialized;

    #region Cross-Tab Navigation

    private async void ViewNpc_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedBrowserNode?.DataObject is not NpcRecord npc)
        {
            return;
        }

        // Switch to NPC Browser tab
        SubTabView.SelectedItem = NpcBrowserTab;

        // Ensure the NPC browser is populated
        if (!_session.NpcBrowserPopulated)
        {
            await PopulateNpcBrowserAsync();
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
                NpcListView.ScrollIntoView(match);
            }
        }
    }

    #endregion

    #region Initialization

    private async Task PopulateNpcBrowserAsync()
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
        NpcBrowserStatusText.Text = "Detecting BSA files...";

        try
        {
            var esmPath = _session.FilePath!;

            var bsaPaths = await NpcBrowserWorkflowService.DiscoverBsaPathsAsync(
                esmPath,
                _session.NpcBsaDirectory);

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

            await InitializeWebViewAsync();
        }
        catch (Exception ex)
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
        if (_webViewInitialized)
        {
            return;
        }

        try
        {
            await NpcModelViewer.EnsureCoreWebView2Async();

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
            _webViewInitialized = true;
        }
        catch (Exception ex)
        {
            NpcBrowserStatusText.Text = $"WebView2 init failed: {ex.Message}";
            NpcBrowserPlaceholder.Visibility = Visibility.Visible;
            NpcBrowserContent.Visibility = Visibility.Collapsed;
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
        if (NpcListView.SelectedItem is not NpcListItem npc || _npcBrowserService == null)
        {
            ApplyNpcSelectionState(NpcSelectionState.Empty);
            return;
        }

        ApplyNpcSelectionState(_npcBrowser.Select(npc));

        await LoadNpcIntoViewerAsync(npc);
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
    }

    #endregion

    #region 3D Viewer

    private async Task LoadNpcIntoViewerAsync(NpcListItem npc)
    {
        if (_npcBrowserService == null || !_webViewInitialized)
        {
            return;
        }

        NpcModelLoadingRing.Visibility = Visibility.Visible;

        try
        {
            await NpcModelViewer.ExecuteScriptAsync("setStatus('Building model...')");

            var glbBytes = await NpcBrowserWorkflowService.BuildGlbAsync(
                _npcBrowserService,
                npc,
                BuildNpcRenderOptions());

            if (glbBytes == null)
            {
                var label = npc.IsCreature ? "creature" : "NPC";
                await NpcModelViewer.ExecuteScriptAsync($"setStatus('No geometry for this {label}')");
                NpcModelLoadingRing.Visibility = Visibility.Collapsed;
                return;
            }

            var base64 = Convert.ToBase64String(glbBytes);
            await NpcModelViewer.ExecuteScriptAsync($"loadModel('{base64}')");
        }
        catch (Exception ex)
        {
            try
            {
                await NpcModelViewer.ExecuteScriptAsync(
                    $"setStatus('Error: {EscapeJsString(ex.Message)}')");
            }
            catch
            {
                // WebView may not be ready
            }
        }
        finally
        {
            NpcModelLoadingRing.Visibility = Visibility.Collapsed;
        }
    }

    private async void NpcRenderOption_Changed(object sender, RoutedEventArgs e)
    {
        if (NpcListView.SelectedItem is not NpcListItem npc || _npcBrowserService == null)
        {
            return;
        }

        // Debounce rapid toggling
        if (_npcRenderOptionDebounce != null)
        {
            await _npcRenderOptionDebounce.CancelAsync();
        }

        _npcRenderOptionDebounce = new CancellationTokenSource();
        var token = _npcRenderOptionDebounce.Token;

        try
        {
            await Task.Delay(300, token);
            if (!token.IsCancellationRequested)
            {
                await LoadNpcIntoViewerAsync(npc);
            }
        }
        catch (TaskCanceledException)
        {
            // Expected on rapid toggling
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
            NpcBrowserStatusText.Text = "No meshes BSA found in selected directory.";
            return;
        }

        _session.NpcBsaDirectory = bsaDir;
        NpcBsaPathPanel.Visibility = Visibility.Collapsed;
        _session.NpcBrowserPopulated = false;
        await PopulateNpcBrowserAsync();
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

    private void ResetNpcBrowser()
    {
        _npcBatchCts?.Cancel();
        _npcBatchCts?.Dispose();
        _npcBatchCts = null;

        _npcRenderOptionDebounce?.Cancel();
        _npcRenderOptionDebounce?.Dispose();
        _npcRenderOptionDebounce = null;

        _npcBrowserService?.Dispose();
        _npcBrowserService = null;

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
        NpcBatchProgressBar.Visibility = Visibility.Collapsed;
        NpcBatchStatusText.Text = "";
    }

    private static string EscapeJsString(string s)
    {
        return s.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\n", "\\n").Replace("\r", "");
    }

    #endregion
}
