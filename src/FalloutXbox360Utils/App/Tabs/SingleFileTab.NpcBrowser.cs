using FalloutXbox360Utils.CLI;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace FalloutXbox360Utils;

/// <summary>
///     NPC Browser tab: 3D viewer, render/export controls, batch operations.
/// </summary>
public sealed partial class SingleFileTab
{
    private CancellationTokenSource? _npcBatchCts;
    private NpcBrowserService? _npcBrowserService;
    private List<NpcListItem>? _npcFilteredList;
    private List<NpcListItem>? _npcFullList;
    private CancellationTokenSource? _npcRenderOptionDebounce;
    private uint? _selectedNpcFormId;
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
        if (_npcFilteredList != null)
        {
            var match = _npcFilteredList.FirstOrDefault(n => n.FormId == npc.FormId);
            if (match == null && _npcFullList != null)
            {
                // NPC may be filtered out — clear filters and refresh
                NpcNamedOnlyCheckBox.IsChecked = false;
                NpcSearchBox.Text = "";
                RefreshNpcList();
                match = _npcFilteredList?.FirstOrDefault(n => n.FormId == npc.FormId);
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

            // If user configured a BSA directory, discover from there instead
            BsaDiscoveryResult bsaPaths;
            if (_session.NpcBsaDirectory != null)
            {
                var pseudoEsmPath = Path.Combine(_session.NpcBsaDirectory, Path.GetFileName(esmPath));
                bsaPaths = await Task.Run(() => BsaDiscovery.Discover(pseudoEsmPath));
            }
            else
            {
                bsaPaths = await Task.Run(() => BsaDiscovery.Discover(esmPath));
            }

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
                var esmData = await Task.Run(() => File.ReadAllBytes(esmPath));
                service = await Task.Run(() =>
                    NpcBrowserService.TryCreate(esmData, bigEndian, esmPath, bsaPaths));
            }

            if (service == null)
            {
                NpcBrowserProgressBar.Visibility = Visibility.Collapsed;
                NpcBrowserStatusText.Text = "Failed to initialize NPC browser.";
                return;
            }

            _npcBrowserService = service;
            _session.NpcBrowserPopulated = true;

            _npcFullList = service.GetNpcList();
            RefreshNpcList();

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
        var esmFile = DiscoverEsmFile(dataDir);

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

        var accessor = _session.Accessor;
        var fileSize = _session.FileSize;

        return await Task.Run(() =>
        {
            var esmData = File.ReadAllBytes(esmFile);
            var esmBigEndian = NpcBrowserService.DetectEsmBigEndian(esmData);

            return NpcBrowserService.TryCreateFromDmp(
                accessor,
                fileSize,
                minidumpInfo,
                scanResult,
                esmData,
                esmBigEndian,
                esmFile,
                bsaPaths);
        });
    }

    /// <summary>
    ///     Finds an ESM file in a game Data directory. Prefers FalloutNV.esm, falls back to any *.esm.
    /// </summary>
    private static string? DiscoverEsmFile(string dataDir)
    {
        var preferred = Path.Combine(dataDir, "FalloutNV.esm");
        if (File.Exists(preferred))
        {
            return preferred;
        }

        var esmFiles = Directory.GetFiles(dataDir, "*.esm");
        return esmFiles.Length > 0 ? esmFiles[0] : null;
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

            NpcModelViewer.CoreWebView2.Navigate("https://npc-viewer-assets/npc-viewer.html");
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
        if (_npcFullList == null)
        {
            return;
        }

        var namedOnly = NpcNamedOnlyCheckBox.IsChecked == true;
        var searchText = NpcSearchBox.Text?.Trim();

        _npcFilteredList = _npcFullList
            .Where(n =>
            {
                if (namedOnly && string.IsNullOrEmpty(n.FullName))
                {
                    return false;
                }

                if (!string.IsNullOrEmpty(searchText))
                {
                    return n.DisplayName.Contains(searchText, StringComparison.OrdinalIgnoreCase)
                           || (n.EditorId?.Contains(searchText, StringComparison.OrdinalIgnoreCase) == true)
                           || $"0x{n.FormId:X8}".Contains(searchText, StringComparison.OrdinalIgnoreCase);
                }

                return true;
            })
            .OrderBy(n => n.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        NpcListView.ItemsSource = _npcFilteredList;

        // Restore selection after ItemsSource reassignment
        if (_selectedNpcFormId.HasValue)
        {
            var match = _npcFilteredList.FirstOrDefault(n => n.FormId == _selectedNpcFormId.Value);
            if (match != null)
            {
                NpcListView.SelectedItem = match;
            }
        }

        NpcCountText.Text = $"{_npcFilteredList.Count} actors" +
                            (_npcFullList.Count != _npcFilteredList.Count
                                ? $" (of {_npcFullList.Count})"
                                : "");
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
        NpcListItem.ShowEditorId = NpcShowEditorIdCheckBox.IsChecked == true;
        RefreshNpcList();
    }

    private async void NpcListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NpcListView.SelectedItem is not NpcListItem npc || _npcBrowserService == null)
        {
            NpcDetailName.Text = "";
            NpcDetailInfo.Text = "";
            NpcExportGlbButton.IsEnabled = false;
            NpcRenderPngButton.IsEnabled = false;
            return;
        }

        _selectedNpcFormId = npc.FormId;
        NpcDetailName.Text = npc.DisplayName;

        if (npc.IsCreature)
        {
            NpcDetailInfo.Text = $"FormID: 0x{npc.FormId:X8}\n" +
                                 $"Editor ID: {npc.EditorId ?? "(none)"}\n" +
                                 $"Type: {npc.CreatureTypeName}\n" +
                                 $"Model: {npc.ModelPath ?? "(none)"}";
            NpcFullBodyCheckBox.IsEnabled = false;
            NpcArmorCheckBox.IsEnabled = false;
            NpcWeaponCheckBox.IsEnabled = false;
            NpcIdlePoseCheckBox.IsEnabled = false;
        }
        else
        {
            NpcDetailInfo.Text = $"FormID: 0x{npc.FormId:X8}\n" +
                                 $"Editor ID: {npc.EditorId ?? "(none)"}\n" +
                                 $"Gender: {(npc.IsFemale ? "Female" : "Male")}";
            NpcFullBodyCheckBox.IsEnabled = true;
            NpcArmorCheckBox.IsEnabled = true;
            NpcWeaponCheckBox.IsEnabled = true;
            NpcIdlePoseCheckBox.IsEnabled = true;
        }

        NpcExportGlbButton.IsEnabled = true;
        NpcRenderPngButton.IsEnabled = !npc.IsCreature; // PNG render not yet supported for creatures

        await LoadNpcIntoViewerAsync(npc);
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

            byte[]? glbBytes;
            if (npc.IsCreature)
            {
                glbBytes = await Task.Run(() =>
                    _npcBrowserService.BuildCreatureGlb(npc.FormId));
            }
            else
            {
                var headOnly = NpcFullBodyCheckBox.IsChecked != true;
                var noEquip = NpcArmorCheckBox.IsChecked != true;
                var noWeapon = NpcWeaponCheckBox.IsChecked != true;
                var bindPose = NpcIdlePoseCheckBox.IsChecked != true;

                glbBytes = await Task.Run(() =>
                    _npcBrowserService.BuildGlb(npc.FormId, headOnly, noEquip, noWeapon, bindPose));
            }

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
        if (_selectedNpcFormId == null || _npcBrowserService == null)
        {
            return;
        }

        var picker = new FileSavePicker();
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeChoices.Add("GLB File", [".glb"]);
        var npc = NpcListView.SelectedItem as NpcListItem;
        picker.SuggestedFileName = npc != null
            ? $"{npc.EditorId ?? $"npc_{npc.FormId:X8}"}.glb"
            : "npc.glb";
        InitializeWithWindow.Initialize(picker, NpcGetWindowHandle());

        var file = await picker.PickSaveFileAsync();
        if (file == null)
        {
            return;
        }

        NpcExportGlbButton.IsEnabled = false;
        try
        {
            byte[]? glbBytes;
            if (npc is { IsCreature: true })
            {
                glbBytes = await Task.Run(() =>
                    _npcBrowserService.BuildCreatureGlb(npc.FormId));
            }
            else
            {
                var headOnly = NpcFullBodyCheckBox.IsChecked != true;
                var noEquip = NpcArmorCheckBox.IsChecked != true;
                var noWeapon = NpcWeaponCheckBox.IsChecked != true;
                var bindPose = NpcIdlePoseCheckBox.IsChecked != true;

                glbBytes = await Task.Run(() =>
                    _npcBrowserService.BuildGlb(_selectedNpcFormId.Value, headOnly, noEquip, noWeapon, bindPose));
            }

            if (glbBytes != null)
            {
                await File.WriteAllBytesAsync(file.Path, glbBytes);
                StatusTextBlock.Text = $"Exported: {file.Name}";
            }
            else
            {
                StatusTextBlock.Text = $"No geometry for this {(npc?.IsCreature == true ? "creature" : "NPC")}";
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
        if (_selectedNpcFormId == null || _npcBrowserService == null)
        {
            return;
        }

        var picker = new FileSavePicker();
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeChoices.Add("PNG Image", [".png"]);
        var npc = NpcListView.SelectedItem as NpcListItem;
        picker.SuggestedFileName = npc != null
            ? $"{npc.EditorId ?? $"npc_{npc.FormId:X8}"}.png"
            : "npc.png";
        InitializeWithWindow.Initialize(picker, NpcGetWindowHandle());

        var file = await picker.PickSaveFileAsync();
        if (file == null)
        {
            return;
        }

        NpcRenderPngButton.IsEnabled = false;
        try
        {
            var headOnly = NpcFullBodyCheckBox.IsChecked != true;
            var noEquip = NpcArmorCheckBox.IsChecked != true;
            var noWeapon = NpcWeaponCheckBox.IsChecked != true;
            var spriteSize = GetSelectedSpriteSize();
            var camera = BuildCameraConfig();
            var views = camera.ResolveViews(defaultAzimuth: 90f);

            foreach (var (suffix, azimuth, elevation) in views)
            {
                var pngBytes = await Task.Run(() =>
                    _npcBrowserService.RenderPng(
                        _selectedNpcFormId.Value, headOnly, noEquip, noWeapon,
                        spriteSize, azimuth, elevation));

                if (pngBytes != null)
                {
                    var outputPath = views.Length > 1
                        ? Path.Combine(
                            Path.GetDirectoryName(file.Path) ?? ".",
                            Path.GetFileNameWithoutExtension(file.Path) + suffix + ".png")
                        : file.Path;
                    await File.WriteAllBytesAsync(outputPath, pngBytes);
                }
            }

            StatusTextBlock.Text = $"Rendered: {(views.Length > 1 ? $"{views.Length} views" : file.Name)}";
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

        var selectedIds = GetSelectedNpcFormIds();
        await RunBatchOperationAsync("Exporting GLBs", async (progress, ct) =>
        {
            var headOnly = NpcFullBodyCheckBox.IsChecked != true;
            var noEquip = NpcArmorCheckBox.IsChecked != true;
            var noWeapon = NpcWeaponCheckBox.IsChecked != true;

            await _npcBrowserService.BatchExportGlbAsync(
                outputDir, headOnly, noEquip, noWeapon, progress, ct, selectedIds);
        });
    }

    private async void NpcBatchRenderPng_Click(object sender, RoutedEventArgs e)
    {
        var outputDir = await PickOutputFolderAsync();
        if (outputDir == null || _npcBrowserService == null)
        {
            return;
        }

        var selectedIds = GetSelectedNpcFormIds();
        await RunBatchOperationAsync("Rendering PNGs", async (progress, ct) =>
        {
            var headOnly = NpcFullBodyCheckBox.IsChecked != true;
            var noEquip = NpcArmorCheckBox.IsChecked != true;
            var noWeapon = NpcWeaponCheckBox.IsChecked != true;
            var spriteSize = GetSelectedSpriteSize();
            var camera = BuildCameraConfig();

            await _npcBrowserService.BatchRenderPngAsync(
                outputDir, headOnly, noEquip, noWeapon, spriteSize, camera, progress, ct, selectedIds);
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
            NpcBatchStatusText.Text = $"{operationName}: {p.Done}/{p.Total} — {p.Name}";
        });

        try
        {
            await work(progress, _npcBatchCts.Token);
            NpcBatchStatusText.Text = $"{operationName} complete.";
        }
        catch (OperationCanceledException)
        {
            NpcBatchStatusText.Text = $"{operationName} cancelled.";
        }
        catch (Exception ex)
        {
            NpcBatchStatusText.Text = $"{operationName} failed: {ex.Message}";
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
        if (_npcFilteredList == null)
        {
            return;
        }

        foreach (var item in _npcFilteredList)
        {
            item.IsSelected = selected;
        }

        UpdateNpcSelectionCountText();
    }

    private List<uint>? GetSelectedNpcFormIds()
    {
        if (_npcFilteredList == null)
        {
            return null;
        }

        var selected = _npcFilteredList.Where(n => n.IsSelected).Select(n => n.FormId).ToList();
        return selected.Count > 0 ? selected : null;
    }

    private void UpdateNpcSelectionCountText()
    {
        if (_npcFilteredList == null || _npcFullList == null)
        {
            return;
        }

        var selectedCount = _npcFilteredList.Count(n => n.IsSelected);
        var filterNote = _npcFullList.Count != _npcFilteredList.Count
            ? $" (of {_npcFullList.Count})"
            : "";
        NpcCountText.Text = selectedCount > 0
            ? $"{_npcFilteredList.Count} actors{filterNote} — {selectedCount} selected"
            : $"{_npcFilteredList.Count} actors{filterNote}";
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
        var value = (int)NpcSizeNumberBox.Value;
        return Math.Clamp(value, 64, 4096);
    }

    private CameraConfig BuildCameraConfig()
    {
        var perspective = "front";
        if (NpcPerspectiveComboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            perspective = tag;
        }

        var elevation = (float)NpcElevationSlider.Value;

        return perspective switch
        {
            "iso" => new CameraConfig
            {
                Isometric = true, ElevationDeg = elevation, ElevationOverridden = true
            },
            "side" => new CameraConfig { SideProfile = true },
            "trimetric" => new CameraConfig { Trimetric = true },
            _ => new CameraConfig { ElevationDeg = elevation, ElevationOverridden = true }
        };
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
        return WindowNative.GetWindowHandle(App.Current.MainWindow);
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

        _npcFullList = null;
        _npcFilteredList = null;
        _selectedNpcFormId = null;

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
