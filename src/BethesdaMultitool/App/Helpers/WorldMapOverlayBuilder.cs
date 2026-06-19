using BethesdaMultitool.Core.Formats.Esm;
using BethesdaMultitool.Core.Formats.Esm.Export;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.SaveGame;
using BethesdaMultitool.Core.Formats.SpeedTree;

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
    ///     Detects which Bethesda game a source file belongs to, for game-specific rendering (sky
    ///     textures, moon count). Covers the whole Gamebryo/Creation lineage: Morrowind, Oblivion,
    ///     Fallout 3, Fallout: New Vegas, Skyrim, Fallout 4, Fallout 76, Starfield. Uses the structural
    ///     plugin probe (unambiguous for Morrowind / Oblivion), then — since the 24-byte TES4 framing is
    ///     shared by FO3 / FNV / Skyrim / FO4 / FO76 / Starfield and the HEDR version float overlaps
    ///     between them — refines via the master list + source filename. Reads only the file's leading
    ///     bytes (the header sits at the start). Returns Unknown on any failure.
    /// </summary>
    private static BethesdaGame DetectGame(string? sourceFilePath)
    {
        if (string.IsNullOrEmpty(sourceFilePath) || !File.Exists(sourceFilePath))
        {
            return BethesdaGame.Unknown;
        }

        try
        {
            byte[] head;
            using (var fs = File.OpenRead(sourceFilePath))
            {
                var len = (int)Math.Min(64 * 1024, fs.Length);
                head = new byte[len];
                fs.ReadExactly(head, 0, len);
            }

            var format = PluginFormat.Detect(head);
            if (format.Game is BethesdaGame.Morrowind or BethesdaGame.Oblivion)
            {
                return format.Game; // structurally unambiguous
            }

            // 24-byte TES4 family — refine by the master names + the source filename.
            var names = (EsmParser.ParseFileHeader(head)?.Masters ?? [])
                .Append(Path.GetFileName(sourceFilePath));
            foreach (var name in names)
            {
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                // Newest / most-specific first. FO76's main master is SeventySix.esm (some packs name it
                // Fallout76); Starfield's is Starfield.esm.
                if (name.Contains("Starfield", StringComparison.OrdinalIgnoreCase)) return BethesdaGame.Starfield;
                if (name.Contains("SeventySix", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Fallout76", StringComparison.OrdinalIgnoreCase)) return BethesdaGame.Fallout76;
                if (name.Contains("Skyrim", StringComparison.OrdinalIgnoreCase)) return BethesdaGame.Skyrim;
                if (name.Contains("Fallout4", StringComparison.OrdinalIgnoreCase)) return BethesdaGame.Fallout4;
                if (name.Contains("Oblivion", StringComparison.OrdinalIgnoreCase)) return BethesdaGame.Oblivion;
                if (name.Contains("Fallout3", StringComparison.OrdinalIgnoreCase)) return BethesdaGame.Fallout3;
                if (name.Contains("FalloutNV", StringComparison.OrdinalIgnoreCase)) return BethesdaGame.FalloutNewVegas;
            }

            return format.Game; // structural default (FNV for the 24-byte family)
        }
        catch
        {
            return BethesdaGame.Unknown;
        }
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
