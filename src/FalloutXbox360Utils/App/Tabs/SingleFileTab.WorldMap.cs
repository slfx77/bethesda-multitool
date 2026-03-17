using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI.Text;

namespace FalloutXbox360Utils;

/// <summary>
///     World Map tab: initialization, data loading, and object inspection.
/// </summary>
public sealed partial class SingleFileTab
{
    private CellRecord? _selectedWorldCell;
    private PlacedReference? _selectedWorldObject;

    private async Task PopulateWorldMapAsync()
    {
        if (_session.WorldMapPopulated)
        {
            return;
        }

        // Show progress
        WorldMapProgressBar.Visibility = Visibility.Visible;
        WorldMapStatusText.Text = _session.IsEsmFile
            ? Strings.Status_LoadingWorldData
            : Strings.Status_ParsingWorldData;

        try
        {
            // Save file path: build world data from supplementary ESM or save positions
            if (_session.IsSaveFile)
            {
                var worldData = await BuildSaveWorldDataAsync();
                if (worldData == null)
                {
                    WorldMapStatusText.Text = "No world data available. Use Load Order to load an ESM for terrain.";
                    return;
                }

                _session.WorldViewData = worldData;
                _session.WorldMapPopulated = true;
                WorldMapControl.LoadData(worldData);
                WorldMapPlaceholder.Visibility = Visibility.Collapsed;
                WorldMapContent.Visibility = Visibility.Visible;
                return;
            }

            // Ensure semantic parse is complete
            if (_session.SemanticResult == null)
            {
                WorldMapProgressBar.IsIndeterminate = false;
                await EnsureSemanticParseAsync();
            }

            var semantic = _session.SemanticResult;
            if (semantic == null)
            {
                WorldMapStatusText.Text = Strings.Status_NoWorldData;
                return;
            }

            // Merge load order records so DLC worldspaces appear on the map
            var loadOrderRecords = _session.LoadOrder.BuildMergedRecords();
            if (loadOrderRecords != null)
                semantic = loadOrderRecords.MergeWith(semantic);

            WorldMapStatusText.Text = Strings.Status_BuildingWorldIndex;

            // Build world data on background thread
            var filePath = _session.FilePath;
            var esmWorldData = await Task.Run(() =>
                WorldMapOverlayBuilder.BuildFromRecords(semantic, filePath));

            _session.WorldViewData = esmWorldData;
            _session.WorldMapPopulated = true;

            WorldMapControl.LoadData(esmWorldData);

            WorldMapPlaceholder.Visibility = Visibility.Collapsed;
            WorldMapContent.Visibility = Visibility.Visible;
        }
        finally
        {
            WorldMapProgressBar.Visibility = Visibility.Collapsed;
        }
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
        _selectedWorldCell = cell;
        _selectedWorldObject = null;
        ViewBaseInBrowserButton.Visibility = Visibility.Collapsed;
        ViewCellInDetailButton.Visibility = Visibility.Visible;
        WorldMapControl?.SelectObject(null);

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
        _selectedWorldObject = obj;
        _selectedWorldCell = _session.WorldViewData?.RefrToCellIndex.TryGetValue(obj.FormId, out var ownerCell) == true
            ? ownerCell
            : null;

        // Show "View in Data Browser" for the base record, hide cell button
        ViewBaseInBrowserButton.Visibility = Visibility.Visible;
        ViewCellInDetailButton.Visibility = Visibility.Collapsed;
        var navigable = obj.BaseFormId > 0 && IsFormIdNavigable(obj.BaseFormId);
        ViewBaseInBrowserButton.IsEnabled = navigable;
        ToolTipService.SetToolTip(ViewBaseInBrowserButton, navigable
            ? "View the base record in the Data Browser"
            : "Base record not available in Data Browser (record type not reconstructed)");

        WorldMapControl?.SelectObject(obj);

        var worldResolver = _session.WorldViewData?.Resolver ?? _session.Resolver;
        WorldObjectTitle.Text = PlacedObjectCategoryResolver.GetObjectInspectionTitle(
            obj, _session.WorldViewData, worldResolver);

        WorldPropertyPanel.Children.Clear();
        BuildWorldPropertyPanel(
            PlacedObjectCategoryResolver.BuildObjectProperties(obj, _session.WorldViewData, worldResolver));
    }

    private void BuildWorldPropertyPanel(List<EsmPropertyEntry> properties)
    {
        WorldPropertyPanel.Children.Clear();

        // Use a single Grid matching Data Browser layout (shared column widths)
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
                MaxWidth = 160
            };

            // Cell navigation links (linked cells, door destinations)
            if (sub.CellNavigationFormId is > 0)
            {
                subName.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Microsoft.UI.Colors.CornflowerBlue);
                var capturedFormId = sub.CellNavigationFormId.Value;
                subName.Tapped += (_, _) => NavigateToCellInWorldMap(capturedFormId);
            }
            // Find the placed object for navigation
            else if (sub.Col3FormId is > 0 && _selectedWorldCell != null)
            {
                var targetFormId = sub.Col3FormId.Value;
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

            Grid.SetRow(subName, subRow);
            Grid.SetColumn(subName, 0);
            subItemsGrid.Children.Add(subName);

            // Sub-item FormID (linked if navigable)
            var subFormIdText = sub.Col3 ?? sub.Value;
            var subFormId = sub.Col3FormId ?? sub.LinkedFormId;
            if (subFormId is > 0 && IsFormIdNavigable(subFormId.Value))
            {
                var link = CreateFormIdLink(subFormIdText, subFormId.Value, 11, monospace: true);
                link.Margin = new Thickness(0, 0, 4, 0);
                Grid.SetRow(link, subRow);
                Grid.SetColumn(link, 1);
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
                    IsTextSelectionEnabled = true
                };
                Grid.SetRow(subVal, subRow);
                Grid.SetColumn(subVal, 1);
                subItemsGrid.Children.Add(subVal);
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

        await PopulateWorldMapAsync();
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

        await PopulateWorldMapAsync();
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
        _selectedWorldCell = null;
        _selectedWorldObject = null;
        ViewBaseInBrowserButton.Visibility = Visibility.Collapsed;
        ViewCellInDetailButton.Visibility = Visibility.Collapsed;
        WorldMapPlaceholder.Visibility = Visibility.Visible;
        WorldMapProgressBar.Visibility = Visibility.Collapsed;
        WorldMapStatusText.Text = Strings.Empty_RunAnalysisForWorldMap;
        WorldMapContent.Visibility = Visibility.Collapsed;
        WorldMapControl?.Reset();
    }
}
