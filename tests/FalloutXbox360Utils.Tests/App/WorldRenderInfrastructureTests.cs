using System.Numerics;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera;
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
    public void WorldRenderCache_QueryPlacementCandidates_SkipsDistantSubCellBucketsButKeepsIntersectingBounds()
    {
        var near = RenderablePlacement(0x201, 100f, 0f);
        var largeIntersecting = RenderablePlacement(
            0x202,
            1400f,
            0f,
            new ObjectBounds { X1 = -1000, X2 = 1000 });
        var far = RenderablePlacement(0x203, 3000f, 0f);
        var actor = RenderablePlacement(0x204, 0f, 0f) with { RecordType = "ACHR" };
        var cell = new CellRecord
        {
            FormId = 0x200,
            GridX = 0,
            GridY = 0,
            PlacedObjects = [near, largeIntersecting, far, actor]
        };
        var cache = new WorldRenderCache();
        var candidates = new List<RenderableReference>();

        var totalRenderable = cache.QueryPlacementCandidates(
            cell,
            0f,
            0f,
            512f,
            null,
            0f,
            candidates);

        Assert.Equal(3, totalRenderable);
        Assert.Contains(candidates, r => r.FormId == near.FormId);
        Assert.Contains(candidates, r => r.FormId == largeIntersecting.FormId);
        Assert.DoesNotContain(candidates, r => r.FormId == far.FormId);
        Assert.DoesNotContain(candidates, r => r.FormId == actor.FormId);
    }

    [Fact]
    public void WorldRenderCache_QueryPlacementCandidates_SkipsBucketsOutsideFrustum()
    {
        var inside = RenderablePlacement(0x211, 0f, 0f);
        var outside = RenderablePlacement(0x212, 5000f, 0f);
        var cell = new CellRecord
        {
            FormId = 0x210,
            GridX = 0,
            GridY = 0,
            PlacedObjects = [inside, outside]
        };
        var cache = new WorldRenderCache();
        var candidates = new List<RenderableReference>();
        var frustum = Frustum.FromViewProjection(Matrix4x4.Identity);

        var totalRenderable = cache.QueryPlacementCandidates(
            cell,
            0f,
            0f,
            10_000f,
            frustum,
            0f,
            candidates);

        Assert.Equal(2, totalRenderable);
        Assert.Contains(candidates, r => r.FormId == inside.FormId);
        Assert.DoesNotContain(candidates, r => r.FormId == outside.FormId);
    }

    [Fact]
    public void ResolveEffectiveWaterHeight_CellSentinelFallsBackToWorldspaceDefault()
    {
        // Vanilla WastelandNV's Colorado river cells have HasWater=true but XCLW set to
        // float.MaxValue (the no-water sentinel) — the engine treats that as "no explicit
        // override, use worldspace default" (-2300 in WastelandNV), NOT "no water at all."
        // The 2026-05-28 sentinel-preservation fix had short-circuited the cell sentinel to
        // null, suppressing water for every such cell; the visible regression was
        // "Colorado river displays empty."
        var cellWithSentinel = new CellRecord
        {
            FormId = 0x000E2823,
            GridX = -65,
            GridY = -1,
            Flags = 0x02, // HasWater
            WaterHeight = float.MaxValue
        };

        var result = WorldRenderCache.ResolveEffectiveWaterHeight(cellWithSentinel, -2300f);

        Assert.Equal(-2300f, result);
    }

    [Fact]
    public void ResolveEffectiveWaterHeight_CellExplicitValueOverridesWorldspaceDefault()
    {
        var cell = new CellRecord { FormId = 0x100, WaterHeight = -1500f };

        var result = WorldRenderCache.ResolveEffectiveWaterHeight(cell, -2300f);

        Assert.Equal(-1500f, result);
    }

    [Fact]
    public void ResolveEffectiveWaterHeight_NullCellHeightFallsBackToWorldspaceDefault()
    {
        var cell = new CellRecord { FormId = 0x100, WaterHeight = null };

        var result = WorldRenderCache.ResolveEffectiveWaterHeight(cell, -2300f);

        Assert.Equal(-2300f, result);
    }

    [Fact]
    public void ResolveEffectiveWaterHeight_WorldspaceSentinelMeansNoWater()
    {
        // When the worldspace itself has no water (DNAM = sentinel) and the cell has no
        // explicit override, there's no fallback to apply — return null so the renderer
        // skips water entirely. This is what prevented the tLandscape prototype worldspace
        // from being incorrectly flooded at Z=0 (the original sentinel-preservation fix).
        var cellNoOverride = new CellRecord { FormId = 0x100, WaterHeight = null };
        var cellWithSentinel = new CellRecord { FormId = 0x101, WaterHeight = float.MaxValue };

        Assert.Null(WorldRenderCache.ResolveEffectiveWaterHeight(cellNoOverride, float.MaxValue));
        Assert.Null(WorldRenderCache.ResolveEffectiveWaterHeight(cellWithSentinel, float.MaxValue));
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

    [Fact]
    public void Frustum_IntersectsSphereAcceptsInsideAndRejectsOutside()
    {
        var frustum = Frustum.FromViewProjection(Matrix4x4.Identity);

        Assert.True(frustum.IntersectsSphere(new Vector3(0f, 0f, 0.5f), 0.1f));
        Assert.True(frustum.IntersectsSphere(new Vector3(1.05f, 0f, 0.5f), 0.1f));
        Assert.False(frustum.IntersectsSphere(new Vector3(1.2f, 0f, 0.5f), 0.05f));
    }

    [Fact]
    public void WastelandNvHeavyBookmarkScoringChoosesDensestNearbyReferenceCluster()
    {
        var clusterA = CreateCell(0x501, 0, 0, 4);
        var clusterB = CreateCell(0x502, 1, 0, 4);
        var distant = CreateCell(0x503, 10, 10, 5);
        var worldspace = new WorldspaceRecord
        {
            FormId = 0x600,
            EditorId = "WastelandNV",
            Cells = [clusterA, clusterB, distant]
        };
        var data = CreateWorldViewData(worldspace, [clusterA, clusterB, distant], []);
        var index = WorldSpatialIndex.Build(
            data,
            [clusterA, clusterB, distant],
            [],
            worldspace.FormId,
            worldspace.DefaultWaterHeight);

        var bookmark = WorldViewStressBookmarkFinder.FindWastelandNvHeavyBookmark(
            index,
            data.RenderCache,
            WorldGridConstants.CellSize * 2f);

        Assert.NotNull(bookmark);
        Assert.Equal(8, bookmark.Value.Score);
        Assert.InRange(bookmark.Value.CanvasCenter.X, 0f, WorldGridConstants.CellSize * 2f);
        Assert.InRange(bookmark.Value.CanvasCenter.Y, -WorldGridConstants.CellSize, 0f);
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

    private static CellRecord CreateCell(uint formId, int gx, int gy, int referenceCount)
    {
        var refs = new List<PlacedReference>(referenceCount);
        for (var i = 0; i < referenceCount; i++)
        {
            refs.Add(new PlacedReference
            {
                FormId = formId + (uint)i + 1,
                BaseFormId = 0x700 + (uint)i,
                ModelPath = "meshes\\clutter\\test.nif",
                X = gx * WorldGridConstants.CellSize + 256f + i,
                Y = gy * WorldGridConstants.CellSize + 256f + i,
                Z = 0f
            });
        }

        return new CellRecord
        {
            FormId = formId,
            GridX = gx,
            GridY = gy,
            PlacedObjects = refs
        };
    }

    private static PlacedReference RenderablePlacement(
        uint formId,
        float x,
        float y,
        ObjectBounds? bounds = null)
    {
        return new PlacedReference
        {
            FormId = formId,
            BaseFormId = 0x700 + formId,
            ModelPath = $"meshes\\clutter\\test{formId:x}.nif",
            X = x,
            Y = y,
            Z = 0f,
            Bounds = bounds
        };
    }
}