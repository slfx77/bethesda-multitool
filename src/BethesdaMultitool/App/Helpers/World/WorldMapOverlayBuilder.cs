using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm;
using BethesdaMultitool.Core.Formats.Esm.Export;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.Formats.SaveGame.Models;
using BethesdaMultitool.Core.Formats.SaveGame;
using BethesdaMultitool.Core.Formats.SpeedTree;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool;

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
        var modelPathIndex = ObjectBoundsIndex.BuildModelPathIndex(semantic);

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
        var usageIndex = FormUsageIndex.Build(semantic);
        var (moonPrimarySize, moonSecondarySize) = ComputeMoonSizes(semantic);

        return new WorldViewData
        {
            Worldspaces = semantic.Worldspaces,
            InteriorCells = semantic.Cells.Where(c => c.IsInterior).ToList(),
            UnlinkedExteriorCells = unlinkedExterior,
            UnlinkedMapMarkers = unlinkedMarkers,
            AllCells = semantic.Cells,
            CellWorldSize = ResolveCellWorldSize(semantic.Cells),
            CellByFormId = cellByFormId,
            RefrToCellIndex = refrToCellIndex,
            BoundsIndex = boundsIndex,
            ModelPathIndex = modelPathIndex,
            SpeedTreeHeights = BuildSpeedTreeHeights(semantic),
            SpeedTreeLeafTextures = BuildSpeedTreeLeafTextures(semantic),
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
            Game = DetectGame(sourceFilePath),
            MoonPrimaryHalfSizeFraction = moonPrimarySize,
            MoonSecondaryHalfSizeFraction = moonSecondarySize,
            SpawnIndex = spawnIndex,
            UsageIndex = usageIndex,
            RefPositionIndex = refPositionIndex,
            DanglingRefs = DanglingRefAttributions.LoadDefault(),
            NavMeshesByCell = BuildNavMeshIndex(semantic.NavMeshes, semantic.Cells),
            LandTexturesByFormId = BuildLandTextureIndex(semantic.LandTextures),
            TextureSetsByFormId = BuildTextureSetIndex(semantic.TextureSets),
            WatersByFormId = BuildWaterIndex(semantic.Water),
            WeathersByFormId = BuildWeatherIndex(semantic.Weather),
            ClimatesByFormId = BuildClimateIndex(semantic.Climate),
            AllWeathers = BuildAllWeathers(semantic.Weather)
        };
    }

    /// <summary>
    ///     Detects which Bethesda game a source file belongs to, for game-specific rendering (e.g. the
    ///     engine-default landscape texture). Delegates to the shared <see cref="GameDetector" />, which
    ///     does the structural plugin probe plus master-list/filename refinement.
    /// </summary>
    private static BethesdaGame DetectGame(string? sourceFilePath)
        => GameDetector.DetectFromFile(sourceFilePath).Game;

    // Engine-exact moon-disc sizes for the loaded game, read from its GMSTs: iMasserSize / iSecundaSize
    // (the ±size billboard quad half-extent) ÷ fSunXExtreme (the sky-dome horizontal radius), as a fraction
    // of the billboard radius. This is the engine's exact apparent-size model (decompiled from FNV Moon.cpp
    // + Skyrim TESV Moon::Initialize); because they're GMSTs the values vary per game/mod (FNV 85 / dome 800,
    // Skyrim 90 / dome 400), so reading them here makes the moon exact and mod-aware. Either fraction is null
    // when its GMSTs are absent (e.g. Morrowind TES3) → the viewer uses the per-game SkyMoonProfile default.
    private static (float? Primary, float? Secondary) ComputeMoonSizes(RecordCollection records)
    {
        int? GmstInt(string id) => records.GameSettings
            .FirstOrDefault(g => string.Equals(g.EditorId, id, StringComparison.OrdinalIgnoreCase))?.IntValue;
        float? GmstFloat(string id) => records.GameSettings
            .FirstOrDefault(g => string.Equals(g.EditorId, id, StringComparison.OrdinalIgnoreCase))?.FloatValue;

        var sunXExtreme = GmstFloat("fSunXExtreme");
        return (SkyMoonProfile.FractionFromGmst(GmstInt("iMasserSize"), sunXExtreme),
                SkyMoonProfile.FractionFromGmst(GmstInt("iSecundaSize"), sunXExtreme));
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
            UsageIndex = null,
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
        var modelPathIndex = ObjectBoundsIndex.BuildModelPathIndex(suppRecords);

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
        var usageIndex = FormUsageIndex.Build(suppRecords);
        var (moonPrimarySize, moonSecondarySize) = ComputeMoonSizes(suppRecords);

        return new WorldViewData
        {
            Worldspaces = suppRecords.Worldspaces,
            InteriorCells = suppRecords.Cells.Where(c => c.IsInterior).ToList(),
            UnlinkedExteriorCells = unlinkedExterior,
            UnlinkedMapMarkers = unlinkedMarkers,
            AllCells = suppRecords.Cells,
            CellWorldSize = ResolveCellWorldSize(suppRecords.Cells),
            CellByFormId = cellByFormId,
            RefrToCellIndex = refrToCellIndex,
            BoundsIndex = boundsIndex,
            ModelPathIndex = modelPathIndex,
            SpeedTreeHeights = BuildSpeedTreeHeights(suppRecords),
            SpeedTreeLeafTextures = BuildSpeedTreeLeafTextures(suppRecords),
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
            MoonPrimaryHalfSizeFraction = moonPrimarySize,
            MoonSecondaryHalfSizeFraction = moonSecondarySize,
            SpawnIndex = spawnIndex,
            UsageIndex = usageIndex,
            RefPositionIndex = refPositionIndex,
            SaveOverlayMarkers = overlayMarkers,
            PlayerPosition = playerPos,
            DanglingRefs = DanglingRefAttributions.LoadDefault(),
            NavMeshesByCell = BuildNavMeshIndex(suppRecords.NavMeshes, suppRecords.Cells),
            LandTexturesByFormId = BuildLandTextureIndex(suppRecords.LandTextures),
            TextureSetsByFormId = BuildTextureSetIndex(suppRecords.TextureSets),
            WatersByFormId = BuildWaterIndex(suppRecords.Water),
            WeathersByFormId = BuildWeatherIndex(suppRecords.Weather),
            ClimatesByFormId = BuildClimateIndex(suppRecords.Climate),
            AllWeathers = BuildAllWeathers(suppRecords.Weather)
        };
    }

    /// <summary>
    ///     Map each SpeedTree <c>.spt</c> archive path to its recorded height (TREE record OBND Z-extent)
    ///     so the procedural generator can size trees from the ESM rather than a constant.
    /// </summary>
    private static Dictionary<string, float> BuildSpeedTreeHeights(RecordCollection semantic)
    {
        var map = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in semantic.GenericRecords)
        {
            if (record.ModelPath is not { } modelPath || !SpeedTreeModelPath.IsSpt(modelPath))
            {
                continue;
            }

            // FO3/FNV: the TREE record's OBND Z-extent. Oblivion (TES4) records predate OBND, so fall
            // back to the BNAM billboard Height (the tree's rendered height), then the MODB bound radius.
            // Without this, Oblivion trees keep their tiny built scale and render far too small.
            var height = record.Bounds is { } bounds ? bounds.Z2 - bounds.Z1 : 0f;

            if (height <= 0f && record.Fields.TryGetValue("BNAM", out var bnam) &&
                bnam is System.Collections.IDictionary bd && bd["Height"] is float bh)
            {
                height = bh;
            }

            if (height <= 0f && record.Fields.TryGetValue("MODB", out var modb) &&
                modb is byte[] { Length: >= 4 } mb)
            {
                height = BitConverter.ToSingle(mb, 0);
            }

            if (height > 0f)
            {
                map[SpeedTreeModelPath.ToArchivePath(modelPath)] = height;
            }
        }

        return map;
    }

    /// <summary>
    ///     Map each SpeedTree <c>.spt</c> archive path → the leaf atlas the engine actually applies: the
    ///     <c>TREE</c> record's <c>ICON</c> field (the `.spt`'s own leaf material is a dev-era path that
    ///     often never shipped — e.g. WhiteOak's `treewoakleaves01b` vs the shipped `WhiteOakLeaves01.dds`).
    /// </summary>
    private static Dictionary<string, string> BuildSpeedTreeLeafTextures(RecordCollection semantic)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in semantic.GenericRecords)
        {
            if (record.ModelPath is not { } modelPath || !SpeedTreeModelPath.IsSpt(modelPath) ||
                !record.Fields.TryGetValue("ICON", out var iconObj) || iconObj is not string icon)
            {
                continue;
            }

            if (SpeedTreeTexturePath.IconToLeafPath(icon) is { } leaf)
            {
                map[SpeedTreeModelPath.ToArchivePath(modelPath)] = leaf;
            }
        }

        return map;
    }

    private static Dictionary<uint, List<NavMeshRecord>> BuildNavMeshIndex(
        List<NavMeshRecord> navMeshes, List<CellRecord> cells)
    {
        // Skyrim EXTERIOR navmeshes (NVNM) identify their cell by worldspace + grid, not a direct cell
        // FormID. Build a (worldspace, gridX, gridY) → cell FormID lookup so they can be keyed by cell
        // like every other game; interior navmeshes already carry a CellFormID.
        Dictionary<(uint Worldspace, int X, int Y), uint>? exteriorCells = null;
        foreach (var c in cells)
        {
            if (c.WorldspaceFormId is { } ws and not 0 && c.GridX is { } gx && c.GridY is { } gy)
            {
                (exteriorCells ??= new Dictionary<(uint, int, int), uint>()).TryAdd((ws, gx, gy), c.FormId);
            }
        }

        var dict = new Dictionary<uint, List<NavMeshRecord>>();
        foreach (var nm in navMeshes)
        {
            var cellFormId = nm.CellFormId;
            if (cellFormId == 0 && nm.WorldspaceFormId != 0 && nm.GridX is { } ngx && nm.GridY is { } ngy &&
                exteriorCells is not null &&
                exteriorCells.TryGetValue((nm.WorldspaceFormId, ngx, ngy), out var resolved))
            {
                cellFormId = resolved;
            }

            if (cellFormId == 0) continue;
            if (!dict.TryGetValue(cellFormId, out var list))
            {
                list = new List<NavMeshRecord>();
                dict[cellFormId] = list;
            }
            list.Add(nm);
        }
        return dict;
    }

    private static Dictionary<uint, LandscapeTextureRecord> BuildLandTextureIndex(
        List<LandscapeTextureRecord> records)
    {
        var dict = new Dictionary<uint, LandscapeTextureRecord>(records.Count);
        foreach (var r in records)
        {
            dict.TryAdd(r.FormId, r);
        }
        return dict;
    }

    private static Dictionary<uint, TextureSetRecord> BuildTextureSetIndex(
        List<TextureSetRecord> records)
    {
        var dict = new Dictionary<uint, TextureSetRecord>(records.Count);
        foreach (var r in records)
        {
            dict.TryAdd(r.FormId, r);
        }
        return dict;
    }

    private static Dictionary<uint, WaterRecord> BuildWaterIndex(List<WaterRecord> records)
    {
        var dict = new Dictionary<uint, WaterRecord>(records.Count);
        foreach (var r in records)
        {
            dict.TryAdd(r.FormId, r);
        }
        return dict;
    }

    private static Dictionary<uint, WeatherRecord> BuildWeatherIndex(List<WeatherRecord> records)
    {
        var dict = new Dictionary<uint, WeatherRecord>(records.Count);
        foreach (var r in records)
        {
            dict.TryAdd(r.FormId, r);
        }
        return dict;
    }

    private static Dictionary<uint, ClimateRecord> BuildClimateIndex(List<ClimateRecord> records)
    {
        var dict = new Dictionary<uint, ClimateRecord>(records.Count);
        foreach (var r in records)
        {
            dict.TryAdd(r.FormId, r);
        }
        return dict;
    }

    private static List<WeatherRecord> BuildAllWeathers(List<WeatherRecord> records) =>
        records.OrderBy(r => r.EditorId ?? $"0x{r.FormId:X8}", StringComparer.OrdinalIgnoreCase).ToList();

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
                BaseFormId = resolver.GetBaseFormId(resolvedFormId) ?? resolvedFormId,
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

    // Group map markers by owning worldspace, folding in markers inherited from "Use Map Data" child
    // worldspaces. Pure record computation, so it lives in Core (WorldspaceMarkerGrouping) where it is
    // headless-unit-testable; this WinUI builder just delegates.
    private static Dictionary<uint, List<PlacedReference>> GroupMarkersByWorldspace(
        List<WorldspaceRecord> worldspaces)
        => WorldspaceMarkerGrouping.GroupByWorldspace(worldspaces);

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

    /// <summary>
    ///     The worldspace's exterior cell-edge size in world units. All cells of one worldspace share
    ///     it, so the first cell that declares a non-default <see cref="CellRecord.CellWorldSize" />
    ///     (8192 for Morrowind) wins; everything else falls back to the Fallout-family 4096.
    /// </summary>
    private static float ResolveCellWorldSize(IEnumerable<CellRecord> cells)
    {
        foreach (var cell in cells)
        {
            if (cell.CellWorldSize > 0f)
            {
                return cell.CellWorldSize;
            }
        }

        return WorldGridConstants.CellSize;
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

