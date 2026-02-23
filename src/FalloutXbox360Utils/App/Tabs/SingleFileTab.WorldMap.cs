using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.SaveGame;
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
            : Strings.Status_ReconstructingWorldData;

        try
        {
            // Save file path: build world data from supplementary ESM or save positions
            if (_session.IsSaveFile)
            {
                var worldData = await BuildSaveWorldDataAsync();
                if (worldData == null)
                {
                    WorldMapStatusText.Text = "No world data available. Use Add Data to load an ESM for terrain.";
                    return;
                }

                _session.WorldViewData = worldData;
                _session.WorldMapPopulated = true;
                WorldMapControl.LoadData(worldData);
                WorldMapPlaceholder.Visibility = Visibility.Collapsed;
                WorldMapContent.Visibility = Visibility.Visible;
                return;
            }

            // Ensure semantic reconstruction is complete
            if (_session.SemanticResult == null)
            {
                WorldMapProgressBar.IsIndeterminate = false;
                await EnsureSemanticReconstructionAsync();
            }

            var semantic = _session.SemanticResult;
            if (semantic == null)
            {
                WorldMapStatusText.Text = Strings.Status_NoWorldData;
                return;
            }

            WorldMapStatusText.Text = Strings.Status_BuildingWorldIndex;

            // Build world data on background thread
            var esmWorldData = await Task.Run(() =>
            {
                var (boundsIndex, categoryIndex) = ObjectBoundsIndex.BuildCombined(semantic);

                // Pre-compute grayscale heightmap and water mask for the first (default) worldspace
                byte[]? hmGrayscale = null;
                byte[]? hmWaterMask = null;
                int hmWidth = 0, hmHeight = 0, hmMinX = 0, hmMaxY = 0;
                float? defaultWaterHeight = null;
                if (semantic.Worldspaces.Count > 0 && semantic.Worldspaces[0].Cells.Count > 0)
                {
                    defaultWaterHeight = semantic.Worldspaces[0].DefaultWaterHeight;
                    var result = WorldMapControl.ComputeHeightmapData(
                        semantic.Worldspaces[0].Cells, defaultWaterHeight);
                    if (result.HasValue)
                    {
                        (hmGrayscale, hmWaterMask, hmWidth, hmHeight, hmMinX, hmMaxY) = result.Value;
                    }
                }

                // Group map markers by worldspace using cell ownership (GRUP-based, not coordinates)
                var markersByWorldspace = new Dictionary<uint, List<PlacedReference>>();
                foreach (var ws in semantic.Worldspaces)
                {
                    var wsMarkers = new List<PlacedReference>();
                    foreach (var cell in ws.Cells)
                    {
                        wsMarkers.AddRange(cell.PlacedObjects.Where(o => o.IsMapMarker));
                    }

                    if (wsMarkers.Count > 0)
                    {
                        markersByWorldspace[ws.FormId] = wsMarkers;
                    }
                }

                // Find exterior cells with grid coords but no worldspace linkage (common in DMP files)
                var linkedCellFormIds = new HashSet<uint>();
                foreach (var ws in semantic.Worldspaces)
                {
                    foreach (var cell in ws.Cells)
                    {
                        linkedCellFormIds.Add(cell.FormId);
                    }
                }

                var unlinkedExterior = semantic.Cells
                    .Where(c => !c.IsInterior && c.GridX.HasValue && c.GridY.HasValue &&
                                !linkedCellFormIds.Contains(c.FormId))
                    .ToList();

                // Collect map markers not assigned to any worldspace (common in DMP files)
                var linkedMarkerFormIds = new HashSet<uint>(
                    markersByWorldspace.Values.SelectMany(m => m).Select(m => m.FormId));
                var unlinkedMarkers = semantic.MapMarkers
                    .Where(m => !linkedMarkerFormIds.Contains(m.FormId))
                    .ToList();

                // Build cell FormID lookup for navigation
                var cellByFormId = new Dictionary<uint, CellRecord>();
                foreach (var cell in semantic.Cells)
                {
                    cellByFormId.TryAdd(cell.FormId, cell);
                }

                // Build reverse index: placed reference FormID → parent cell
                var refrToCellIndex = new Dictionary<uint, CellRecord>();
                var refPositionIndex = new Dictionary<uint, (float X, float Y)>();
                foreach (var cell in semantic.Cells)
                {
                    foreach (var obj in cell.PlacedObjects)
                    {
                        refrToCellIndex.TryAdd(obj.FormId, cell);
                        if (obj.FormId != 0)
                        {
                            refPositionIndex.TryAdd(obj.FormId, (obj.X, obj.Y));
                        }
                    }
                }

                // Build spawn resolution index
                var spawnIndex = SpawnResolutionIndex.Build(semantic);

                return new WorldViewData
                {
                    Worldspaces = semantic.Worldspaces,
                    InteriorCells = semantic.Cells.Where(c => c.IsInterior).ToList(),
                    UnlinkedExteriorCells = unlinkedExterior,
                    UnlinkedMapMarkers = unlinkedMarkers,
                    AllCells = semantic.Cells,
                    CellByFormId = cellByFormId,
                    RefrToCellIndex = refrToCellIndex,
                    BoundsIndex = boundsIndex,
                    CategoryIndex = categoryIndex,
                    Resolver = semantic.CreateResolver(),
                    MapMarkers = semantic.MapMarkers,
                    MarkersByWorldspace = markersByWorldspace,
                    DefaultWaterHeight = defaultWaterHeight,
                    HeightmapGrayscale = hmGrayscale,
                    HeightmapWaterMask = hmWaterMask,
                    HeightmapPixelWidth = hmWidth,
                    HeightmapPixelHeight = hmHeight,
                    HeightmapMinCellX = hmMinX,
                    HeightmapMaxCellY = hmMaxY,
                    SourceFilePath = _session.FilePath,
                    SpawnIndex = spawnIndex,
                    RefPositionIndex = refPositionIndex
                };
            });

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

        var suppRecords = _session.Supplementary?.EsmRecords;
        var resolver = _session.EffectiveResolver ?? FormIdResolver.Empty;
        var formIdArray = save.FormIdArray.ToArray();

        WorldMapStatusText.Text = "Building world map from save data...";

        var worldData = await Task.Run(() =>
        {
            // Build save overlay markers from changed forms with positions
            var overlayMarkers = new List<PlacedReference>();
            foreach (var form in save.ChangedForms)
            {
                if (form.Initial == null) continue;
                if (form.ChangeType is not (0 or 1 or 2)) continue; // REFR, ACHR, ACRE only

                var resolvedFormId = form.RefId.ResolveFormId(formIdArray);
                var recordType = form.ChangeType switch
                {
                    0 => "REFR",
                    1 => "ACHR",
                    2 => "ACRE",
                    _ => "REFR"
                };

                overlayMarkers.Add(new PlacedReference
                {
                    FormId = resolvedFormId,
                    BaseFormId = resolvedFormId, // Save forms don't have separate base
                    BaseEditorId = resolver.GetBestNameWithRefChain(resolvedFormId),
                    RecordType = recordType,
                    X = form.Initial.PosX,
                    Y = form.Initial.PosY,
                    Z = form.Initial.PosZ,
                    RotX = form.Initial.RotX,
                    RotY = form.Initial.RotY,
                    RotZ = form.Initial.RotZ
                });
            }

            // Player position
            (float X, float Y, float Z)? playerPos = save.PlayerLocation != null
                ? (save.PlayerLocation.PosX, save.PlayerLocation.PosY, save.PlayerLocation.PosZ)
                : null;

            if (suppRecords != null)
            {
                // Full world data from supplementary ESM
                var (boundsIndex, categoryIndex) = ObjectBoundsIndex.BuildCombined(suppRecords);

                byte[]? hmGrayscale = null;
                byte[]? hmWaterMask = null;
                int hmWidth = 0, hmHeight = 0, hmMinX = 0, hmMaxY = 0;
                float? defaultWaterHeight = null;
                if (suppRecords.Worldspaces.Count > 0 && suppRecords.Worldspaces[0].Cells.Count > 0)
                {
                    defaultWaterHeight = suppRecords.Worldspaces[0].DefaultWaterHeight;
                    var hmResult = WorldMapControl.ComputeHeightmapData(
                        suppRecords.Worldspaces[0].Cells, defaultWaterHeight);
                    if (hmResult.HasValue)
                    {
                        (hmGrayscale, hmWaterMask, hmWidth, hmHeight, hmMinX, hmMaxY) = hmResult.Value;
                    }
                }

                var markersByWorldspace = new Dictionary<uint, List<PlacedReference>>();
                foreach (var ws in suppRecords.Worldspaces)
                {
                    var wsMarkers = new List<PlacedReference>();
                    foreach (var cell in ws.Cells)
                        wsMarkers.AddRange(cell.PlacedObjects.Where(o => o.IsMapMarker));
                    if (wsMarkers.Count > 0)
                        markersByWorldspace[ws.FormId] = wsMarkers;
                }

                var linkedCellFormIds = new HashSet<uint>();
                foreach (var ws in suppRecords.Worldspaces)
                foreach (var cell in ws.Cells)
                    linkedCellFormIds.Add(cell.FormId);

                var unlinkedExterior = suppRecords.Cells
                    .Where(c => !c.IsInterior && c.GridX.HasValue && c.GridY.HasValue &&
                                !linkedCellFormIds.Contains(c.FormId))
                    .ToList();

                var linkedMarkerFormIds = new HashSet<uint>(
                    markersByWorldspace.Values.SelectMany(m => m).Select(m => m.FormId));
                var unlinkedMarkers = suppRecords.MapMarkers
                    .Where(m => !linkedMarkerFormIds.Contains(m.FormId))
                    .ToList();

                var cellByFormId = new Dictionary<uint, CellRecord>();
                foreach (var cell in suppRecords.Cells)
                    cellByFormId.TryAdd(cell.FormId, cell);

                var refrToCellIndex = new Dictionary<uint, CellRecord>();
                var refPositionIndex = new Dictionary<uint, (float X, float Y)>();
                foreach (var cell in suppRecords.Cells)
                {
                    foreach (var obj in cell.PlacedObjects)
                    {
                        refrToCellIndex.TryAdd(obj.FormId, cell);
                        if (obj.FormId != 0)
                            refPositionIndex.TryAdd(obj.FormId, (obj.X, obj.Y));
                    }
                }

                var spawnIndex = SpawnResolutionIndex.Build(suppRecords);

                return new WorldViewData
                {
                    Worldspaces = suppRecords.Worldspaces,
                    InteriorCells = suppRecords.Cells.Where(c => c.IsInterior).ToList(),
                    UnlinkedExteriorCells = unlinkedExterior,
                    UnlinkedMapMarkers = unlinkedMarkers,
                    AllCells = suppRecords.Cells,
                    CellByFormId = cellByFormId,
                    RefrToCellIndex = refrToCellIndex,
                    BoundsIndex = boundsIndex,
                    CategoryIndex = categoryIndex,
                    Resolver = resolver,
                    MapMarkers = suppRecords.MapMarkers,
                    MarkersByWorldspace = markersByWorldspace,
                    DefaultWaterHeight = defaultWaterHeight,
                    HeightmapGrayscale = hmGrayscale,
                    HeightmapWaterMask = hmWaterMask,
                    HeightmapPixelWidth = hmWidth,
                    HeightmapPixelHeight = hmHeight,
                    HeightmapMinCellX = hmMinX,
                    HeightmapMaxCellY = hmMaxY,
                    SourceFilePath = _session.Supplementary?.EsmFilePath,
                    SpawnIndex = spawnIndex,
                    RefPositionIndex = refPositionIndex,
                    SaveOverlayMarkers = overlayMarkers,
                    PlayerPosition = playerPos
                };
            }
            else
            {
                // Minimal world data from save positions only (no terrain)
                return new WorldViewData
                {
                    Worldspaces = [],
                    InteriorCells = [],
                    UnlinkedExteriorCells = [],
                    UnlinkedMapMarkers = [],
                    AllCells = [],
                    CellByFormId = [],
                    RefrToCellIndex = [],
                    BoundsIndex = [],
                    CategoryIndex = [],
                    Resolver = resolver,
                    MapMarkers = [],
                    MarkersByWorldspace = [],
                    SaveOverlayMarkers = overlayMarkers,
                    PlayerPosition = playerPos
                };
            }
        });

        return worldData;
    }

    private void WorldMap_InspectCell(object? sender, CellRecord cell)
    {
        _selectedWorldCell = cell;
        _selectedWorldObject = null;
        ViewBaseInBrowserButton.Visibility = Visibility.Collapsed;
        WorldMapControl?.SelectObject(null);

        var name = cell.EditorId ?? cell.FullName ?? $"0x{cell.FormId:X8}";
        WorldObjectTitle.Text = cell.GridX.HasValue && cell.GridY.HasValue
            ? $"Cell [{cell.GridX.Value}, {cell.GridY.Value}]: {name}"
            : $"Cell: {name}";

        BuildWorldPropertyPanel(BuildCellProperties(cell));
    }

    private List<EsmPropertyEntry> BuildCellProperties(CellRecord cell)
    {
        var properties = new List<EsmPropertyEntry>();

        // Identity
        properties.Add(new EsmPropertyEntry
            { Name = "Form ID", Value = $"0x{cell.FormId:X8}", Category = "Identity" });
        if (!string.IsNullOrEmpty(cell.EditorId))
        {
            properties.Add(new EsmPropertyEntry
                { Name = "Editor ID", Value = cell.EditorId, Category = "Identity" });
        }

        if (!string.IsNullOrEmpty(cell.FullName))
        {
            properties.Add(new EsmPropertyEntry
                { Name = "Full Name", Value = cell.FullName, Category = "Identity" });
        }

        // Grid
        if (cell.GridX.HasValue && cell.GridY.HasValue)
        {
            properties.Add(new EsmPropertyEntry
                { Name = "Grid", Value = $"({cell.GridX.Value}, {cell.GridY.Value})", Category = "Grid" });
        }

        if (cell.WorldspaceFormId is > 0)
        {
            properties.Add(new EsmPropertyEntry
            {
                Name = "Worldspace",
                Value = $"0x{cell.WorldspaceFormId.Value:X8}",
                Category = "Grid",
                LinkedFormId = cell.WorldspaceFormId.Value
            });
        }

        // Properties
        properties.Add(new EsmPropertyEntry
            { Name = "Interior", Value = cell.IsInterior ? "Yes" : "No", Category = "Properties" });
        if (cell.HasWater)
        {
            properties.Add(new EsmPropertyEntry
                { Name = "Has Water", Value = "Yes", Category = "Properties" });
            if (cell.WaterHeight.HasValue && cell.WaterHeight.Value < 1_000_000f)
            {
                properties.Add(new EsmPropertyEntry
                    { Name = "Water Height", Value = $"{cell.WaterHeight.Value:F1}", Category = "Properties" });
            }
        }

        // Audio/Visual
        AddFormIdProperty(properties, "Encounter Zone", cell.EncounterZoneFormId, "Audio/Visual");
        AddFormIdProperty(properties, "Music Type", cell.MusicTypeFormId, "Audio/Visual");
        AddFormIdProperty(properties, "Acoustic Space", cell.AcousticSpaceFormId, "Audio/Visual");
        AddFormIdProperty(properties, "Image Space", cell.ImageSpaceFormId, "Audio/Visual");

        // Statistics
        AddCellStatistics(properties, cell);

        // Placed Objects (expandable by category)
        AddCellPlacedObjects(properties, cell);

        // Metadata
        properties.Add(new EsmPropertyEntry
            { Name = "File Offset", Value = $"0x{cell.Offset:X}", Category = "Metadata" });
        properties.Add(new EsmPropertyEntry
        {
            Name = "Endianness",
            Value = cell.IsBigEndian ? "Big-endian (Xbox 360)" : "Little-endian (PC)",
            Category = "Metadata"
        });

        return properties;
    }

    private static void AddFormIdProperty(
        List<EsmPropertyEntry> properties, string name, uint? formId, string category)
    {
        if (formId is > 0)
        {
            properties.Add(new EsmPropertyEntry
            {
                Name = name,
                Value = $"0x{formId.Value:X8}",
                Category = category,
                LinkedFormId = formId.Value
            });
        }
    }

    private static void AddCellStatistics(List<EsmPropertyEntry> properties, CellRecord cell)
    {
        properties.Add(new EsmPropertyEntry
            { Name = "Placed Objects", Value = cell.PlacedObjects.Count.ToString(), Category = "Statistics" });

        int refrCount = 0, achrCount = 0, acreCount = 0, markerCount = 0;
        foreach (var p in cell.PlacedObjects)
        {
            switch (p.RecordType)
            {
                case "REFR": refrCount++; break;
                case "ACHR": achrCount++; break;
                case "ACRE": acreCount++; break;
            }

            if (p.IsMapMarker)
            {
                markerCount++;
            }
        }

        if (refrCount > 0)
        {
            properties.Add(new EsmPropertyEntry
                { Name = "  REFR (Objects)", Value = refrCount.ToString(), Category = "Statistics" });
        }

        if (achrCount > 0)
        {
            properties.Add(new EsmPropertyEntry
                { Name = "  ACHR (NPCs)", Value = achrCount.ToString(), Category = "Statistics" });
        }

        if (acreCount > 0)
        {
            properties.Add(new EsmPropertyEntry
                { Name = "  ACRE (Creatures)", Value = acreCount.ToString(), Category = "Statistics" });
        }

        if (markerCount > 0)
        {
            properties.Add(new EsmPropertyEntry
                { Name = "  Map Markers", Value = markerCount.ToString(), Category = "Statistics" });
        }

        properties.Add(new EsmPropertyEntry
            { Name = "Has Heightmap", Value = cell.Heightmap != null ? "Yes" : "No", Category = "Statistics" });
    }

    private void AddCellPlacedObjects(List<EsmPropertyEntry> properties, CellRecord cell)
    {
        if (cell.LinkedCellFormIds.Count > 0)
        {
            properties.Add(new EsmPropertyEntry
            {
                Name = "Linked Cells",
                Value = cell.LinkedCellFormIds.Count.ToString(),
                Category = "Statistics",
                IsExpandable = true,
                SubItems = cell.LinkedCellFormIds.Select(formId =>
                {
                    var cellName = $"0x{formId:X8}";
                    if (_session.WorldViewData?.CellByFormId.TryGetValue(formId, out var linked) == true)
                    {
                        cellName = linked.EditorId ?? linked.FullName ?? cellName;
                    }

                    return new EsmPropertyEntry
                    {
                        Name = cellName,
                        Value = $"0x{formId:X8}",
                        Col1 = cellName,
                        Col3 = $"0x{formId:X8}",
                        CellNavigationFormId = formId
                    };
                }).ToList()
            });
        }

        if (cell.PlacedObjects.Count == 0)
        {
            return;
        }

        var grouped = cell.PlacedObjects
            .GroupBy(obj => GetPlacedObjectCategoryName(obj))
            .OrderBy(g => g.Key);

        foreach (var group in grouped)
        {
            properties.Add(new EsmPropertyEntry
            {
                Name = group.Key,
                Value = group.Count().ToString(),
                Category = "Placed Objects",
                IsExpandable = true,
                SubItems = group.Select(obj =>
                {
                    var baseName = obj.BaseEditorId
                                   ?? _session.Resolver?.GetBestName(obj.BaseFormId)
                                   ?? $"0x{obj.BaseFormId:X8}";
                    return new EsmPropertyEntry
                    {
                        Col1 = baseName,
                        Col3 = $"0x{obj.BaseFormId:X8}",
                        Col3FormId = obj.BaseFormId,
                        Name = baseName,
                        Value = $"0x{obj.BaseFormId:X8}"
                    };
                }).ToList()
            });
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

        // Show "View in Data Browser" for the base record
        ViewBaseInBrowserButton.Visibility = Visibility.Visible;
        var navigable = obj.BaseFormId > 0 && IsFormIdNavigable(obj.BaseFormId);
        ViewBaseInBrowserButton.IsEnabled = navigable;
        ToolTipService.SetToolTip(ViewBaseInBrowserButton, navigable
            ? "View the base record in the Data Browser"
            : "Base record not available in Data Browser (record type not reconstructed)");

        WorldMapControl?.SelectObject(obj);

        // Build display name: prefer marker name, then display name, then editor ID
        string displayName;
        if (obj.IsMapMarker && !string.IsNullOrEmpty(obj.MarkerName))
        {
            displayName = obj.MarkerName;
        }
        else
        {
            displayName = _session.Resolver?.GetDisplayName(obj.BaseFormId)
                          ?? obj.BaseEditorId
                          ?? _session.Resolver?.GetBestName(obj.BaseFormId)
                          ?? $"0x{obj.BaseFormId:X8}";
        }

        // Category-based prefix (not raw record type)
        string prefix;
        if (obj.IsMapMarker)
        {
            prefix = "Map Marker";
        }
        else if (obj.RecordType == "ACHR")
        {
            prefix = "NPC";
        }
        else if (obj.RecordType == "ACRE")
        {
            prefix = "Creature";
        }
        else if (_session.WorldViewData?.CategoryIndex.TryGetValue(obj.BaseFormId, out var titleCat) == true)
        {
            prefix = WorldMapControl.GetCategoryDisplayName(titleCat);
        }
        else
        {
            prefix = obj.RecordType;
        }

        WorldObjectTitle.Text = obj.IsInitiallyDisabled
            ? $"{prefix}: {displayName} [Disabled]"
            : $"{prefix}: {displayName}";

        WorldPropertyPanel.Children.Clear();
        BuildWorldPropertyPanel(BuildObjectProperties(obj));
    }

    private List<EsmPropertyEntry> BuildObjectProperties(PlacedReference obj)
    {
        var properties = new List<EsmPropertyEntry>();

        // Identity
        properties.Add(new EsmPropertyEntry
            { Name = "Form ID", Value = $"0x{obj.FormId:X8}", Category = "Identity" });
        properties.Add(new EsmPropertyEntry
        {
            Name = "Base Form ID",
            Value = $"0x{obj.BaseFormId:X8}",
            Category = "Identity",
            LinkedFormId = obj.BaseFormId
        });
        var baseEditorId = obj.BaseEditorId ?? _session.Resolver?.GetEditorId(obj.BaseFormId);
        if (!string.IsNullOrEmpty(baseEditorId))
        {
            properties.Add(new EsmPropertyEntry
                { Name = "Base Editor ID", Value = baseEditorId, Category = "Identity" });
        }

        var baseFullName = _session.Resolver?.GetDisplayName(obj.BaseFormId);
        if (!string.IsNullOrEmpty(baseFullName))
        {
            properties.Add(new EsmPropertyEntry
                { Name = "Base Name", Value = baseFullName, Category = "Identity" });
        }

        properties.Add(new EsmPropertyEntry
            { Name = "Record Type", Value = obj.RecordType, Category = "Identity" });

        // Category (from ObjectBoundsIndex or record type)
        if (_session.WorldViewData?.CategoryIndex.TryGetValue(obj.BaseFormId, out var objCategory) == true)
        {
            properties.Add(new EsmPropertyEntry
            {
                Name = "Category",
                Value = WorldMapControl.GetCategoryDisplayName(objCategory),
                Category = "Identity"
            });
        }
        else if (obj.IsMapMarker)
        {
            properties.Add(new EsmPropertyEntry
                { Name = "Category", Value = "Map Marker", Category = "Identity" });
        }
        else if (obj.RecordType == "ACHR")
        {
            properties.Add(new EsmPropertyEntry
                { Name = "Category", Value = "NPC", Category = "Identity" });
        }
        else if (obj.RecordType == "ACRE")
        {
            properties.Add(new EsmPropertyEntry
                { Name = "Category", Value = "Creature", Category = "Identity" });
        }

        if (obj.IsInitiallyDisabled)
        {
            properties.Add(new EsmPropertyEntry
                { Name = "Initial State", Value = "Initially Disabled", Category = "Identity" });
        }

        if (_session.WorldViewData?.RefrToCellIndex.TryGetValue(obj.FormId, out var parentCell) == true)
        {
            var cellName = parentCell.EditorId ?? parentCell.FullName ?? $"0x{parentCell.FormId:X8}";
            if (parentCell.GridX.HasValue && parentCell.GridY.HasValue)
            {
                cellName = $"[{parentCell.GridX.Value},{parentCell.GridY.Value}] {cellName}";
            }

            properties.Add(new EsmPropertyEntry
            {
                Name = "Parent Cell",
                Value = cellName,
                Category = "Identity",
                CellNavigationFormId = parentCell.FormId
            });
        }

        // Position
        properties.Add(new EsmPropertyEntry
            { Name = "Position", Value = $"({obj.X:F1}, {obj.Y:F1}, {obj.Z:F1})", Category = "Position" });
        properties.Add(new EsmPropertyEntry
            { Name = "Rotation", Value = $"({obj.RotX:F3}, {obj.RotY:F3}, {obj.RotZ:F3}) rad", Category = "Position" });
        if (Math.Abs(obj.Scale - 1.0f) > 0.001f)
        {
            properties.Add(new EsmPropertyEntry
                { Name = "Scale", Value = $"{obj.Scale:F3}", Category = "Position" });
        }

        if (_session.WorldViewData?.BoundsIndex.TryGetValue(obj.BaseFormId, out var bounds) == true)
        {
            properties.Add(new EsmPropertyEntry
                { Name = "Object Bounds", Value = bounds.ToString(), Category = "Position" });
        }

        // References
        AddFormIdProperty(properties, "Owner", obj.OwnerFormId, "References");
        AddFormIdProperty(properties, "Enable Parent", obj.EnableParentFormId, "References");
        AddFormIdProperty(properties, "Destination Door", obj.DestinationDoorFormId, "References");

        if (obj.DestinationCellFormId is > 0)
        {
            var cellName = $"0x{obj.DestinationCellFormId.Value:X8}";
            if (_session.WorldViewData?.CellByFormId.TryGetValue(obj.DestinationCellFormId.Value, out var destCell) ==
                true)
            {
                cellName = destCell.EditorId ?? destCell.FullName ?? cellName;
            }

            properties.Add(new EsmPropertyEntry
            {
                Name = "Destination Cell",
                Value = cellName,
                Category = "References",
                CellNavigationFormId = obj.DestinationCellFormId.Value
            });
        }

        // Map Marker
        if (obj.IsMapMarker)
        {
            if (!string.IsNullOrEmpty(obj.MarkerName))
            {
                properties.Add(new EsmPropertyEntry
                    { Name = "Marker Name", Value = obj.MarkerName, Category = "Map Marker" });
            }

            if (obj.MarkerType.HasValue)
            {
                properties.Add(new EsmPropertyEntry
                    { Name = "Marker Type", Value = obj.MarkerType.Value.ToString(), Category = "Map Marker" });
            }
        }

        // Spawn Info (for ACHR/ACRE placing leveled lists or direct actors)
        AddSpawnInfo(properties, obj);

        // Metadata
        properties.Add(new EsmPropertyEntry
            { Name = "File Offset", Value = $"0x{obj.Offset:X}", Category = "Metadata" });
        properties.Add(new EsmPropertyEntry
        {
            Name = "Endianness",
            Value = obj.IsBigEndian ? "Big-endian (Xbox 360)" : "Little-endian (PC)",
            Category = "Metadata"
        });

        return properties;
    }

    private void AddSpawnInfo(List<EsmPropertyEntry> properties, PlacedReference obj)
    {
        var spawnIndex = _session.WorldViewData?.SpawnIndex;
        if (spawnIndex == null)
        {
            return;
        }

        var isLeveled = spawnIndex.LeveledListTypes.ContainsKey(obj.BaseFormId);
        var isAchr = obj.RecordType == "ACHR";
        var isAcre = obj.RecordType == "ACRE";

        if (!isAchr && !isAcre)
        {
            return;
        }

        // Update title with [Leveled] prefix if applicable
        if (isLeveled)
        {
            var name = _session.Resolver?.GetDisplayName(obj.BaseFormId)
                       ?? obj.BaseEditorId
                       ?? _session.Resolver?.GetBestName(obj.BaseFormId)
                       ?? $"0x{obj.BaseFormId:X8}";
            var lvlPrefix = isAchr ? "NPC" : "Creature";
            WorldObjectTitle.Text = $"{lvlPrefix}: [Leveled] {name}";
        }

        // Resolve actors from leveled list or direct placement
        var actorFormIds = new List<uint>();
        if (isLeveled && spawnIndex.LeveledListEntries.TryGetValue(obj.BaseFormId, out var resolved))
        {
            actorFormIds.AddRange(resolved);

            // Show possible spawns
            var label = isAchr ? "Possible NPCs" : "Possible Creatures";
            var distinct = resolved.Distinct().ToList();
            var names = distinct.Select(fid =>
            {
                var n = _session.Resolver?.GetBestName(fid);
                return n ?? $"0x{fid:X8}";
            }).ToList();

            properties.Add(new EsmPropertyEntry
            {
                Name = label,
                Value = $"{distinct.Count} entries",
                Category = "Spawn Info",
                IsExpandable = true,
                SubItems = distinct.Select((fid, i) => new EsmPropertyEntry
                {
                    Name = names[i],
                    Value = $"0x{fid:X8}",
                    Col1 = names[i],
                    Col3 = $"0x{fid:X8}",
                    LinkedFormId = fid
                }).ToList()
            });
        }
        else
        {
            // Direct placement — the actor is the base form
            actorFormIds.Add(obj.BaseFormId);
        }

        // Collect AI package cells and refs from all resolved actors
        var packageCells = new List<uint>();
        var packageRefs = new List<PackageRefLocation>();
        foreach (var actorFid in actorFormIds.Distinct())
        {
            if (spawnIndex.ActorToPackageCells.TryGetValue(actorFid, out var cells))
            {
                packageCells.AddRange(cells);
            }

            if (spawnIndex.ActorToPackageRefs.TryGetValue(actorFid, out var refs))
            {
                packageRefs.AddRange(refs);
            }
        }

        // Show AI package cells
        if (packageCells.Count > 0)
        {
            var distinctCells = packageCells.Distinct().ToList();
            properties.Add(new EsmPropertyEntry
            {
                Name = "AI Package Cells",
                Value = $"{distinctCells.Count} cells",
                Category = "Spawn Info",
                IsExpandable = true,
                SubItems = distinctCells.Select(cellFid =>
                {
                    var cellName = _session.Resolver?.GetBestName(cellFid) ?? $"0x{cellFid:X8}";
                    return new EsmPropertyEntry
                    {
                        Name = cellName,
                        Value = $"0x{cellFid:X8}",
                        Col1 = cellName,
                        Col3 = $"0x{cellFid:X8}",
                        CellNavigationFormId = cellFid
                    };
                }).ToList()
            });
        }

        // Show AI package refs
        if (packageRefs.Count > 0)
        {
            var distinctRefs = packageRefs.DistinctBy(r => r.RefFormId).ToList();
            properties.Add(new EsmPropertyEntry
            {
                Name = "AI Package Refs",
                Value = $"{distinctRefs.Count} locations",
                Category = "Spawn Info",
                IsExpandable = true,
                SubItems = distinctRefs.Select(r =>
                {
                    var refName = _session.Resolver?.GetBestName(r.RefFormId) ?? $"0x{r.RefFormId:X8}";
                    var radiusStr = r.Radius > 0 ? $" (radius: {r.Radius})" : "";
                    return new EsmPropertyEntry
                    {
                        Name = refName,
                        Value = $"0x{r.RefFormId:X8}{radiusStr}",
                        Col1 = refName,
                        Col3 = $"0x{r.RefFormId:X8}{radiusStr}",
                        LinkedFormId = r.RefFormId
                    };
                }).ToList()
            });
        }
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
                // Expandable entry - header row
                mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                AddAlternatingRowBackground(mainGrid, currentRow, 3, propertyRowIndex, altRowBrush);

                var expandIcon = new TextBlock
                {
                    Text = "\u25B6",
                    FontSize = 10,
                    Width = 18,
                    Padding = new Thickness(4, 3, 0, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground =
                        (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
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
                    Foreground =
                        (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                };
                Grid.SetRow(countText, currentRow);
                Grid.SetColumn(countText, 2);
                mainGrid.Children.Add(countText);

                currentRow++;

                // Sub-items grid (collapsible)
                var subItemsGrid = new Grid { Visibility = Visibility.Collapsed };
                subItemsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                subItemsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                subItemsGrid.ColumnDefinitions.Add(
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var subRow = 0;
                foreach (var sub in prop.SubItems)
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
            }
            else
            {
                // Normal property row
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
                currentRow++;
            }
        }

        WorldPropertyPanel.Children.Add(mainGrid);
    }

    private string GetPlacedObjectCategoryName(PlacedReference obj)
    {
        if (obj.IsMapMarker)
        {
            return "Map Markers";
        }

        if (obj.RecordType == "ACHR")
        {
            return "NPCs";
        }

        if (obj.RecordType == "ACRE")
        {
            return "Creatures";
        }

        if (_session.WorldViewData?.CategoryIndex.TryGetValue(obj.BaseFormId, out var category) == true)
        {
            return category switch
            {
                PlacedObjectCategory.Static => "Statics",
                PlacedObjectCategory.Architecture => "Architecture",
                PlacedObjectCategory.Landscape => "Landscape",
                PlacedObjectCategory.Plants => "Plants",
                PlacedObjectCategory.Clutter => "Clutter",
                PlacedObjectCategory.Dungeon => "Dungeon",
                PlacedObjectCategory.Effects => "Effects",
                PlacedObjectCategory.Vehicles => "Vehicles",
                PlacedObjectCategory.Traps => "Traps",
                PlacedObjectCategory.Door => "Doors",
                PlacedObjectCategory.Activator => "Activators",
                PlacedObjectCategory.Light => "Lights",
                PlacedObjectCategory.Furniture => "Furniture",
                PlacedObjectCategory.Npc => "NPCs",
                PlacedObjectCategory.Creature => "Creatures",
                PlacedObjectCategory.Container => "Containers",
                PlacedObjectCategory.Item => "Items",
                _ => "Other"
            };
        }

        return "Other";
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

    private void ResetWorldMap()
    {
        _selectedWorldCell = null;
        _selectedWorldObject = null;
        ViewBaseInBrowserButton.Visibility = Visibility.Collapsed;
        WorldMapPlaceholder.Visibility = Visibility.Visible;
        WorldMapProgressBar.Visibility = Visibility.Collapsed;
        WorldMapStatusText.Text = Strings.Empty_RunAnalysisForWorldMap;
        WorldMapContent.Visibility = Visibility.Collapsed;
        WorldMapControl?.Reset();
    }
}
