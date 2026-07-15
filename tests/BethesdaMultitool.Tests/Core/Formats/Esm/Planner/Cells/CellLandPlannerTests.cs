using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Planner.Catalog;
using BethesdaMultitool.Core.Formats.Esm.Planner.Cells;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner.Cells;

public sealed class CellLandPlannerTests
{
    [Fact]
    public void DmpNew_Exterior_Prefers_Captured_Heightmap()
    {
        var captured = Heightmap(7);
        var cell = Exterior() with
        {
            Heightmap = captured,
            RuntimeTerrainMesh = new RuntimeTerrainMesh { Vertices = [0f, 0f, 0f] },
        };

        var result = CellLandPlanner.DecideAll([Entry(cell, SourceKind.DmpNew)]);

        var decision = Assert.Single(result.DecisionsByCellSourceFormId.Values);
        Assert.Same(captured, decision.Heightmap);
        Assert.Equal(CellLandHeightSource.CapturedHeightmap, decision.HeightSource);
    }

    [Fact]
    public void Complete_Runtime_Mesh_Synthesizes_Land()
    {
        var cell = Exterior() with { RuntimeTerrainMesh = CompleteRuntimeMesh() };

        var result = CellLandPlanner.DecideAll([Entry(cell, SourceKind.DmpNew)]);

        var decision = Assert.Single(result.DecisionsByCellSourceFormId.Values);
        Assert.Equal(CellLandHeightSource.CompleteRuntimeMesh, decision.HeightSource);
        Assert.Equal(33 * 33, decision.Heightmap.HeightDeltas.Length);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void CoordinateOnly_And_Partial_Runtime_Captures_Do_Not_Invent_Land()
    {
        var coordinateOnly = Exterior();
        var partial = Exterior(0x0010B902) with
        {
            RuntimeTerrainMesh = new RuntimeTerrainMesh
            {
                Vertices = [0f, 0f, 1f, 128f, 0f, 2f, 0f, 128f, 3f],
            },
        };

        var result = CellLandPlanner.DecideAll(
            [Entry(coordinateOnly, SourceKind.DmpNew), Entry(partial, SourceKind.DmpNew)]);

        Assert.Empty(result.DecisionsByCellSourceFormId);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("land.runtime-mesh-not-complete", diagnostic.Code);
        Assert.Equal(partial.FormId, diagnostic.FormId);
    }

    [Fact]
    public void Enriched_Partial_Runtime_Heightmap_Does_Not_Bypass_Complete_Mesh_Gate()
    {
        var mesh = PartialRuntimeMesh();
        var cell = Exterior() with
        {
            Heightmap = mesh.ToLandHeightmap(),
            RuntimeTerrainMesh = mesh,
        };

        var result = CellLandPlanner.DecideAll([Entry(cell, SourceKind.DmpNew)]);

        Assert.Empty(result.DecisionsByCellSourceFormId);
        Assert.Equal("land.runtime-mesh-not-complete", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void Captured_Vhgt_Wins_When_Runtime_Mesh_Is_Partial()
    {
        var captured = Heightmap(4);
        var mesh = PartialRuntimeMesh();
        var cell = Exterior() with
        {
            CapturedLandHeightmap = captured,
            Heightmap = mesh.ToLandHeightmap(),
            RuntimeTerrainMesh = mesh,
        };

        var result = CellLandPlanner.DecideAll([Entry(cell, SourceKind.DmpNew)]);

        var decision = Assert.Single(result.DecisionsByCellSourceFormId.Values);
        Assert.Same(captured, decision.Heightmap);
        Assert.Equal(CellLandHeightSource.CapturedHeightmap, decision.HeightSource);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Master_Override_Interior_And_Persistent_Cell_Never_Get_New_Land()
    {
        var masterOverride = Exterior() with { Heightmap = Heightmap(1) };
        var interior = new CellRecord
        {
            FormId = 0x0010B903,
            Flags = 0x01,
            Heightmap = Heightmap(2),
        };
        var persistent = Exterior(0x0010B904) with
        {
            IsPersistentCell = true,
            Heightmap = Heightmap(3),
        };

        var result = CellLandPlanner.DecideAll(
            [
                Entry(masterOverride, SourceKind.DmpOverride),
                Entry(interior, SourceKind.DmpNew),
                Entry(persistent, SourceKind.DmpNew),
            ]);

        Assert.Empty(result.DecisionsByCellSourceFormId);
    }

    [Fact]
    public void CellSection_Plans_Land_Before_Navm_And_Temporary_Refs()
    {
        var cell = Exterior() with
        {
            Heightmap = Heightmap(1),
            PlacedObjects =
            [
                new PlacedReference
                {
                    FormId = 0xAA003000,
                    BaseFormId = 0x00001000,
                    RecordType = "REFR",
                },
            ],
        };
        var navm = new NavMeshRecord
        {
            FormId = 0xAA004000,
            CellFormId = cell.FormId,
            RawSubrecords = [new NavMeshSubrecord("DATA", [1, 2, 3, 4])],
        };

        var result = CellSectionPlanner.Plan(
            new Dictionary<uint, PcEsmCellContext>(),
            new Dictionary<uint, ParsedMainRecord>(),
            [cell],
            [navm],
            [],
            new HashSet<uint>(),
            new FormIdAllocator(),
            emitMasterCellNavmAugmentation: false);

        var plan = Assert.Single(result.CellsByFormId.Values);
        Assert.Equal(["LAND", "NAVM", "REFR"], plan.TemporaryChildren.Select(child => child.Type));
    }

    private static CellRecord Exterior(uint formId = 0x0010B901) => new()
    {
        FormId = formId,
        WorldspaceFormId = 0x0010B96F,
        GridX = 0,
        GridY = 0,
    };

    private static CellCatalogEntry Entry(CellRecord cell, SourceKind source) => new()
    {
        CellFormId = cell.FormId,
        Source = source,
        DmpModel = cell,
    };

    private static LandHeightmap Heightmap(sbyte value) => new()
    {
        HeightOffset = 100f,
        HeightDeltas = Enumerable.Repeat(value, 33 * 33).ToArray(),
    };

    private static RuntimeTerrainMesh CompleteRuntimeMesh()
    {
        var vertices = new float[33 * 33 * 3];
        for (var y = 0; y < 33; y++)
        {
            for (var x = 0; x < 33; x++)
            {
                var index = (y * 33 + x) * 3;
                vertices[index] = x * 128f;
                vertices[index + 1] = y * 128f;
                vertices[index + 2] = x * 3f + y * 2f + (x * y % 7);
            }
        }

        return new RuntimeTerrainMesh { Vertices = vertices };
    }

    private static RuntimeTerrainMesh PartialRuntimeMesh()
    {
        var vertices = new List<float>();
        for (var y = 0; y < 12; y++)
        {
            for (var x = 0; x < 24; x++)
            {
                vertices.Add(x * 128f);
                vertices.Add(y * 128f);
                vertices.Add(x + y);
            }
        }

        return new RuntimeTerrainMesh { Vertices = vertices.ToArray() };
    }
}
