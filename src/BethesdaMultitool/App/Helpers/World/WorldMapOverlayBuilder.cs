using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm;
using BethesdaMultitool.Core.Formats.Esm.Export;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Atmosphere;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Lighting;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Scene;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
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
        var game = DetectGame(sourceFilePath);

        // Morrowind authors its entire weather/sky model in Morrowind.ini rather than WTHR/CLMT
        // records — synthesize record equivalents from the install's INI (vanilla-Clear fallback)
        // so the atmosphere, the weather picker, and captures work through the same plumbing as
        // every other game. See docs/research/morrowind_atmosphere_water_model.md.
        var weatherRecords = semantic.Weather;
        var climateRecords = semantic.Climate;
        if (game == BethesdaGame.Morrowind && weatherRecords.Count == 0)
        {
            var (mwWeathers, mwClimate) =
                Core.Formats.Tes3.MorrowindWeatherIni.SynthesizeFromInstall(sourceFilePath);
            weatherRecords = mwWeathers;
            climateRecords = [mwClimate];
        }

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
        var markersByWorldspace = GroupMarkersByWorldspace(semantic.Worldspaces, game);

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

        // One shared placement index (FormID -> ref + parent cell) serves the ref->cell and
        // ref->position lookups AND the XESP/day-night resolvers below — previously four separate
        // full-population dictionaries (5.1M entries each on FO76).
        var placedRefs = PlacedRefIndex.Build(semantic.Cells);

        // Build spawn resolution index
        var spawnIndex = SpawnResolutionIndex.Build(semantic);
        var usageIndex = FormUsageIndex.Build(semantic);
        var (moonPrimarySize, moonSecondarySize) = ComputeMoonSizes(semantic, game);
        var gameSettingsByEditorId = BuildGameSettingIndex(semantic.GameSettings);
        var textureSetsByFormId = BuildTextureSetIndex(semantic.TextureSets);

        return new WorldViewData
        {
            Worldspaces = semantic.Worldspaces,
            InteriorCells = semantic.Cells.Where(c => c.IsInterior).ToList(),
            UnlinkedExteriorCells = unlinkedExterior,
            UnlinkedMapMarkers = unlinkedMarkers,
            AllCells = semantic.Cells,
            XespDisabledRefs = PlacedReferenceEnableStateResolver.ResolveXespDisabledRefs(placedRefs),
            DayNightSchedule = Core.WorldData.DayNight.DayNightRefSchedule.Build(
                semantic.Scripts, semantic.Cells, semantic.Activators, semantic.Lights, placedRefs),
            CellWorldSize = ResolveCellWorldSize(semantic.Cells),
            CellByFormId = cellByFormId,
            PlacedRefs = placedRefs,
            BoundsIndex = boundsIndex,
            ModelPathIndex = modelPathIndex,
            SpeedTreeLeafTextures = BuildSpeedTreeLeafTextures(semantic),
            SpeedTreeDimming = BuildSpeedTreeDimming(semantic),
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
            Game = game,
            GameSettingsByEditorId = gameSettingsByEditorId,
            MoonPrimaryHalfSizeFraction = moonPrimarySize,
            MoonSecondaryHalfSizeFraction = moonSecondarySize,
            SpawnIndex = spawnIndex,
            UsageIndex = usageIndex,
            DanglingRefs = DanglingRefAttributions.LoadDefault(),
            NavMeshesByCell = BuildNavMeshIndex(semantic.NavMeshes, semantic.Cells),
            LandTexturesByFormId = BuildLandTextureIndex(semantic.LandTextures),
            GrassesByFormId = BuildGrassIndex(semantic.Grasses),
            TextureSetsByFormId = textureSetsByFormId,
            AlternateTexturesByFormId = BuildAlternateTextureIndex(
                semantic.AlternateTexturesByFormId, textureSetsByFormId),
            MaterialSwapsByFormId = BuildMaterialSwapIndex(semantic.MaterialSwaps),
            BaseMaterialSwapsByFormId = semantic.BaseMaterialSwapFormIds,
            BaseColorRemapsByFormId = semantic.BaseColorRemapIndices,
            WatersByFormId = BuildWaterIndex(semantic.Water),
            WeathersByFormId = BuildWeatherIndex(weatherRecords),
            RegionsByFormId = BuildRegionIndex(semantic.Regions),
            RuntimeWeatherTransition = semantic.RuntimeWeatherTransition,
            ClimatesByFormId = BuildClimateIndex(climateRecords),
            ImageSpacesByFormId = BuildImageSpaceIndex(semantic.ImageSpaces),
            ImageSpaceModifiersByFormId = BuildImageSpaceModifierIndex(semantic.ImageSpaceModifiers),
            LightingTemplatesByFormId = BuildLightingTemplateIndex(semantic.LightingTemplates),
            LightsByFormId = BuildLightIndex(semantic.Lights),
            ExternalEmittanceColorsByFormId = ExternalEmittanceResolver.BuildIndex(
                semantic.Regions, semantic.Lights),
            AllWeathers = BuildAllWeathers(weatherRecords)
        };
    }

    /// <summary>
    ///     Detects which Bethesda game a source file belongs to, for game-specific rendering (e.g. the
    ///     engine-default landscape texture). Delegates to the shared <see cref="GameDetector" />, which
    ///     does the structural plugin probe plus master-list/filename refinement.
    /// </summary>
    private static BethesdaGame DetectGame(string? sourceFilePath)
        => GameDetector.DetectFromFile(sourceFilePath).Game;

    // Moon-disc sizes for the loaded game, read from iMasserSize/iSecundaSize. Recovered FNV and Skyrim
    // Moon::Initialize implementations place their ±size billboard quad on a fixed 512-unit arm; FO4's
    // separate triangle family uses fSunXExtreme as its path radius. The profile owns that distinction so
    // the loaded GMST remains mod-aware without incorrectly coupling the classic arm to the sun path.
    private static (float? Primary, float? Secondary) ComputeMoonSizes(
        RecordCollection records, BethesdaGame game)
    {
        int? GmstInt(string id) => records.GameSettings
            .FirstOrDefault(g => string.Equals(g.EditorId, id, StringComparison.OrdinalIgnoreCase))?.IntValue;
        float? GmstFloat(string id) => records.GameSettings
            .FirstOrDefault(g => string.Equals(g.EditorId, id, StringComparison.OrdinalIgnoreCase))?.FloatValue;

        var profile = SkyMoonProfile.ForGame(game);

        // Keep the existing 800-unit calibration only for unrecovered legacy families such as Oblivion
        // when fSunXExtreme is absent. Recovered rotated arms ignore it; modern paths must fall back to
        // their profile rather than silently borrowing a classic radius.
        const float fallbackDomeRadius = 800f;
        var dome = GmstFloat("fSunXExtreme");
        if (dome is null && profile.PathFamily == MoonPathFamily.CalibratedTesOrbit)
        {
            dome = fallbackDomeRadius;
        }

        var masser = GmstInt("iMasserSize");
        var secunda = GmstInt("iSecundaSize") ?? (masser is int m ? (int)(m * 0.55f) : null);
        return (profile.HalfSizeFractionFromGmst(masser, dome),
                profile.HalfSizeFractionFromGmst(secunda, dome));
    }

    internal static Dictionary<string, GameSettingRecord> BuildGameSettingIndex(
        IReadOnlyList<GameSettingRecord> settings) => GameSettingRegistry.BuildIndex(settings);

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
            PlacedRefs = PlacedRefIndex.Empty,
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
        var game = DetectGame(supplementaryEsmPath);
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

        var markersByWorldspace = GroupMarkersByWorldspace(suppRecords.Worldspaces, game);
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
        var placedRefs = PlacedRefIndex.Build(suppRecords.Cells);
        var spawnIndex = SpawnResolutionIndex.Build(suppRecords);
        var usageIndex = FormUsageIndex.Build(suppRecords);
        var (moonPrimarySize, moonSecondarySize) = ComputeMoonSizes(suppRecords, game);
        var gameSettingsByEditorId = BuildGameSettingIndex(suppRecords.GameSettings);
        var textureSetsByFormId = BuildTextureSetIndex(suppRecords.TextureSets);

        return new WorldViewData
        {
            Worldspaces = suppRecords.Worldspaces,
            InteriorCells = suppRecords.Cells.Where(c => c.IsInterior).ToList(),
            UnlinkedExteriorCells = unlinkedExterior,
            UnlinkedMapMarkers = unlinkedMarkers,
            AllCells = suppRecords.Cells,
            XespDisabledRefs = PlacedReferenceEnableStateResolver.ResolveXespDisabledRefs(placedRefs),
            CellWorldSize = ResolveCellWorldSize(suppRecords.Cells),
            CellByFormId = cellByFormId,
            PlacedRefs = placedRefs,
            BoundsIndex = boundsIndex,
            ModelPathIndex = modelPathIndex,
            SpeedTreeLeafTextures = BuildSpeedTreeLeafTextures(suppRecords),
            SpeedTreeDimming = BuildSpeedTreeDimming(suppRecords),
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
            Game = game,
            GameSettingsByEditorId = gameSettingsByEditorId,
            MoonPrimaryHalfSizeFraction = moonPrimarySize,
            MoonSecondaryHalfSizeFraction = moonSecondarySize,
            SpawnIndex = spawnIndex,
            UsageIndex = usageIndex,
            SaveOverlayMarkers = overlayMarkers,
            PlayerPosition = playerPos,
            DanglingRefs = DanglingRefAttributions.LoadDefault(),
            NavMeshesByCell = BuildNavMeshIndex(suppRecords.NavMeshes, suppRecords.Cells),
            LandTexturesByFormId = BuildLandTextureIndex(suppRecords.LandTextures),
            GrassesByFormId = BuildGrassIndex(suppRecords.Grasses),
            TextureSetsByFormId = textureSetsByFormId,
            AlternateTexturesByFormId = BuildAlternateTextureIndex(
                suppRecords.AlternateTexturesByFormId, textureSetsByFormId),
            MaterialSwapsByFormId = BuildMaterialSwapIndex(suppRecords.MaterialSwaps),
            BaseMaterialSwapsByFormId = suppRecords.BaseMaterialSwapFormIds,
            BaseColorRemapsByFormId = suppRecords.BaseColorRemapIndices,
            WatersByFormId = BuildWaterIndex(suppRecords.Water),
            WeathersByFormId = BuildWeatherIndex(suppRecords.Weather),
            RegionsByFormId = BuildRegionIndex(suppRecords.Regions),
            RuntimeWeatherTransition = suppRecords.RuntimeWeatherTransition,
            ClimatesByFormId = BuildClimateIndex(suppRecords.Climate),
            ImageSpacesByFormId = BuildImageSpaceIndex(suppRecords.ImageSpaces),
            ImageSpaceModifiersByFormId = BuildImageSpaceModifierIndex(suppRecords.ImageSpaceModifiers),
            LightingTemplatesByFormId = BuildLightingTemplateIndex(suppRecords.LightingTemplates),
            LightsByFormId = BuildLightIndex(suppRecords.Lights),
            ExternalEmittanceColorsByFormId = ExternalEmittanceResolver.BuildIndex(
                suppRecords.Regions, suppRecords.Lights),
            AllWeathers = BuildAllWeathers(suppRecords.Weather)
        };
    }

    /// <summary>
    ///     Map each SpeedTree <c>.spt</c> archive path → the leaf atlas the engine actually applies: the
    ///     <c>TREE</c> record's <c>ICON</c> field (the `.spt`'s own leaf material is a dev-era path that
    ///     often never shipped — e.g. WhiteOak's `treewoakleaves01b` vs the shipped `WhiteOakLeaves01.dds`).
    ///     <see cref="SpeedTreeRecordSource" /> walks BOTH the typed <c>Trees</c> list (FNV/FO3) and the
    ///     generic records (Oblivion/Skyrim/FO4); scanning only one drops every tree on the other family.
    /// </summary>
    private static Dictionary<string, string> BuildSpeedTreeLeafTextures(RecordCollection semantic) =>
        SpeedTreeRecordSource.BuildLeafTextureMap(semantic);

    /// <summary>
    ///     Map each SpeedTree <c>.spt</c> archive path → the TREE record's CNAM dimming pair — the engine's
    ///     canopy-depth darkening inputs (<c>CSpeedTreeRT::Set{Leaf,Branch}DimmingScalar</c>, applied per
    ///     tree before Compute). Without these the generator falls back to the <c>.spt</c>'s token-3010
    ///     leaf default and neutral bark.
    /// </summary>
    private static Dictionary<string, SpeedTreeDimming> BuildSpeedTreeDimming(RecordCollection semantic) =>
        SpeedTreeRecordSource.BuildDimmingMap(semantic);

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

    private static Dictionary<uint, GrassRecord> BuildGrassIndex(List<GrassRecord> records)
    {
        var dict = new Dictionary<uint, GrassRecord>(records.Count);
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

    /// <summary>
    ///     Resolves each base record's raw <c>MODS</c> entries (shape → TXST FormID) into an
    ///     <see cref="AlternateTextureSet" /> keyed by base FormID, by looking each TXST FormID up in
    ///     <paramref name="textureSets" /> and taking its diffuse/normal slots. Paths are canonicalized
    ///     with <see cref="NifTexturePathUtility.Normalize" /> so a TXST's data-relative path resolves
    ///     through the same texture cache as a NIF-embedded path. Entries whose TXST is unresolved or has
    ///     no diffuse/normal are skipped; base objects left with nothing to override are omitted.
    /// </summary>
    private static Dictionary<uint, AlternateTextureSet> BuildAlternateTextureIndex(
        IReadOnlyDictionary<uint, IReadOnlyList<AlternateTextureEntry>> entriesByFormId,
        Dictionary<uint, TextureSetRecord> textureSets)
    {
        var dict = new Dictionary<uint, AlternateTextureSet>();
        if (entriesByFormId.Count == 0)
        {
            return dict;
        }

        foreach (var (baseFormId, entries) in entriesByFormId)
        {
            var overrides = new Dictionary<string, ShapeTextureOverride>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                if (!textureSets.TryGetValue(entry.TextureSetFormId, out var txst))
                {
                    continue;
                }

                var diffuse = string.IsNullOrEmpty(txst.DiffuseTexture)
                    ? null
                    : NifTexturePathUtility.Normalize(txst.DiffuseTexture);
                var normal = string.IsNullOrEmpty(txst.NormalTexture)
                    ? null
                    : NifTexturePathUtility.Normalize(txst.NormalTexture);

                if (diffuse is null && normal is null)
                {
                    continue;
                }

                // Later MODS entries for the same shape win (engine applies the array in order).
                overrides[entry.ShapeName] = new ShapeTextureOverride(diffuse, normal);
            }

            if (AlternateTextureSet.Create(overrides) is { } set)
            {
                dict[baseFormId] = set;
            }
        }

        return dict;
    }

    /// <summary>
    ///     Indexes each MSWP record's already-normalized swap table by its FormID for the placement
    ///     bake (REFR <c>XMSP</c> → swaps). Records with no effective pairs are omitted so the bake's
    ///     "has a swap" check stays a plain dictionary hit.
    /// </summary>
    private static Dictionary<uint, IReadOnlyDictionary<string, string>> BuildMaterialSwapIndex(
        List<MaterialSwapRecord> records)
    {
        var dict = new Dictionary<uint, IReadOnlyDictionary<string, string>>(records.Count);
        foreach (var r in records)
        {
            if (r.Swaps.Count > 0)
            {
                dict.TryAdd(r.FormId, r.Swaps);
            }
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

    private static Dictionary<uint, RegionRecord> BuildRegionIndex(List<RegionRecord> records)
    {
        var dict = new Dictionary<uint, RegionRecord>(records.Count);
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

    private static Dictionary<uint, LightingTemplateRecord> BuildLightingTemplateIndex(List<LightingTemplateRecord> records)
    {
        var dict = new Dictionary<uint, LightingTemplateRecord>(records.Count);
        foreach (var r in records)
        {
            dict.TryAdd(r.FormId, r);
        }
        return dict;
    }

    private static Dictionary<uint, LightRecord> BuildLightIndex(List<LightRecord> records)
    {
        var dict = new Dictionary<uint, LightRecord>(records.Count);
        foreach (var r in records)
        {
            dict.TryAdd(r.FormId, r);
        }
        return dict;
    }

    private static Dictionary<uint, ImageSpaceRecord> BuildImageSpaceIndex(List<ImageSpaceRecord> records)
    {
        var dict = new Dictionary<uint, ImageSpaceRecord>(records.Count);
        foreach (var r in records)
        {
            dict.TryAdd(r.FormId, r);
        }
        return dict;
    }

    private static Dictionary<uint, ImageSpaceModifierRecord> BuildImageSpaceModifierIndex(
        List<ImageSpaceModifierRecord> records)
    {
        var dict = new Dictionary<uint, ImageSpaceModifierRecord>(records.Count);
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

    // Group map markers by owning worldspace, folding in markers inherited from child worldspaces
    // (FO3+ "Use Map Data" flag, or the bare WNAM chain on TES4-era games). Pure record computation,
    // so it lives in Core (WorldspaceMarkerGrouping) where it is headless-unit-testable; this WinUI
    // builder just delegates.
    private static Dictionary<uint, List<PlacedReference>> GroupMarkersByWorldspace(
        List<WorldspaceRecord> worldspaces, BethesdaGame game)
        => WorldspaceMarkerGrouping.GroupByWorldspace(worldspaces, game);

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

}

