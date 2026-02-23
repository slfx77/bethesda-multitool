using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.SaveGame;

namespace FalloutXbox360Utils;

/// <summary>
///     Builds <see cref="WorldViewData" /> from either a <see cref="RecordCollection" /> (ESM)
///     or from a save file's changed forms.  All methods are pure computation with no UI dependencies.
/// </summary>
internal static class WorldMapOverlayBuilder
{
    /// <summary>
    ///     Build full <see cref="WorldViewData" /> from a semantic record collection (ESM or DMP).
    /// </summary>
    public static WorldViewData BuildFromRecords(RecordCollection semantic, string? sourceFilePath)
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
            var result = HeightmapRenderer.ComputeHeightmapData(
                semantic.Worldspaces[0].Cells, defaultWaterHeight);
            if (result.HasValue)
            {
                (hmGrayscale, hmWaterMask, hmWidth, hmHeight, hmMinX, hmMaxY) = result.Value;
            }
        }

        // Group map markers by worldspace using cell ownership (GRUP-based, not coordinates)
        var markersByWorldspace = GroupMarkersByWorldspace(semantic.Worldspaces);

        // Find exterior cells with grid coords but no worldspace linkage (common in DMP files)
        var linkedCellFormIds = CollectLinkedCellFormIds(semantic.Worldspaces);

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
        var cellByFormId = BuildCellByFormId(semantic.Cells);

        // Build reverse index: placed reference FormID -> parent cell
        var (refrToCellIndex, refPositionIndex) = BuildRefrIndices(semantic.Cells);

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
            SourceFilePath = sourceFilePath,
            SpawnIndex = spawnIndex,
            RefPositionIndex = refPositionIndex
        };
    }

    /// <summary>
    ///     Build <see cref="WorldViewData" /> from a save file, optionally enriched with a supplementary ESM.
    /// </summary>
    public static WorldViewData BuildFromSave(
        SaveFile save,
        RecordCollection? suppRecords,
        FormIdResolver resolver,
        string? supplementaryEsmPath)
    {
        var formIdArray = save.FormIdArray.ToArray();

        // Build save overlay markers from changed forms with positions
        var overlayMarkers = BuildSaveOverlayMarkers(save, formIdArray, resolver);

        // Player position
        (float X, float Y, float Z)? playerPos = save.PlayerLocation != null
            ? (save.PlayerLocation.PosX, save.PlayerLocation.PosY, save.PlayerLocation.PosZ)
            : null;

        if (suppRecords != null)
        {
            return BuildFromSaveWithEsm(
                suppRecords, resolver, supplementaryEsmPath, overlayMarkers, playerPos);
        }

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

    private static WorldViewData BuildFromSaveWithEsm(
        RecordCollection suppRecords,
        FormIdResolver resolver,
        string? supplementaryEsmPath,
        List<PlacedReference> overlayMarkers,
        (float X, float Y, float Z)? playerPos)
    {
        var (boundsIndex, categoryIndex) = ObjectBoundsIndex.BuildCombined(suppRecords);

        byte[]? hmGrayscale = null;
        byte[]? hmWaterMask = null;
        int hmWidth = 0, hmHeight = 0, hmMinX = 0, hmMaxY = 0;
        float? defaultWaterHeight = null;
        if (suppRecords.Worldspaces.Count > 0 && suppRecords.Worldspaces[0].Cells.Count > 0)
        {
            defaultWaterHeight = suppRecords.Worldspaces[0].DefaultWaterHeight;
            var hmResult = HeightmapRenderer.ComputeHeightmapData(
                suppRecords.Worldspaces[0].Cells, defaultWaterHeight);
            if (hmResult.HasValue)
            {
                (hmGrayscale, hmWaterMask, hmWidth, hmHeight, hmMinX, hmMaxY) = hmResult.Value;
            }
        }

        var markersByWorldspace = GroupMarkersByWorldspace(suppRecords.Worldspaces);
        var linkedCellFormIds = CollectLinkedCellFormIds(suppRecords.Worldspaces);

        var unlinkedExterior = suppRecords.Cells
            .Where(c => !c.IsInterior && c.GridX.HasValue && c.GridY.HasValue &&
                        !linkedCellFormIds.Contains(c.FormId))
            .ToList();

        var linkedMarkerFormIds = new HashSet<uint>(
            markersByWorldspace.Values.SelectMany(m => m).Select(m => m.FormId));
        var unlinkedMarkers = suppRecords.MapMarkers
            .Where(m => !linkedMarkerFormIds.Contains(m.FormId))
            .ToList();

        var cellByFormId = BuildCellByFormId(suppRecords.Cells);
        var (refrToCellIndex, refPositionIndex) = BuildRefrIndices(suppRecords.Cells);
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
            SourceFilePath = supplementaryEsmPath,
            SpawnIndex = spawnIndex,
            RefPositionIndex = refPositionIndex,
            SaveOverlayMarkers = overlayMarkers,
            PlayerPosition = playerPos
        };
    }

    private static List<PlacedReference> BuildSaveOverlayMarkers(
        SaveFile save, uint[] formIdArray, FormIdResolver resolver)
    {
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

        return overlayMarkers;
    }

    // -- Shared helpers for both ESM and save+ESM paths --

    private static Dictionary<uint, List<PlacedReference>> GroupMarkersByWorldspace(
        List<WorldspaceRecord> worldspaces)
    {
        var markersByWorldspace = new Dictionary<uint, List<PlacedReference>>();
        foreach (var ws in worldspaces)
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

        return markersByWorldspace;
    }

    private static HashSet<uint> CollectLinkedCellFormIds(List<WorldspaceRecord> worldspaces)
    {
        var linkedCellFormIds = new HashSet<uint>();
        foreach (var ws in worldspaces)
        {
            foreach (var cell in ws.Cells)
            {
                linkedCellFormIds.Add(cell.FormId);
            }
        }

        return linkedCellFormIds;
    }

    private static Dictionary<uint, CellRecord> BuildCellByFormId(List<CellRecord> cells)
    {
        var cellByFormId = new Dictionary<uint, CellRecord>();
        foreach (var cell in cells)
        {
            cellByFormId.TryAdd(cell.FormId, cell);
        }

        return cellByFormId;
    }

    private static (Dictionary<uint, CellRecord> RefrToCell, Dictionary<uint, (float X, float Y)> RefPosition)
        BuildRefrIndices(List<CellRecord> cells)
    {
        var refrToCellIndex = new Dictionary<uint, CellRecord>();
        var refPositionIndex = new Dictionary<uint, (float X, float Y)>();
        foreach (var cell in cells)
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

        return (refrToCellIndex, refPositionIndex);
    }
}
