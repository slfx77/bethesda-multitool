using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Export;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Scene;
using BethesdaMultitool.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI.Text;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Character;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;

namespace BethesdaMultitool;

/// <summary>
///     World Map tab: initialization, data loading, and object inspection.
/// </summary>
public sealed partial class SingleFileTab
{
    private CellRecord? _selectedWorldCell;
    private PlacedReference? _selectedWorldObject;
    private bool _suppressReferenceStateSelectionChanged;

    private async Task PopulateWorldMapAsync(CancellationToken cancellationToken)
    {
        var loadGeneration = Volatile.Read(ref _worldMapLoadGeneration);
        if (_session.WorldMapPopulated)
        {
            return;
        }

        // Cancellation while WAITING throws before the gate is acquired (outside the try), so the
        // finally's Release stays balanced.
        await _worldMapLoadGate.WaitAsync(cancellationToken);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentWorldMapLoad(loadGeneration) || _session.WorldMapPopulated)
            {
                return;
            }

            // Show progress
            WorldMapProgressBar.Visibility = Visibility.Visible;
            WorldMapProgressBar.IsIndeterminate = true;
            WorldMapStatusText.Text = _session.IsEsmFile
                ? Strings.Status_LoadingWorldData
                : Strings.Status_ParsingWorldData;

            // Save file path: build world data from supplementary ESM or save positions
            if (_session.IsSaveFile)
            {
                var worldData = await BuildSaveWorldDataAsync();
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsCurrentWorldMapLoad(loadGeneration))
                {
                    return;
                }

                if (worldData == null)
                {
                    WorldMapStatusText.Text = "No world data available. Use Load Order to load an ESM for terrain.";
                    return;
                }

                worldData.AdditionalDataPaths = CollectLoadOrderPaths();
                ApplyWorldMapData(worldData);
                return;
            }

            // Ensure semantic parse is complete
            if (_session.SemanticResult == null)
            {
                WorldMapProgressBar.IsIndeterminate = false;
                await EnsureSemanticParseAsync();
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsCurrentWorldMapLoad(loadGeneration))
                {
                    return;
                }
            }

            var semantic = _session.SemanticResult;
            if (semantic == null)
            {
                WorldMapStatusText.Text = Strings.Status_NoWorldData;
                return;
            }

            // Snapshot UI-thread-owned state before going off-thread: LoadOrder.Entries is a
            // UI-mutated ObservableCollection, so the allocation-heavy merge below must work from a
            // snapshot — it used to run right here on the UI thread and hard-froze the map populate
            // for seconds with a DLC-sized load order.
            var loadOrderEntries = _session.LoadOrder.Entries.ToList();
            // Full path, not just the name: the mapper reads this file's MAST list to place its
            // masters on the slots its own raw FormIDs already name (Tes4LoadOrderFormIdMapper).
            var primaryFilePath = _session.IsEsmFile ? _session.FilePath : null;
            var isEsmFile = _session.IsEsmFile;
            var isSaveFile = _session.IsSaveFile;
            var filePath = _session.FilePath;

            WorldMapStatusText.Text = Strings.Status_BuildingWorldIndex;

            // Merge the load order + build world data on a background thread.
            var esmWorldData = await Task.Run(() =>
            {
                var records = semantic;

                // Snapshot the primary's own worldspaces AND cells before merging supplementary
                // Load-Order records. A DMP must show only the worldspaces + cells it captured;
                // Load-Order ESM records are merged in for base-record/terrain/asset data but the
                // ESM's worldspaces (picker) and cells (grid/list) are filtered back out below —
                // only what the dump captured is shown.
                var primaryWorldspaceIds = records.Worldspaces.Select(w => w.FormId).ToHashSet();
                var primaryCellIds = records.Cells.Select(c => c.FormId).ToHashSet();

                // Merge load order records so DLC worldspaces appear on the map. An ESM/ESP primary's
                // MAST list anchors the slots, so entries land where its raw FormIDs already point.
                var loadOrderRecords = LoadOrder.BuildMergedRecordsFrom(loadOrderEntries, primaryFilePath);
                if (loadOrderRecords != null)
                {
                    // Precedence is type-aware: an opened ESM/ESP is the base the Load Order layers on
                    // top of (later wins → ESP edits apply); an opened DMP/save is the runtime truth
                    // and wins over the Load Order. MergeList unions by FormID either way, so DLC/new
                    // worldspaces still appear.
                    records = isEsmFile
                        ? records.MergeWith(loadOrderRecords)
                        : loadOrderRecords.MergeWith(records);

                    // Re-link cells to worldspaces against the MERGED cell list so overridden/added
                    // cells reach the viewer (which reads ws.Cells), not each worldspace's pre-merge
                    // cells. Then resolve placed-object meshes against the merged base set so a ref
                    // that places a base defined in another loaded plugin (e.g. Bloodmoon's Fort
                    // Frostmoth placing Morrowind Imperial-fort statics, or a TES4 mod placing a
                    // vanilla static) gets its ModelPath instead of rendering "missing" — per-source
                    // parse enrichment can't see other plugins.
                    records.RelinkWorldspaceCells().ResolvePlacedModels();

                    // For a memory-dump primary, hide both the worldspaces AND the cells the dump
                    // didn't capture: the ESM is merged in only so dumped objects can resolve their
                    // base models/textures, not to gap-fill the cell grid/list. Re-link again AFTER
                    // filtering so each worldspace's Cells reflects the trimmed (captured-only) set.
                    // (Order matters.)
                    if (!isEsmFile && !isSaveFile)
                    {
                        records = records
                            .WithWorldspacesFilteredTo(primaryWorldspaceIds)
                            .WithCellsFilteredTo(primaryCellIds);
                        records.RelinkWorldspaceCells().ResolvePlacedModels();
                    }
                }

                return WorldMapOverlayBuilder.BuildFromRecords(records, filePath);
            });
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentWorldMapLoad(loadGeneration))
            {
                return;
            }

            esmWorldData.AdditionalDataPaths = CollectLoadOrderPaths();
            ApplyWorldMapData(esmWorldData);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (IsCurrentWorldMapLoad(loadGeneration))
            {
                BethesdaMultitool.Core.Diagnostics.Logger.Instance.Warn(
                    "World map population failed: {0}", ex);
                WorldMapStatusText.Text = $"World map failed: {ex.GetType().Name}: {ex.Message}";
            }
        }
        finally
        {
            if (IsCurrentWorldMapLoad(loadGeneration))
            {
                WorldMapProgressBar.Visibility = Visibility.Collapsed;
            }
            _worldMapLoadGate.Release();
        }
    }

    private bool IsCurrentWorldMapLoad(int loadGeneration) =>
        Volatile.Read(ref _worldMapLoadGeneration) == loadGeneration &&
        (_session.HasEsmRecords || _session.IsSaveFile);

    private void ApplyWorldMapData(WorldViewData worldData)
    {
        // A reload can race a pending top-down request from the old scene. Unwire first, then
        // rewire after the 3D scene has accepted the same WorldViewData instance.
        WorldMapControl.TopDownProvider = null;
        _session.WorldViewData = worldData;

        // Memory dumps get the renamed-asset fuzzy mesh fallback + loose-file overrides in the 3D
        // viewer; ESM/ESP/save views stay exact-only. Set before LoadData so the mesh pipeline opens
        // its archive set with the right resolution mode.
        worldData.IsMemoryDump = !_session.IsEsmFile && !_session.IsSaveFile;

        WorldMapControl.LoadData(worldData);
        WorldView3DControl.LoadData(worldData);

        // Let the 2D map borrow the 3D control's D3D12 stack for the top-down "Rendered
        // models" overlay. Set after 3D LoadData so its reference pipeline is up.
        WorldMapControl.TopDownProvider = WorldView3DControl;
        _session.WorldMapPopulated = true;

        WorldMapPlaceholder.Visibility = Visibility.Collapsed;
        WorldMapContent.Visibility = Visibility.Visible;

        // World data is the single largest CPU-cache load; give trimmable caches a chance to
        // shed older session weight right away instead of waiting for the timer tick.
        MemoryBudgetCoordinator.Instance.CheckNow("world-load");
    }

    /// <summary>
    ///     Snapshots the file paths of every loaded entry in the active Load Order so the 3D
    ///     viewer can discover BSAs in their parent Data folders. Especially load-bearing for
    ///     DMP loads, which carry no adjacent BSAs of their own; without Load Order entries the
    ///     3D viewer would have nowhere to source ground textures and would render all-white.
    /// </summary>
    private IReadOnlyList<string> CollectLoadOrderPaths()
    {
        if (_session.LoadOrder.Entries.Count == 0) return Array.Empty<string>();
        var paths = new List<string>(_session.LoadOrder.Entries.Count);
        foreach (var entry in _session.LoadOrder.Entries)
        {
            if (!string.IsNullOrEmpty(entry.FilePath)) paths.Add(entry.FilePath);
        }
        return paths;
    }

    /// <summary>
    ///     Builds WorldViewData for a save file. Uses supplementary ESM for terrain if available,
    ///     then overlays changed form positions from the save.
    /// </summary>
    private async Task<WorldViewData?> BuildSaveWorldDataAsync()
    {
        var save = _session.SaveData;
        if (save == null) return null;

        var suppRecords = _session.LoadOrder.GetTerrainRecords();
        var resolver = _session.EffectiveResolver ?? FormIdResolver.Empty;
        var supplementaryEsmPath = _session.LoadOrder.GetTerrainFilePath();

        WorldMapStatusText.Text = "Building world map from save data...";

        return await Task.Run(() =>
            WorldMapOverlayBuilder.BuildFromSave(save, suppRecords, resolver, supplementaryEsmPath));
    }

    private void WorldMap_InspectCell(object? sender, CellRecord cell)
    {
        WorldPanelSelector.SelectedItem = WorldPanelInspectionItem; // inspecting → show the Inspection tab
        _selectedWorldCell = cell;
        _selectedWorldObject = null;
        ViewBaseInBrowserButton.Visibility = Visibility.Collapsed;
        ViewCellInDetailButton.Visibility = Visibility.Visible;
        ReferenceStatePanel.Visibility = Visibility.Collapsed;

        // Mirror the guard in WorldMap_InspectObject: a cell inspected from the 3D viewer must
        // not clear the hidden 2D map's selection (the 3D viewer owns its own highlight).
        if (!ReferenceEquals(sender, WorldView3DControl))
        {
            WorldMapControl?.SelectObject(null);
        }

        var name = cell.EditorId ?? cell.FullName ?? $"0x{cell.FormId:X8}";
        WorldObjectTitle.Text = cell.GridX.HasValue && cell.GridY.HasValue
            ? $"Cell [{cell.GridX.Value}, {cell.GridY.Value}]: {name}"
            : $"Cell: {name}";

        var worldResolver = _session.WorldViewData?.Resolver ?? _session.Resolver;
        BuildWorldPropertyPanel(
            WorldMapCellPropertyBuilder.BuildCellProperties(cell, _session.WorldViewData, worldResolver));
    }

    private void ViewCellInDetail_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedWorldCell != null)
        {
            WorldMapControl.NavigateToCell(_selectedWorldCell);
        }
    }

    private void ViewBaseInBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedWorldObject?.BaseFormId is > 0)
        {
            NavigateToFormId(_selectedWorldObject.BaseFormId);
        }
    }

    private void WorldMap_InspectObject(object? sender, PlacedReference obj)
    {
        WorldPanelSelector.SelectedItem = WorldPanelInspectionItem; // inspecting → show the Inspection tab
        _selectedWorldObject = obj;
        _selectedWorldCell = _session.WorldViewData?.PlacedRefs.TryGetCell(obj.FormId, out var ownerCell) == true
            ? ownerCell
            : null;

        // Show "View in Records" for the base record, hide cell button
        ViewBaseInBrowserButton.Visibility = Visibility.Visible;
        ViewCellInDetailButton.Visibility = Visibility.Collapsed;
        var navigable = obj.BaseFormId > 0 && IsFormIdNavigable(obj.BaseFormId);
        ViewBaseInBrowserButton.IsEnabled = navigable;
        ToolTipService.SetToolTip(ViewBaseInBrowserButton, navigable
            ? "View the base record in Records"
            : "Base record not available in Records (record type not reconstructed)");

        // A pick in the 3D viewer must NOT drive the 2D map's selection — the 3D viewer owns its
        // own highlight, and the picked object may belong to a different worldspace than the 2D map is
        // showing (the leak this guards against). Only sync the 2D-map selection when the inspect came
        // from the 2D map or a 2D-map navigation (sender == null), not from WorldView3DControl.
        if (!ReferenceEquals(sender, WorldView3DControl))
        {
            WorldMapControl?.SelectObject(obj);
        }

        var worldResolver = _session.WorldViewData?.Resolver ?? _session.Resolver;
        WorldObjectTitle.Text = PlacedObjectCategoryResolver.GetObjectInspectionTitle(
            obj, _session.WorldViewData, worldResolver);
        UpdateReferenceStateInspection(obj);

        WorldPropertyPanel.Children.Clear();
        var properties = PlacedObjectCategoryResolver.BuildObjectProperties(obj, _session.WorldViewData, worldResolver);

        // When the viewer rendered this ref from a fuzzy-substituted mesh (a renamed prototype path,
        // memory-dump browsing only), surface the path it actually used right after the "Model" row.
        // The requested path is computed the same way BuildObjectProperties does (the ref's own
        // enriched ModelPath, else the base record's ModelPathIndex entry).
        var requestedModel = obj.ModelPath;
        if (string.IsNullOrEmpty(requestedModel) &&
            _session.WorldViewData?.ModelPathIndex.TryGetValue(obj.BaseFormId, out var mp) == true)
        {
            requestedModel = mp;
        }

        if (!string.IsNullOrEmpty(requestedModel) &&
            WorldView3DControl.TryResolveFallbackMeshPath(requestedModel) is { } fallbackMesh)
        {
            var fallbackEntry = new EsmPropertyEntry { Name = "Fallback Mesh", Value = fallbackMesh, Category = "Identity" };
            var modelIndex = properties.FindIndex(p => p.Name == "Model" && p.Category == "Identity");
            if (modelIndex >= 0)
            {
                properties.Insert(modelIndex + 1, fallbackEntry);
            }
            else
            {
                properties.Add(fallbackEntry);
            }
        }

        BuildWorldPropertyPanel(properties);
    }

    /// <summary>
    ///     Shows the per-instance 3D visibility preview for any selected placed reference. The
    ///     authored label is resolved from both the placement flag and its XESP parent chain.
    /// </summary>
    private void UpdateReferenceStateInspection(PlacedReference obj)
    {
        var hasReferencePreview = WorldView3DControl.CanPreviewReferenceVisibility(obj);
        ReferenceStatePanel.Visibility = hasReferencePreview ? Visibility.Visible : Visibility.Collapsed;
        if (!hasReferencePreview) return;

        var enabledOverride = WorldView3DControl.GetReferenceEnabledOverride(obj.FormId);
        _suppressReferenceStateSelectionChanged = true;
        ReferenceStateComboBox.SelectedIndex = (int)enabledOverride;
        _suppressReferenceStateSelectionChanged = false;
        UpdateReferenceStateHint(obj, enabledOverride);
    }

    private void ReferenceStateComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressReferenceStateSelectionChanged || _selectedWorldObject is not { } obj) return;
        if (ReferenceStateComboBox.SelectedIndex is < 0 or > 2) return;

        var enabledOverride = (ReferenceEnabledOverride)ReferenceStateComboBox.SelectedIndex;
        WorldView3DControl.SetReferenceEnabledOverride(obj.FormId, enabledOverride);
        UpdateReferenceStateHint(obj, enabledOverride);
    }

    private void WorldView3D_ReferenceEnabledOverridesReset(object? sender, EventArgs e)
    {
        if (_selectedWorldObject is { } obj) UpdateReferenceStateInspection(obj);
    }

    private void UpdateReferenceStateHint(PlacedReference obj, ReferenceEnabledOverride enabledOverride)
    {
        var placementAuthored = WorldView3DControl.IsReferenceAuthoredEnabled(obj) ? "Shown" : "Hidden";
        var lightAuthored = WorldView3DControl.IsReferenceBaseLightAuthoredEnabled(obj) switch
        {
            true => "; base LIGH emission: On",
            false => "; base LIGH emission: Off By Default",
            null => string.Empty,
        };
        ReferenceStateHint.Text = enabledOverride switch
        {
            ReferenceEnabledOverride.Authored =>
                $"Authored placement state: {placementAuthored} (REFR/XESP){lightAuthored}. Quest/script runtime changes are not simulated.",
            ReferenceEnabledOverride.On =>
                $"Preview forces Form ID 0x{obj.FormId:X8} Shown at the authored-state gate; independent layer, category, lighting, and water filters still apply.",
            _ =>
                $"Preview hides Form ID 0x{obj.FormId:X8} from supported 3D output (mesh, light, and embedded water as applicable), picks, collision preview, and walk collision; the parsed record is unchanged.",
        };
    }

    private void BuildWorldPropertyPanel(List<EsmPropertyEntry> properties)
    {
        WorldPropertyPanel.Children.Clear();

        // Use a single Grid matching Records layout (shared column widths)
        var mainGrid = new Grid();
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // icon/spacer
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // name
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // value

        var currentRow = 0;
        var propertyRowIndex = 0;
        string? lastCategory = null;

        var foregroundBrush = (Microsoft.UI.Xaml.Media.SolidColorBrush)
            Application.Current.Resources["TextFillColorPrimaryBrush"];
        var altRowBrush = CreateAlternatingRowBrush();

        foreach (var prop in properties)
        {
            // Category header
            if (prop.Category != null && prop.Category != lastCategory)
            {
                lastCategory = prop.Category;
                propertyRowIndex = 0;
                AddCategoryHeader(mainGrid, prop.Category, currentRow, 3, foregroundBrush);
                currentRow++;
            }

            if (prop.IsExpandable && prop.SubItems?.Count > 0)
            {
                currentRow = AddExpandablePropertyRow(
                    mainGrid, prop, currentRow, ref propertyRowIndex, altRowBrush);
            }
            else
            {
                AddNormalPropertyRow(mainGrid, prop, currentRow, ref propertyRowIndex, altRowBrush);
                currentRow++;
            }
        }

        WorldPropertyPanel.Children.Add(mainGrid);
    }

    private int AddExpandablePropertyRow(
        Grid mainGrid, EsmPropertyEntry prop, int currentRow,
        ref int propertyRowIndex, Microsoft.UI.Xaml.Media.SolidColorBrush altRowBrush)
    {
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddAlternatingRowBackground(mainGrid, currentRow, 3, propertyRowIndex, altRowBrush);

        var expandIcon = new TextBlock
        {
            Text = "\u25B6",
            FontSize = 10,
            Width = 18,
            Padding = new Thickness(4, 3, 0, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        };
        Grid.SetRow(expandIcon, currentRow);
        Grid.SetColumn(expandIcon, 0);
        mainGrid.Children.Add(expandIcon);

        var nameText = new TextBlock
        {
            Text = prop.Name,
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Padding = new Thickness(0, 3, 16, 2),
            IsTextSelectionEnabled = true
        };
        Grid.SetRow(nameText, currentRow);
        Grid.SetColumn(nameText, 1);
        mainGrid.Children.Add(nameText);

        var countText = new TextBlock
        {
            Text = prop.Value,
            FontSize = 12,
            Padding = new Thickness(0, 3, 4, 2),
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        };
        Grid.SetRow(countText, currentRow);
        Grid.SetColumn(countText, 2);
        mainGrid.Children.Add(countText);

        currentRow++;

        // Sub-items grid (collapsible)
        var subItemsGrid = BuildSubItemsGrid(prop.SubItems!);

        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(subItemsGrid, currentRow);
        Grid.SetColumnSpan(subItemsGrid, 3);
        mainGrid.Children.Add(subItemsGrid);

        // Toggle expand/collapse on header click
        var capturedIcon = expandIcon;
        var capturedSubItems = subItemsGrid;
        nameText.Tapped += (_, _) => ToggleExpandSection(capturedIcon, capturedSubItems);
        expandIcon.Tapped += (_, _) => ToggleExpandSection(capturedIcon, capturedSubItems);
        countText.Tapped += (_, _) => ToggleExpandSection(capturedIcon, capturedSubItems);

        currentRow++;
        propertyRowIndex++;
        return currentRow;
    }

    private Grid BuildSubItemsGrid(List<EsmPropertyEntry> subItems)
    {
        var subItemsGrid = new Grid { Visibility = Visibility.Collapsed };
        // Col 0: editor ID / name (clickable for in-map nav)
        // Col 1: full base name (in-game FULL), auto-sized
        // Col 2: FormID / value (link to Records when navigable)
        // Col 3: optional contextual link (door destination, etc.)
        subItemsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        subItemsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        subItemsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        subItemsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var subRow = 0;
        foreach (var sub in subItems)
        {
            subItemsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Sub-item name / editor ID (clickable to navigate in map)
            var subName = new TextBlock
            {
                Text = sub.Col1 ?? sub.Name,
                FontSize = 11,
                Padding = new Thickness(22, 1, 12, 1),
                IsTextSelectionEnabled = true,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 200
            };

            // Cell navigation links (linked cells, door destinations)
            if (sub.CellNavigationFormId is > 0)
            {
                subName.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Microsoft.UI.Colors.CornflowerBlue);
                var capturedFormId = sub.CellNavigationFormId.Value;
                subName.Tapped += (_, _) => NavigateToCellInWorldMap(capturedFormId);
            }
            // In-map navigation to a placed reference
            else
            {
                var placedRefFormId = sub.PlacedReferenceFormId ?? sub.Col3FormId;
                if (placedRefFormId is > 0 && _selectedWorldCell != null)
                {
                    var targetFormId = placedRefFormId.Value;
                    var placedObj = _selectedWorldCell.PlacedObjects
                        .FirstOrDefault(o => o.FormId == targetFormId);
                    if (placedObj != null)
                    {
                        subName.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                            Microsoft.UI.Colors.CornflowerBlue);
                        var capturedObj = placedObj;
                        subName.Tapped += (_, _) =>
                        {
                            WorldMapControl?.NavigateToObjectInOverview(capturedObj);
                            WorldMap_InspectObject(null, capturedObj);
                        };
                    }
                }
            }

            Grid.SetRow(subName, subRow);
            Grid.SetColumn(subName, 0);
            subItemsGrid.Children.Add(subName);

            // Optional full-name column (Col2)
            if (!string.IsNullOrEmpty(sub.Col2))
            {
                var fullName = new TextBlock
                {
                    Text = sub.Col2,
                    FontSize = 11,
                    Padding = new Thickness(0, 1, 12, 1),
                    IsTextSelectionEnabled = true,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current
                        .Resources["TextFillColorSecondaryBrush"]
                };
                Grid.SetRow(fullName, subRow);
                Grid.SetColumn(fullName, 1);
                subItemsGrid.Children.Add(fullName);
            }

            // Sub-item FormID column — prefer LinkedFormId (base record) for Records navigation,
            // falling back to Col3FormId for legacy callers.
            var subFormIdText = sub.Col3 ?? sub.Value;
            var subFormId = sub.LinkedFormId ?? sub.Col3FormId;
            if (subFormId is > 0 && IsFormIdNavigable(subFormId.Value))
            {
                var link = CreateFormIdLink(subFormIdText, subFormId.Value, 11, monospace: true);
                link.Margin = new Thickness(0, 0, 4, 0);
                Grid.SetRow(link, subRow);
                Grid.SetColumn(link, 2);
                subItemsGrid.Children.Add(link);
            }
            else
            {
                var subVal = new TextBlock
                {
                    Text = subFormIdText,
                    FontSize = 11,
                    Padding = new Thickness(0, 1, 4, 1),
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                    IsTextSelectionEnabled = true,
                    TextWrapping = TextWrapping.Wrap
                };
                Grid.SetRow(subVal, subRow);
                Grid.SetColumn(subVal, 2);
                subItemsGrid.Children.Add(subVal);
            }

            if (!string.IsNullOrEmpty(sub.Col4))
            {
                var col4Text = new TextBlock
                {
                    Text = sub.Col4,
                    FontSize = 11,
                    Padding = new Thickness(0, 1, 4, 1),
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                    IsTextSelectionEnabled = true,
                    TextWrapping = TextWrapping.Wrap
                };

                if (sub.Col4CellNavigationFormId is > 0)
                {
                    col4Text.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                        Microsoft.UI.Colors.CornflowerBlue);
                    col4Text.TextDecorations = TextDecorations.Underline;
                    var capturedFormId = sub.Col4CellNavigationFormId.Value;
                    col4Text.Tapped += (_, _) => NavigateToCellInWorldMap(capturedFormId);
                }

                Grid.SetRow(col4Text, subRow);
                Grid.SetColumn(col4Text, 3);
                subItemsGrid.Children.Add(col4Text);
            }

            subRow++;
        }

        return subItemsGrid;
    }

    private void AddNormalPropertyRow(
        Grid mainGrid, EsmPropertyEntry prop, int currentRow,
        ref int propertyRowIndex, Microsoft.UI.Xaml.Media.SolidColorBrush altRowBrush)
    {
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddAlternatingRowBackground(mainGrid, currentRow, 3, propertyRowIndex, altRowBrush);

        // Spacer for icon column alignment
        var spacer = new TextBlock { Width = 18, Padding = new Thickness(4, 3, 0, 2) };
        Grid.SetRow(spacer, currentRow);
        Grid.SetColumn(spacer, 0);
        mainGrid.Children.Add(spacer);

        var nameText = new TextBlock
        {
            Text = prop.Name,
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Padding = new Thickness(0, 3, 16, 2),
            IsTextSelectionEnabled = true
        };
        Grid.SetRow(nameText, currentRow);
        Grid.SetColumn(nameText, 1);
        mainGrid.Children.Add(nameText);

        // Value column: cell navigation link, FormID link, or plain text
        if (prop.CellNavigationFormId is > 0)
        {
            var linkColor = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                ActualTheme == ElementTheme.Light
                    ? Windows.UI.Color.FromArgb(0xFF, 0x00, 0x66, 0xCC)
                    : Windows.UI.Color.FromArgb(0xFF, 0x75, 0xBE, 0xFF));
            var linkText = new TextBlock
            {
                Text = prop.Value,
                TextDecorations = TextDecorations.Underline,
                FontSize = 12,
                Foreground = linkColor
            };
            var cellLink = new HyperlinkButton
            {
                Content = linkText,
                Padding = new Thickness(0)
            };
            var capturedCellFormId = prop.CellNavigationFormId.Value;
            cellLink.Click += (_, _) => NavigateToCellInWorldMap(capturedCellFormId);
            cellLink.Margin = new Thickness(0, 2, 4, 2);
            Grid.SetRow(cellLink, currentRow);
            Grid.SetColumn(cellLink, 2);
            mainGrid.Children.Add(cellLink);
        }
        else if (prop.LinkedFormId is > 0 && IsFormIdNavigable(prop.LinkedFormId.Value))
        {
            var link = CreateFormIdLink(prop.Value, prop.LinkedFormId.Value, 12, monospace: true);
            link.Margin = new Thickness(0, 2, 4, 2);
            Grid.SetRow(link, currentRow);
            Grid.SetColumn(link, 2);
            mainGrid.Children.Add(link);
        }
        else
        {
            var valueText = new TextBlock
            {
                Text = prop.Value,
                FontSize = 12,
                Padding = new Thickness(0, 3, 4, 2),
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            };
            Grid.SetRow(valueText, currentRow);
            Grid.SetColumn(valueText, 2);
            mainGrid.Children.Add(valueText);
        }

        propertyRowIndex++;
    }

    private void NavigateToCellInWorldMap(uint cellFormId)
    {
        if (_session.WorldViewData?.CellByFormId.TryGetValue(cellFormId, out var cell) != true || cell == null)
        {
            return;
        }

        // Route the jump to whichever viewer is active. A door-destination link clicked while the
        // 3D view is showing must drive the 3D camera, not silently re-select the hidden 2D map
        // (WorldViewModeComboBox: 0 = 2D Map, 1 = 3D View).
        if (WorldViewModeComboBox.SelectedIndex == 1)
        {
            WorldView3DControl.NavigateToCell(cell);
            return;
        }

        // For exterior cells, navigate to worldspace first
        if (cell.WorldspaceFormId is > 0)
        {
            var wsIdx = _session.WorldViewData.Worldspaces.FindIndex(ws => ws.FormId == cell.WorldspaceFormId.Value);
            if (wsIdx >= 0)
            {
                WorldMapControl.NavigateToWorldspaceAndCell(wsIdx, cell);
                return;
            }
        }

        WorldMapControl.NavigateToCell(cell);
    }

    private async void ViewWorldspace_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedBrowserNode?.DataObject is not WorldspaceRecord ws)
        {
            return;
        }

        await _tasks.RunExclusiveAsync("populate-worldmap", PopulateWorldMapAsync);
        if (_session.WorldViewData == null)
        {
            return;
        }

        var wsIdx = _session.WorldViewData.Worldspaces.FindIndex(w => w.FormId == ws.FormId);
        if (wsIdx < 0)
        {
            return;
        }

        PushUnifiedNav();
        SubTabView.SelectedItem = WorldMapTab;
        WorldMapControl.NavigateToWorldspace(wsIdx);
    }

    private async void ViewInWorld_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedBrowserNode?.DataObject == null || _placementIndex == null)
        {
            return;
        }

        var formId = _selectedBrowserNode.DataObject switch
        {
            NpcRecord npc => npc.FormId,
            CreatureRecord crea => crea.FormId,
            _ => 0u
        };

        if (formId == 0 || !_placementIndex.TryGetValue(formId, out var placements) || placements.Count == 0)
        {
            return;
        }

        await _tasks.RunExclusiveAsync("populate-worldmap", PopulateWorldMapAsync);
        if (_session.WorldViewData == null)
        {
            return;
        }

        var cellFormId = placements[0].Cell.FormId;
        PushUnifiedNav();
        SubTabView.SelectedItem = WorldMapTab;
        NavigateToCellInWorldMap(cellFormId);
    }

    private void ResetWorldMap()
    {
        Interlocked.Increment(ref _worldMapLoadGeneration);
        _selectedWorldCell = null;
        _selectedWorldObject = null;
        WorldPanelSelector.SelectedItem = WorldPanelSettingsItem; // back to the default tab
        ViewBaseInBrowserButton.Visibility = Visibility.Collapsed;
        ViewCellInDetailButton.Visibility = Visibility.Collapsed;
        WorldMapPlaceholder.Visibility = Visibility.Visible;
        WorldMapProgressBar.Visibility = Visibility.Collapsed;
        WorldMapStatusText.Text = Strings.Empty_RunAnalysisForWorldMap;
        WorldMapContent.Visibility = Visibility.Collapsed;
        WorldMapControl.TopDownProvider = null;
        WorldMapControl?.Reset();
    }

    private void WorldViewMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectedIndex 0 = 2D Map, 1 = 3D View. Swap Visibility instead of re-parenting so
        // neither view has to rebuild on toggle.
        //
        // The XAML loader fires SelectionChanged when SelectedIndex="0" is applied during
        // initial construction — BEFORE WorldMapControl and WorldView3DControl x:Name fields
        // are assigned (they appear later in the document). The null guards keep the first
        // synthetic fire harmless.
        if (WorldMapControl is null || WorldView3DControl is null) return;

        var show3D = WorldViewModeComboBox.SelectedIndex == 1;

        // Carry the outgoing view's location + selection into the incoming view so the user stays in the
        // same area (and keeps their selection) across the switch. Capture from the still-visible source
        // BEFORE toggling visibility. Only once the world data is loaded (the combo also fires this
        // handler during XAML init).
        if (_session.WorldMapPopulated)
        {
            if (show3D) WorldView3DControl.ApplyViewFocus(WorldMapControl.CaptureViewFocus());
            else WorldMapControl.ApplyViewFocus(WorldView3DControl.CaptureViewFocus());
        }

        WorldMapControl.Visibility = show3D ? Visibility.Collapsed : Visibility.Visible;
        WorldView3DControl.Visibility = show3D ? Visibility.Visible : Visibility.Collapsed;

        // The Settings tab always shows the ACTIVE viewer's settings panel. Content swap (not
        // visibility) so each panel keeps its Expander state; the panels are owned by the viewers.
        WorldSettingsPresenter.Content = show3D
            ? (UIElement)WorldView3DControl.SettingsPanel
            : WorldMapControl.SettingsPanel;

        // The Export tab always shows the ACTIVE viewer's export panel — the 2D map's PNG export moved
        // out of its modal dialog into WorldMapExportPanel, so the tab is no longer 3D-only. Content
        // swap (not visibility) so each panel keeps its Expander state and its typed folder/name.
        WorldExportPresenter.Content = show3D
            ? (UIElement)WorldView3DControl.ExportPanel
            : WorldMapControl.ExportPanel;

        // Both panels compute their captured bounds + output-size readout from the active worldspace, so
        // refresh whichever one just became visible.
        if (show3D) WorldView3DControl.RefreshExportBounds();
        else WorldMapControl.RefreshExportBounds();
    }

    /// <summary>Settings / Inspection / Export tab switch for the world-map right panel (SelectorBar).</summary>
    private void WorldPanelSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var settings = sender.SelectedItem == WorldPanelSettingsItem;
        var export = sender.SelectedItem == WorldPanelExportItem;
        WorldSettingsHost.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
        WorldExportHost.Visibility = export ? Visibility.Visible : Visibility.Collapsed;
        WorldInspectionHost.Visibility = !settings && !export ? Visibility.Visible : Visibility.Collapsed;
        // Only draw the export framing overlay while its tab is up AND the 3D viewer is the active one
        // (the overlay is a 3D-scene wireframe); refresh bounds/output-size on open so they reflect the
        // current worldspace (covers a worldspace/interior change since last open).
        var show3D = WorldViewModeComboBox.SelectedIndex == 1;
        WorldView3DControl.SetExportFramingActive(export && show3D);
        if (!export) return;
        if (show3D) WorldView3DControl.RefreshExportBounds();
        else WorldMapControl.RefreshExportBounds();
    }
}

