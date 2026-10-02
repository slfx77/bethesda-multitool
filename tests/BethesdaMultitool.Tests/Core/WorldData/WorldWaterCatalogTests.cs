using BethesdaMultitool.Core.Formats.Esm.Export.Report;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Semantic.LoadOrder;
using BethesdaMultitool.Core.WorldData;
using Xunit;

namespace BethesdaMultitool.Tests.Core.WorldData;

public sealed class WorldWaterCatalogTests
{
    [Theory]
    [InlineData("chain", "inherited", 20f, 0x300u)]
    [InlineData("missing", "missing-parent", null, 0x18u)]
    [InlineData("cycle", "parent-cycle", null, 0x18u)]
    [InlineData("duplicate", "ambiguous-parent", null, 0x18u)]
    public void Route_failure_does_not_borrow_child_fields_and_default_appearance_keeps_its_own_provenance(
        string scenario, string status, float? height, uint expectedWater)
    {
        var child = new WorldspaceRecord { FormId = 0x100, ParentWorldspaceFormId = 0x101,
            ParentUseFlags = 8, DefaultWaterHeight = 999, WaterFormId = 0x200 };
        var parent = new WorldspaceRecord { FormId = 0x101, ParentWorldspaceFormId = 0x102,
            ParentUseFlags = 8, DefaultWaterHeight = 888 };
        var root = new WorldspaceRecord { FormId = 0x102, DefaultWaterHeight = 20, WaterFormId = 0x300,
            ParentWorldspaceFormId = scenario == "cycle" ? 0x100u : null,
            ParentUseFlags = scenario == "cycle" ? (ushort)8 : (ushort)0 };
        var worlds = new List<WorldspaceRecord> { child, root };
        if (scenario != "missing") worlds.Add(parent);
        if (scenario == "duplicate") worlds.Add(parent with { Offset = 100 });
        var catalog = WorldWaterCatalog.Create(worlds, BethesdaGame.FalloutNewVegas);
        var cell = FlatCell(0x500, child.FormId);
        var waters = new Dictionary<uint, WaterRecord>
        {
            [0x18] = new() { FormId = 0x18, EditorId = "DefaultWater" },
            [0x200] = new() { FormId = 0x200 }, [0x300] = new() { FormId = 0x300 }
        };

        var appearance = WaterAppearanceSelectionResolver.Resolve(cell, child, waters,
            BethesdaGame.FalloutNewVegas, worldWaterCatalog: catalog);
        Assert.Equal(status, appearance.WorldWater!.Status);
        Assert.Equal(height, catalog.ResolveHeight(cell, 777));
        Assert.Equal(expectedWater, appearance.WaterFormId);
        Assert.Equal(scenario == "chain" ? WaterAppearanceSelectionSource.WorldspaceNam2
            : WaterAppearanceSelectionSource.EngineDefault, appearance.Source);
        Assert.Equal(child.FormId, appearance.WorldspaceFormId);
        Assert.Equal(scenario == "chain" ? root.FormId : (uint?)null, appearance.WorldWater.SourceFormId);
        Assert.Equal(999f, child.DefaultWaterHeight);
        Assert.Equal(0x200u, child.WaterFormId);

        var overrideCell = cell with { WaterFormId = 0x200, WaterHeight = 42 };
        var overridden = WaterAppearanceSelectionResolver.Resolve(overrideCell, child, waters,
            BethesdaGame.FalloutNewVegas, worldWaterCatalog: catalog);
        Assert.Equal(WaterAppearanceSelectionSource.CellXcwt, overridden.Source);
        Assert.Equal(status, overridden.WorldWater!.Status);
        Assert.Equal(42f, catalog.ResolveHeight(overrideCell));
    }

    [Theory]
    [InlineData(BethesdaGame.Oblivion, false, null)]
    [InlineData(BethesdaGame.Oblivion, true, 50f)]
    [InlineData(BethesdaGame.FalloutNewVegas, false, 50f)]
    public void Existing_Tes4_water_flag_gate_is_not_applied_to_Fallout_parent_routes(
        BethesdaGame game, bool hasWater, float? expected)
    {
        var parent = new WorldspaceRecord { FormId = 0x100, DefaultWaterHeight = 50 };
        var child = new WorldspaceRecord { FormId = 0x101, ParentWorldspaceFormId = parent.FormId,
            ParentUseFlags = 8, DefaultWaterHeight = 50, WaterFromParentWorldspace = true };
        var catalog = WorldWaterCatalog.Create([parent, child], game);
        var cell = FlatCell(0x500, child.FormId) with { Flags = hasWater ? 2u : 0u,
            WaterHeight = WorldHeightNormalizer.NoWaterSentinel };
        Assert.Equal(expected, catalog.ResolveHeight(cell));
        var mask = HeightmapRenderer.ComputeHeightmapData([cell], waterCatalog: catalog)!.Value.WaterMask;
        Assert.Equal(expected.HasValue, mask.Any(v => v > 0));
        Assert.Equal(7f, catalog.ResolveHeight(cell with { WaterHeight = 7 }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Grass_cache_and_water_masks_do_not_depend_on_world_visitation_order(bool reverse)
    {
        var dry = FlatCell(0x500, 0x100);
        var wet = FlatCell(0x501, 0x101);
        var worlds = new List<WorldspaceRecord>
        {
            new() { FormId = 0x100, DefaultWaterHeight = -100 },
            new() { FormId = 0x101, DefaultWaterHeight = 100 }
        };
        var catalog = WorldWaterCatalog.Create(worlds, BethesdaGame.FalloutNewVegas);
        var cache = new WorldRenderCache
        {
            Game = BethesdaGame.FalloutNewVegas, WaterCatalog = catalog,
            LandTextureIndex = new Dictionary<uint, LandscapeTextureRecord>
            { [0x700] = new() { FormId = 0x700, GrassFormIds = [0x701] } },
            GrassIndex = new Dictionary<uint, GrassRecord>
            {
                [0x701] = new() { FormId = 0x701, ModelPath = "grass.nif", ModelBound = 32,
                    Data = new GrassData { Density = 100, MaxSlope = 90, PositionRange = 512,
                        UnitsFromWaterAmount = 20, UnitsFromWaterType = 0 } }
            }
        };
        foreach (var cell in reverse ? new[] { wet, dry } : [dry, wet])
        {
            // Simulate a concurrent/previously selected world leaving the wrong active scalar behind.
            cache.DefaultWaterHeight = cell == dry ? 100 : -100;
            var placements = cache.GetPlacementList(cell);
            Assert.Equal(cell == dry ? 64 : 0, placements.Length);
            var mask = HeightmapRenderer.ComputeHeightmapData([cell], cache.DefaultWaterHeight, cache)!.Value.WaterMask;
            Assert.Equal(cell == wet, mask.Any(v => v > 0));
            Assert.Same(placements, cache.GetPlacementList(cell));
        }
        worlds[0] = worlds[0] with { DefaultWaterHeight = 100 };
        Assert.Equal(-100f, catalog.ResolveHeight(dry)); // the former snapshot is unchanged
        Assert.Equal(100f, WorldWaterCatalog.Create(worlds, BethesdaGame.FalloutNewVegas).ResolveHeight(dry));
    }

    [Fact]
    public void Nonzero_load_order_parent_override_drives_height_appearance_and_report_provenance()
    {
        var order = PluginLoadOrder.Create(["Other.esm", "A.esm", "B.esp"], false,
            p => p == "B.esp" ? ["A.esm"] : []);
        var original = new RecordCollection
        {
            Game = BethesdaGame.FalloutNewVegas,
            Worldspaces = [new() { FormId = 0x800, DefaultWaterHeight = 10, WaterFormId = 0x900 },
                new() { FormId = 0x801, ParentWorldspaceFormId = 0x800, ParentUseFlags = 8, DefaultWaterHeight = 999 }],
            Water = [new() { FormId = 0x900 }]
        };
        var patch = new RecordCollection
        {
            Game = BethesdaGame.FalloutNewVegas,
            Worldspaces = [new() { FormId = 0x800, DefaultWaterHeight = 77.5f, WaterFormId = 0x01000901 }],
            Water = [new() { FormId = 0x01000901 }]
        };
        LoadOrderRecordVersion Version(string file, uint id, string type) =>
            new(file, file, id, order.Map(file, id).LoadOrderFormId, type, null, 0, 24);
        var index = LoadOrderRecordIndex.Create(order,
            [Version("A.esm", 0x800, "WRLD"), Version("A.esm", 0x801, "WRLD"), Version("A.esm", 0x900, "WATR"),
             Version("B.esp", 0x800, "WRLD"), Version("B.esp", 0x01000901, "WATR")]);
        var view = LoadOrderSelectionView.FromSources(order, index,
            [(order.Entries[1], original), (order.Entries[2], patch)]);
        var catalog = WorldWaterCatalog.Create(view.Records.Worldspaces, view.Records.Game);
        var worldView = WorldMapOverlayBuilder.BuildFromRecords(view.Records, null);
        Assert.Equal(BethesdaGame.FalloutNewVegas, worldView.Game);
        Assert.Equal(77.5f, worldView.WaterCatalog.Get(0x01000801).Height);
        var child = view.Records.Worldspaces.Single(w => w.FormId == 0x01000801);
        var water = WaterAppearanceSelectionResolver.Resolve(null, child,
            view.Records.Water.ToDictionary(w => w.FormId), view.Records.Game, worldWaterCatalog: catalog);
        Assert.Equal(77.5f, catalog.Get(child.FormId).Height);
        Assert.Equal(0x02000901u, water.WaterFormId);
        Assert.Equal(0x01000800u, water.WorldWater!.SourceFormId);
        Assert.Contains("0x01000801,world-water-height,0x01000800,,77.5,Reconstruction,inherited,0x01000801;0x01000800,B.esp,B.esp,24",
            SelectedViewReportWriter.Generate(view)["world_inheritance.csv"]);
        Assert.Equal(999f, child.DefaultWaterHeight);
        Assert.Equal(10f, original.Worldspaces[0].DefaultWaterHeight);
        Assert.Equal(0x01000901u, patch.Worldspaces[0].WaterFormId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Two_dimensional_palette_uses_the_same_retained_cell_override_as_three_dimensional_appearance(bool retainCellWater)
    {
        var parent = new WorldspaceRecord { FormId = 0x100, WaterFormId = 0x300 };
        var child = new WorldspaceRecord { FormId = 0x101, ParentWorldspaceFormId = parent.FormId,
            ParentUseFlags = 8, WaterFormId = 0x999 };
        var cell = FlatCell(0x500, child.FormId) with { WaterFormId = 0x200 };
        var catalog = WorldWaterCatalog.Create([parent, child], BethesdaGame.FalloutNewVegas);
        var waters = new Dictionary<uint, WaterRecord>
        {
            [0x300] = new() { FormId = 0x300, VisualProperties = new Dictionary<string, object?>
                { ["ShallowColor"] = 0x0000FF00u, ["DeepColor"] = 0x0000FF00u } }
        };
        if (retainCellWater) waters[0x200] = new() { FormId = 0x200, VisualProperties = new Dictionary<string, object?>
            { ["ShallowColor"] = 0x000000FFu, ["DeepColor"] = 0x000000FFu } };
        var cache = new WorldRenderCache { WaterCatalog = catalog, WaterRecords = waters, Game = BethesdaGame.FalloutNewVegas };
        var appearance = WaterAppearanceSelectionResolver.Resolve(cell, child, waters,
            BethesdaGame.FalloutNewVegas, worldWaterCatalog: catalog);
        var palette = cache.GetWaterPalette(cell, new((0, 0, 255), (0, 0, 255)));
        Assert.NotNull(palette);
        Assert.Equal(retainCellWater ? ((byte)255, (byte)0, (byte)0) : ((byte)0, (byte)255, (byte)0), palette.Shallow);
        Assert.Equal(retainCellWater ? 0x200u : 0x300u, appearance.WaterFormId);
        Assert.Equal("inherited", appearance.WorldWater!.Status);
    }

    private static CellRecord FlatCell(uint id, uint world) => new()
    {
        FormId = id, WorldspaceFormId = world, GridX = 0, GridY = 0, CellWorldSize = 4096,
        Heightmap = new() { HeightOffset = 0, HeightDeltas = new sbyte[1089] },
        LandVisualData = new() { TextureLayers = Enumerable.Range(0, 4).Select(q => new LandTextureLayer
            { Kind = LandTextureLayerKind.Base, TextureFormId = 0x700, Quadrant = (byte)q }).ToList() }
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Terrainless_duplicate_grid_cell_cannot_acquire_the_aggregate_water_palette(bool reversed)
    {
        var terrainCell = FlatCell(0x500, 0x100) with { WaterFormId = 0x200 };
        var emptyCell = terrainCell with { FormId = 0x501, Heightmap = null, LandVisualData = null, WaterFormId = 0x300 };
        var cells = reversed ? new List<CellRecord> { emptyCell, terrainCell } : [terrainCell, emptyCell];
        var admitted = Assert.Single(HeightmapRenderer.GetTerrainCells(cells));
        Assert.Same(terrainCell, admitted.Cell);
        Assert.Equal(0x200u, admitted.Cell.WaterFormId);
        var mask = HeightmapRenderer.ComputeHeightmapData(cells, 100)!.Value.WaterMask;
        Assert.Equal(HeightmapRenderer.ComputeHeightmapData([terrainCell], 100)!.Value.WaterMask, mask);
    }
}
