using System.Numerics;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using Xunit;

namespace FalloutXbox360Utils.Tests.App;

public sealed class WorldRenderInfrastructureTests
{
    [Fact]
    public void WorldRenderCache_DecodesTerrainAndCachesWaterMask()
    {
        var cell = new CellRecord
        {
            FormId = 0x100,
            GridX = 0,
            GridY = 0,
            Heightmap = new LandHeightmap
            {
                HeightOffset = 10f,
                HeightDeltas = new sbyte[33 * 33]
            }
        };
        var cache = new WorldRenderCache();

        var decoded = cache.GetTerrain(cell);
        var decodedAgain = cache.GetTerrain(cell);

        Assert.Same(decoded, decodedAgain);
        Assert.True(decoded.FromEsmHeightmap);
        Assert.False(decoded.FromRuntimeTerrain);
        Assert.False(decoded.MissingTerrain);
        Assert.Equal(33 * 33, decoded.Heights.Length);
        Assert.All(decoded.Heights, h => Assert.Equal(80f, h));

        var mask = decoded.GetLowResWaterMask(100f);
        var maskAgain = decoded.GetLowResWaterMask(100f);

        Assert.NotNull(mask);
        Assert.Same(mask, maskAgain);
        Assert.All(mask!, value => Assert.Equal((byte)180, value));
    }

    [Fact]
    public void WorldSpatialIndex_BucketQueriesReturnVisibleCellsRefsAndMarkers()
    {
        var normalRef = new PlacedReference { FormId = 0x201, BaseFormId = 0x301, X = 256f, Y = 256f };
        var persistentRef = new PlacedReference { FormId = 0x202, BaseFormId = 0x302, X = 512f, Y = 512f };
        var marker = new PlacedReference
        {
            FormId = 0x203,
            BaseFormId = 0x303,
            X = 600f,
            Y = 600f,
            IsMapMarker = true
        };

        var originCell = new CellRecord
        {
            FormId = 0x101,
            GridX = 0,
            GridY = 0,
            PlacedObjects = [normalRef]
        };
        var farCell = new CellRecord
        {
            FormId = 0x102,
            GridX = 10,
            GridY = 10,
            PlacedObjects = [new PlacedReference { FormId = 0x204, BaseFormId = 0x304, X = 41_000f, Y = 41_000f }]
        };
        var persistentCell = new CellRecord
        {
            FormId = 0x103,
            HasPersistentObjects = true,
            PlacedObjects = [persistentRef]
        };
        var worldspace = new WorldspaceRecord
        {
            FormId = 0x400,
            Cells = [originCell, farCell],
            DefaultWaterHeight = 100f
        };
        var data = CreateWorldViewData(worldspace, [originCell, farCell, persistentCell], [marker]);

        var index = WorldSpatialIndex.Build(
            data,
            [originCell, farCell, persistentCell],
            [marker],
            worldspace.FormId,
            worldspace.DefaultWaterHeight);

        var cells = new List<WorldSpatialCell>();
        index.QueryCellsInRadius(2048f, -2048f, 256f, cells);
        Assert.Single(cells);
        Assert.Same(originCell, cells[0].Cell);

        var refs = new List<PlacedReference>();
        index.QueryRefsInViewport(new Vector2(0f, -4096f), new Vector2(4096f, 0f), refs);
        Assert.Contains(normalRef, refs);
        Assert.Contains(persistentRef, refs);
        Assert.DoesNotContain(farCell.PlacedObjects[0], refs);

        index.QueryMarkersNear(new Vector2(600f, -600f), 64f, refs);
        Assert.Single(refs);
        Assert.Same(marker, refs[0]);
    }

    private static WorldViewData CreateWorldViewData(
        WorldspaceRecord worldspace,
        List<CellRecord> allCells,
        List<PlacedReference> markers)
    {
        return new WorldViewData
        {
            Worldspaces = [worldspace],
            InteriorCells = [],
            BoundsIndex = [],
            CategoryIndex = [],
            Resolver = FormIdResolver.Empty,
            MapMarkers = markers,
            MarkersByWorldspace = new Dictionary<uint, List<PlacedReference>>
            {
                [worldspace.FormId] = markers
            },
            AllCells = allCells,
            CellByFormId = allCells.ToDictionary(c => c.FormId),
            RefrToCellIndex = [],
            UnlinkedExteriorCells = [],
            UnlinkedMapMarkers = []
        };
    }
}
