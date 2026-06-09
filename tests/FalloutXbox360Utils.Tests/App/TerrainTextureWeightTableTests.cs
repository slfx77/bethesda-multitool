using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Textures;
using Xunit;

namespace FalloutXbox360Utils.Tests.App;

/// <summary>
///     Asserts the engine-accurate cross-quadrant blend behavior of
///     <see cref="CellLayerWeightTable.Build" />. The seam fix relies on shared-edge vertices
///     (cell column 16 / row 16) and the cell center (16, 16) carrying contributions from all
///     touching quadrants; bilinear interp during per-pixel sampling then produces a smooth
///     blend instead of the prior hard quadrant boundary.
/// </summary>
public sealed class TerrainTextureWeightTableTests
{
    private const uint BtxtSw = 0x10001;
    private const uint BtxtSe = 0x10002;
    private const uint BtxtNw = 0x10003;
    private const uint BtxtNe = 0x10004;

    // -------- Cross-cell blend tests (inter-cell seam fix) --------
    //
    // Neighbor convention: this cell is "A" at some (gx, gy). The "east neighbor" has the
    // same gy, gx+1 (its west edge is A's east edge). Similar for W, N (gy+1), S (gy-1).
    // Layers on the neighbor side are tagged with separate FormIDs so we can verify which
    // BTXT showed up in A's edge vertices.

    private const uint NeighborBtxtWest = 0x20001; // BTXT in neighbor's west-side quadrants
    private const uint NeighborBtxtEast = 0x20002; // ...east-side quadrants
    private const uint NeighborBtxtSouth = 0x20003; // ...south-side quadrants
    private const uint NeighborBtxtNorth = 0x20004; // ...north-side quadrants

    [Fact]
    public void InteriorVertexInOneQuadrant_HasOnlyThatQuadrantsBtxt()
    {
        var layers = FourDistinctBtxts();

        var table = CellLayerWeightTable.Build(layers);

        Assert.NotNull(table);

        // (vx=4, vy=4) — NW quadrant interior (vy=0 is north).
        AssertSingleEntry(table!.At(4, 4), BtxtNw, 1f);
        // (vx=4, vy=28) — SW quadrant interior.
        AssertSingleEntry(table.At(4, 28), BtxtSw, 1f);
        // (vx=28, vy=4) — NE quadrant interior.
        AssertSingleEntry(table.At(28, 4), BtxtNe, 1f);
        // (vx=28, vy=28) — SE quadrant interior.
        AssertSingleEntry(table.At(28, 28), BtxtSe, 1f);
    }

    [Fact]
    public void SharedEastBoundaryOfSouthHalf_BlendsSwAndSeFiftyFifty()
    {
        var layers = FourDistinctBtxts();

        var table = CellLayerWeightTable.Build(layers);

        Assert.NotNull(table);
        // (vx=16, vy=24) — boundary between SW and SE quadrants on the southern half.
        AssertTwoEntries(table!.At(16, 24),
            (BtxtSw, 0.5f), (BtxtSe, 0.5f));
    }

    [Fact]
    public void SharedNorthBoundaryOfWestHalf_BlendsNwAndSwFiftyFifty()
    {
        var layers = FourDistinctBtxts();

        var table = CellLayerWeightTable.Build(layers);

        Assert.NotNull(table);
        // (vx=8, vy=16) — boundary between NW and SW quadrants on the western half.
        AssertTwoEntries(table!.At(8, 16),
            (BtxtNw, 0.5f), (BtxtSw, 0.5f));
    }

    [Fact]
    public void CellCenterVertex_BlendsAllFourQuadrantsTwentyFivePercentEach()
    {
        var layers = FourDistinctBtxts();

        var table = CellLayerWeightTable.Build(layers);

        Assert.NotNull(table);
        ref readonly var center = ref table!.Vertices[16 * 33 + 16];
        Assert.Equal(4, center.Count);

        var weights = new Dictionary<uint, float>
        {
            [center.E0.FormId] = center.E0.Weight,
            [center.E1.FormId] = center.E1.Weight,
            [center.E2.FormId] = center.E2.Weight,
            [center.E3.FormId] = center.E3.Weight
        };

        Assert.Equal(0.25f, weights[BtxtSw], 4);
        Assert.Equal(0.25f, weights[BtxtSe], 4);
        Assert.Equal(0.25f, weights[BtxtNw], 4);
        Assert.Equal(0.25f, weights[BtxtNe], 4);
    }

    [Fact]
    public void UniformBtxtAcrossAllQuadrants_PixelsInteriorAndCenterAlikeProduceSingleEntry()
    {
        // Open-desert case: every quadrant references the same BTXT. The merge-by-FormID path
        // in VertexWeights.Add should collapse the four contributions at the center vertex
        // back to a single entry with weight 1, so the per-pixel fast path applies cell-wide.
        var layers = new List<LandTextureLayer>
        {
            BtxtLayer(BtxtSw, 0),
            BtxtLayer(BtxtSw, 1),
            BtxtLayer(BtxtSw, 2),
            BtxtLayer(BtxtSw, 3)
        };

        var table = CellLayerWeightTable.Build(layers);

        Assert.NotNull(table);
        // Cell center vertex (16, 16): all 4 quadrants contribute BtxtSw with weight 1 each;
        // merge collapses to a single entry of raw weight 4, then renormalize to 1.
        AssertSingleEntry(table!.At(16, 16), BtxtSw, 1f);
        // Shared east boundary of south half (16, 24): SW + SE contribute, both BtxtSw;
        // merge collapses to weight 2, renormalize to 1.
        AssertSingleEntry(table.At(16, 24), BtxtSw, 1f);
        // Pure interior vertex stays at 1.
        AssertSingleEntry(table.At(4, 4), BtxtSw, 1f);
    }

    [Fact]
    public void QuadrantMissingBtxt_FillsResidualWithEngineDefaultSentinel()
    {
        // Only Q=0 (SW) has a BTXT. Other quadrants' residual weight should be filled with
        // the engine-default sentinel (FormID 0) so the per-pixel path falls back to
        // DirtWasteland01 there, matching the legacy behavior.
        var layers = new List<LandTextureLayer>
        {
            BtxtLayer(BtxtSw, 0)
        };

        var table = CellLayerWeightTable.Build(layers);

        Assert.NotNull(table);
        AssertSingleEntry(table!.At(4, 28), BtxtSw, 1f); // SW interior
        AssertSingleEntry(table.At(4, 4), 0u, 1f); // NW interior — engine default
        AssertSingleEntry(table.At(28, 4), 0u, 1f); // NE interior — engine default
    }

    [Fact]
    public void AtxtAtSharedEastEdgeOfSw_ContributesHalfWeightAtBoundaryVertex()
    {
        // SW has a BTXT and one ATXT whose VTXT is 1.0 along the east edge of the SW quadrant
        // (qx=16) and 0.0 elsewhere. The east edge column in SW maps to cell vx=16 (the shared
        // boundary). At that vertex:
        //   - SW contributes its BTXT with weight (1 - 1.0) = 0
        //   - SW contributes its ATXT with weight 1.0
        //   - SE contributes its BTXT with weight 1.0
        //   Raw sum = 2.0 → renormalize → ATXT 0.5, SE-BTXT 0.5.
        const uint sweAtxt = 0x10010;
        var atxt = new LandTextureLayer
        {
            Kind = LandTextureLayerKind.Alpha,
            TextureFormId = sweAtxt,
            Quadrant = 0,
            Layer = 0,
            BlendEntries = BuildBlendEntriesAtEastEdge(1f)
        };

        var layers = new List<LandTextureLayer>
        {
            BtxtLayer(BtxtSw, 0),
            BtxtLayer(BtxtSe, 1),
            atxt
        };

        var table = CellLayerWeightTable.Build(layers);

        Assert.NotNull(table);
        AssertTwoEntries(table!.At(16, 24),
            (sweAtxt, 0.5f), (BtxtSe, 0.5f));

        // One pixel column inside SW (vx=15, vy=24): only SW contributes (interior of SW).
        // SW-ATXT opacity at qx=15 is 0, so BTXT weight = 1 - 0 = 1.
        AssertSingleEntry(table.At(15, 24), BtxtSw, 1f);
        // One pixel column inside SE (vx=17, vy=24): only SE contributes.
        AssertSingleEntry(table.At(17, 24), BtxtSe, 1f);
    }

    [Fact]
    public void NoLayers_ReturnsNull()
    {
        var table = CellLayerWeightTable.Build(new List<LandTextureLayer>());
        Assert.Null(table);
    }

    [Fact]
    public void EastNeighbor_BlendsIntoCellsEasternEdge()
    {
        // A has its own 4 BTXTs (distinct per quadrant). East neighbor E has BtxtSw in its
        // west quadrants (Q0=SW, Q2=NW). At A's east edge (vx=32, vy=24), A's Q1 (BtxtSe)
        // contributes weight 1 and E's Q0 (NeighborBtxtWest) contributes weight 1 →
        // renormalize → 0.5/0.5.
        var aLayers = FourDistinctBtxts();
        var eastNeighbor = new List<LandTextureLayer>
        {
            BtxtLayer(NeighborBtxtWest, 0), // SW of E — A's east-south boundary
            BtxtLayer(NeighborBtxtWest, 2) // NW of E — A's east-north boundary
        };

        var table = CellLayerWeightTable.Build(aLayers, eastNeighbor);

        Assert.NotNull(table);
        // South half of A's east edge.
        AssertTwoEntries(table!.At(32, 24), (BtxtSe, 0.5f), (NeighborBtxtWest, 0.5f));
        // North half of A's east edge.
        AssertTwoEntries(table.At(32, 8), (BtxtNe, 0.5f), (NeighborBtxtWest, 0.5f));
        // One column inside A: only A's own quadrant contributes.
        AssertSingleEntry(table.At(28, 24), BtxtSe, 1f);
    }

    [Fact]
    public void WestNeighbor_BlendsIntoCellsWesternEdge()
    {
        var aLayers = FourDistinctBtxts();
        var westNeighbor = new List<LandTextureLayer>
        {
            BtxtLayer(NeighborBtxtEast, 1), // SE of W — A's west-south boundary
            BtxtLayer(NeighborBtxtEast, 3) // NE of W — A's west-north boundary
        };

        var table = CellLayerWeightTable.Build(aLayers, westNeighborLayers: westNeighbor);

        Assert.NotNull(table);
        AssertTwoEntries(table!.At(0, 24), (BtxtSw, 0.5f), (NeighborBtxtEast, 0.5f));
        AssertTwoEntries(table.At(0, 8), (BtxtNw, 0.5f), (NeighborBtxtEast, 0.5f));
        AssertSingleEntry(table.At(4, 24), BtxtSw, 1f);
    }

    [Fact]
    public void NorthNeighbor_BlendsIntoCellsNorthernEdge()
    {
        var aLayers = FourDistinctBtxts();
        var northNeighbor = new List<LandTextureLayer>
        {
            BtxtLayer(NeighborBtxtSouth, 0), // SW of N — A's north-west boundary
            BtxtLayer(NeighborBtxtSouth, 1) // SE of N — A's north-east boundary
        };

        var table = CellLayerWeightTable.Build(aLayers, northNeighborLayers: northNeighbor);

        Assert.NotNull(table);
        // West half of A's north edge.
        AssertTwoEntries(table!.At(8, 0), (BtxtNw, 0.5f), (NeighborBtxtSouth, 0.5f));
        // East half of A's north edge.
        AssertTwoEntries(table.At(24, 0), (BtxtNe, 0.5f), (NeighborBtxtSouth, 0.5f));
        // One row inside A.
        AssertSingleEntry(table.At(8, 4), BtxtNw, 1f);
    }

    [Fact]
    public void SouthNeighbor_BlendsIntoCellsSouthernEdge()
    {
        var aLayers = FourDistinctBtxts();
        var southNeighbor = new List<LandTextureLayer>
        {
            BtxtLayer(NeighborBtxtNorth, 2), // NW of S — A's south-west boundary
            BtxtLayer(NeighborBtxtNorth, 3) // NE of S — A's south-east boundary
        };

        var table = CellLayerWeightTable.Build(aLayers, southNeighborLayers: southNeighbor);

        Assert.NotNull(table);
        AssertTwoEntries(table!.At(8, 32), (BtxtSw, 0.5f), (NeighborBtxtNorth, 0.5f));
        AssertTwoEntries(table.At(24, 32), (BtxtSe, 0.5f), (NeighborBtxtNorth, 0.5f));
        AssertSingleEntry(table.At(8, 28), BtxtSw, 1f);
    }

    [Fact]
    public void NoNeighbors_EdgeVerticesAreSingleEntry()
    {
        // Without neighbor inputs, the cell's east-edge vertex carries ONLY this cell's
        // own east-quadrant BTXT (weight 1). That's the prior behavior — kept here as
        // backward-compat assertion so callers passing no neighbors get the old result.
        var aLayers = FourDistinctBtxts();

        var table = CellLayerWeightTable.Build(aLayers);

        Assert.NotNull(table);
        AssertSingleEntry(table!.At(32, 24), BtxtSe, 1f);
        AssertSingleEntry(table.At(0, 8), BtxtNw, 1f);
        AssertSingleEntry(table.At(8, 0), BtxtNw, 1f);
        AssertSingleEntry(table.At(24, 32), BtxtSe, 1f);
    }

    [Fact]
    public void CornerVertex_AccumulatesAllAvailableContributors()
    {
        // A's NW corner is shared by: A's Q2 (NW), W's Q3 (NE), N's Q0 (SW), and the
        // diagonal NW-cell's Q1 (SE). We pass only the 4 cardinal neighbors, so the corner
        // accumulates 3 contributions; the diagonal is intentionally absent (close enough
        // to the engine's behavior — see Build's xmldoc). Verify the 3 contributors blend
        // equally after renormalization.
        var aLayers = FourDistinctBtxts();
        var westNeighbor = new List<LandTextureLayer>
        {
            BtxtLayer(NeighborBtxtEast, 1),
            BtxtLayer(NeighborBtxtEast, 3)
        };
        var northNeighbor = new List<LandTextureLayer>
        {
            BtxtLayer(NeighborBtxtSouth, 0),
            BtxtLayer(NeighborBtxtSouth, 1)
        };

        var table = CellLayerWeightTable.Build(aLayers,
            westNeighborLayers: westNeighbor,
            northNeighborLayers: northNeighbor);

        Assert.NotNull(table);
        ref readonly var corner = ref table!.Vertices[0 * 33 + 0]; // NW corner
        Assert.Equal(3, corner.Count);

        var weights = new Dictionary<uint, float>
        {
            [corner.E0.FormId] = corner.E0.Weight,
            [corner.E1.FormId] = corner.E1.Weight,
            [corner.E2.FormId] = corner.E2.Weight
        };

        Assert.Equal(1f / 3f, weights[BtxtNw], 4); // A's own NW
        Assert.Equal(1f / 3f, weights[NeighborBtxtEast], 4); // W's NE
        Assert.Equal(1f / 3f, weights[NeighborBtxtSouth], 4); // N's SW
    }

    [Fact]
    public void NeighborWithEmptyLayers_DoesNotAffectEdge()
    {
        // Defensive: if a neighbor's TextureLayers is non-null but empty, treat it as no
        // contribution. The cell's edge stays the same as if no neighbor was passed.
        var aLayers = FourDistinctBtxts();

        var table = CellLayerWeightTable.Build(aLayers,
            new List<LandTextureLayer>());

        Assert.NotNull(table);
        AssertSingleEntry(table!.At(32, 24), BtxtSe, 1f);
    }

    [Fact]
    public void EngineDefaultStandInOwnLayers_WithRealNeighbor_BlendsAtSharedEdge()
    {
        // The renderer constructs this scenario when this cell has no own LAND texture data
        // but a neighbor does: own layers are 4 engine-default BTXTs (sentinel FormID 0), so
        // the cell's interior renders as engine default but its edge vertices accumulate the
        // real neighbor's BTXT and bilinear interp fades between them across the boundary.
        var defaultOwnLayers = new List<LandTextureLayer>
        {
            BtxtLayer(0u, 0), // 0 == EngineDefaultSentinelFormId
            BtxtLayer(0u, 1),
            BtxtLayer(0u, 2),
            BtxtLayer(0u, 3)
        };
        var westNeighbor = new List<LandTextureLayer>
        {
            BtxtLayer(NeighborBtxtEast, 1),
            BtxtLayer(NeighborBtxtEast, 3)
        };

        var table = CellLayerWeightTable.Build(defaultOwnLayers, westNeighborLayers: westNeighbor);

        Assert.NotNull(table);
        // West edge: 50% engine-default (this cell's own), 50% west neighbor's east BTXT.
        AssertTwoEntries(table!.At(0, 24), (0u, 0.5f), (NeighborBtxtEast, 0.5f));
        // One column inside: pure engine-default.
        AssertSingleEntry(table.At(4, 24), 0u, 1f);
    }

    // -------- Pooled BuildInto parity (allocation-free hot path on the streaming workers) --------

    [Fact]
    public void BuildInto_ProducesIdenticalTableToAllocatingBuild()
    {
        // The streaming workers call BuildInto on a thread-local pooled CellLayerWeightTable
        // to avoid allocating a fresh 33×33 vertex grid + ATXT dense grids per cell. Behaviour
        // must be byte-identical to the allocating Build path so the visual output is
        // unchanged. Construct a representative input (4 distinct BTXTs + ATXT on SW's east
        // edge + east + north neighbors) so the test exercises the main quadrant loop AND all
        // four neighbor-edge helpers.
        const uint sweAtxt = 0x10010;
        var atxt = new LandTextureLayer
        {
            Kind = LandTextureLayerKind.Alpha,
            TextureFormId = sweAtxt,
            Quadrant = 0,
            Layer = 0,
            BlendEntries = BuildBlendEntriesAtEastEdge(1f)
        };
        var layers = new List<LandTextureLayer>
        {
            BtxtLayer(BtxtSw, 0),
            BtxtLayer(BtxtSe, 1),
            BtxtLayer(BtxtNw, 2),
            BtxtLayer(BtxtNe, 3),
            atxt
        };
        var eastNeighbor = new List<LandTextureLayer>
        {
            BtxtLayer(NeighborBtxtWest, 0),
            BtxtLayer(NeighborBtxtWest, 2)
        };
        var northNeighbor = new List<LandTextureLayer>
        {
            BtxtLayer(NeighborBtxtSouth, 0),
            BtxtLayer(NeighborBtxtSouth, 1)
        };

        var reference = CellLayerWeightTable.Build(
            layers, eastNeighbor, northNeighborLayers: northNeighbor);
        Assert.NotNull(reference);

        var pooled = new CellLayerWeightTable();
        var built1 = CellLayerWeightTable.BuildInto(
            pooled, layers, eastNeighbor, northNeighborLayers: northNeighbor);
        Assert.True(built1);
        AssertTablesEqual(reference!, pooled);

        // Invoke BuildInto a SECOND time on the same instance to catch reset bugs (e.g., a
        // vertex with Count==0 still carrying stale E0 from the prior call). Output must match.
        var built2 = CellLayerWeightTable.BuildInto(
            pooled, layers, eastNeighbor, northNeighborLayers: northNeighbor);
        Assert.True(built2);
        AssertTablesEqual(reference!, pooled);
    }

    [Fact]
    public void BuildInto_AfterEmptyInputs_ResetsClean()
    {
        // BuildInto on empty layers must clear the table (return false) and leave the next
        // build with non-empty layers seeing a fresh start.
        var pooled = new CellLayerWeightTable();
        var first = CellLayerWeightTable.BuildInto(pooled, FourDistinctBtxts());
        Assert.True(first);

        var emptyResult = CellLayerWeightTable.BuildInto(pooled, new List<LandTextureLayer>());
        Assert.False(emptyResult);
        // Every vertex must be cleared after the empty reset.
        for (var i = 0; i < pooled.Vertices.Length; i++)
        {
            Assert.Equal(0, pooled.Vertices[i].Count);
        }

        // Refill from a fresh layer set; output should equal a brand-new Build.
        var reference = CellLayerWeightTable.Build(FourDistinctBtxts());
        var second = CellLayerWeightTable.BuildInto(pooled, FourDistinctBtxts());
        Assert.True(second);
        AssertTablesEqual(reference!, pooled);
    }

    private static void AssertTablesEqual(CellLayerWeightTable expected, CellLayerWeightTable actual)
    {
        Assert.Equal(expected.Vertices.Length, actual.Vertices.Length);
        for (var i = 0; i < expected.Vertices.Length; i++)
        {
            ref var e = ref expected.Vertices[i];
            ref var a = ref actual.Vertices[i];
            Assert.Equal(e.Count, a.Count);
            if (e.Count > 0) AssertEntryEqual(in e.E0, in a.E0);
            if (e.Count > 1) AssertEntryEqual(in e.E1, in a.E1);
            if (e.Count > 2) AssertEntryEqual(in e.E2, in a.E2);
            if (e.Count > 3) AssertEntryEqual(in e.E3, in a.E3);
            if (e.Count > 4)
            {
                Assert.NotNull(e.Overflow);
                Assert.NotNull(a.Overflow);
                for (var j = 0; j < e.Count - 4; j++)
                {
                    Assert.Equal(e.Overflow![j].FormId, a.Overflow![j].FormId);
                    Assert.Equal(e.Overflow![j].Weight, a.Overflow![j].Weight, 6);
                }
            }
        }
    }

    private static void AssertEntryEqual(in LayerWeight expected, in LayerWeight actual)
    {
        Assert.Equal(expected.FormId, actual.FormId);
        Assert.Equal(expected.Weight, actual.Weight, 6);
    }

    private static List<LandTextureLayer> FourDistinctBtxts()
    {
        return new List<LandTextureLayer>
        {
            BtxtLayer(BtxtSw, 0),
            BtxtLayer(BtxtSe, 1),
            BtxtLayer(BtxtNw, 2),
            BtxtLayer(BtxtNe, 3)
        };
    }

    private static LandTextureLayer BtxtLayer(uint formId, byte quadrant)
    {
        return new LandTextureLayer
        {
            Kind = LandTextureLayerKind.Base,
            TextureFormId = formId,
            Quadrant = quadrant,
            Layer = 0
        };
    }

    /// <summary>
    ///     Build a VTXT BlendEntries list with opacity at all 17 vertices on the east edge
    ///     of the quadrant (qx=16, qy=0..16); zero elsewhere.
    /// </summary>
    private static List<LandTextureBlendEntry> BuildBlendEntriesAtEastEdge(float opacity)
    {
        var entries = new List<LandTextureBlendEntry>();
        for (var qy = 0; qy < 17; qy++)
        {
            // Position = qy * 17 + qx with qx=16.
            entries.Add(new LandTextureBlendEntry(
                (ushort)(qy * 17 + 16),
                0,
                0,
                opacity));
        }

        return entries;
    }

    private static void AssertSingleEntry(
        in VertexWeights v, uint formId, float expectedWeight)
    {
        Assert.Equal(1, v.Count);
        Assert.Equal(formId, v.E0.FormId);
        Assert.Equal(expectedWeight, v.E0.Weight, 4);
    }

    private static void AssertTwoEntries(
        in VertexWeights v,
        (uint FormId, float Weight) a,
        (uint FormId, float Weight) b)
    {
        Assert.Equal(2, v.Count);
        var actual = new Dictionary<uint, float>
        {
            [v.E0.FormId] = v.E0.Weight,
            [v.E1.FormId] = v.E1.Weight
        };
        Assert.True(actual.ContainsKey(a.FormId), $"Expected entry {a.FormId:X} not present");
        Assert.True(actual.ContainsKey(b.FormId), $"Expected entry {b.FormId:X} not present");
        Assert.Equal(a.Weight, actual[a.FormId], 4);
        Assert.Equal(b.Weight, actual[b.FormId], 4);
    }
}