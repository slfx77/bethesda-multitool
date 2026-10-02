using BethesdaMultitool.Core.Modeling.Nif;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     The strip and list rules of <see cref="NifStripTriangulator" />, pinned against hand-derived triangles and against
///     geometry: vertices are laid out as a zigzag so that a correctly wound strip gives every triangle a +Z normal. Each
///     control is a plausible wrong implementation written here, and each must fail the same check the reader passes.
/// </summary>
public class NifStripTriangulatorTests
{
    /// <summary>
    ///     Strip [0 1 2 3 4] gives (0,1,2), (1,3,2), (2,3,4) and every triangle faces +Z. Control: without the odd flip
    ///     the second triangle is (1,2,3), which faces -Z.
    /// </summary>
    [Fact]
    public void Strip_FlipsOddTriangles()
    {
        ushort[][] strips = [[0, 1, 2, 3, 4]];

        var result = NifStripTriangulator.Triangulate(strips);

        Assert.Equal([0, 1, 2, 1, 3, 2, 2, 3, 4], result.Indices);
        Assert.Equal(3, result.StoredTriangles);
        Assert.Equal(0, result.DroppedTriangles);
        Assert.True(AllFacePositiveZ(result.Indices, Zigzag));

        var noFlip = NoFlip(strips);
        Assert.NotEqual(result.Indices, noFlip);
        Assert.False(AllFacePositiveZ(noFlip, Zigzag));
    }

    /// <summary>
    ///     Two strips (a b c) and (d e f g) stitched with an odd run of repeats: parity counts from the strip start,
    ///     dropped triangles included, so d e f g keeps its standalone winding. Control: counting parity over emitted
    ///     triangles only flips the second strip, which then faces -Z.
    /// </summary>
    [Fact]
    public void Strip_ParityCountsSkippedTriangles()
    {
        ushort[][] strips = [[0, 1, 2, 2, 2, 3, 3, 4, 5, 6]];

        var result = NifStripTriangulator.Triangulate(strips);

        Assert.Equal([0, 1, 2, 3, 4, 5, 4, 6, 5], result.Indices);
        Assert.Equal(8, result.StoredTriangles);
        Assert.Equal([1, 2, 3, 4, 5], result.DroppedOrdinals);
        Assert.Equal([5], result.DroppedPerStrip);
        Assert.True(AllFacePositiveZ(result.Indices, Stitched));

        var emittedParity = EmittedParity(strips);
        Assert.NotEqual(result.Indices, emittedParity);
        Assert.False(AllFacePositiveZ(emittedParity, Stitched));
    }

    /// <summary>
    ///     Parity restarts at every strip. Control: a parity counter carried across strips flips the second strip's first
    ///     triangle (strip 0 stores an odd number of triangles), which then faces -Z.
    /// </summary>
    [Fact]
    public void Strip_ParityRestartsPerStrip()
    {
        ushort[][] strips = [[0, 1, 2], [3, 4, 5, 6]];

        var result = NifStripTriangulator.Triangulate(strips);

        Assert.Equal([0, 1, 2, 3, 4, 5, 4, 6, 5], result.Indices);
        Assert.True(AllFacePositiveZ(result.Indices, Stitched));

        var carried = CarriedParity(strips);
        Assert.False(AllFacePositiveZ(carried, Stitched));
    }

    /// <summary>
    ///     Repeated-index strip triangles are dropped and counted per strip with their global ordinals, and no kept
    ///     triangle repeats an index. Control: a triangulation that keeps them emits faces repeating a vertex, which the
    ///     Blender writer reports as Dropped.
    /// </summary>
    [Fact]
    public void Strip_RepeatedIndexTriangles_AreDroppedAndCounted()
    {
        ushort[][] strips = [[0, 0, 1, 2], [3, 4, 4, 5], [7, 8]];

        var result = NifStripTriangulator.Triangulate(strips);

        Assert.Equal([0, 2, 1], result.Indices);
        Assert.Equal(4, result.StoredTriangles);
        Assert.Equal(1, result.KeptTriangles);
        Assert.Equal([0, 2, 3], result.DroppedOrdinals);
        Assert.Equal([1, 2, 0], result.DroppedPerStrip);
        Assert.False(RepeatsAnIndex(result.Indices));

        Assert.True(RepeatsAnIndex(KeepAll(strips)));
    }

    /// <summary>
    ///     A triangle list drops exactly the triangles repeating an index (the measured landscape-LOD stitching and the
    ///     armor case (804, 805, 804)), in order. Control: the unfiltered list repeats an index.
    /// </summary>
    [Fact]
    public void List_RepeatedIndexTriangles_AreDroppedAndCounted()
    {
        ushort[] list = [0, 1, 2, 804, 805, 804, 5, 5, 5, 2, 1, 4];

        var result = NifStripTriangulator.FilterList(list);

        Assert.Equal([0, 1, 2, 2, 1, 4], result.Indices);
        Assert.Equal(4, result.StoredTriangles);
        Assert.Equal([1, 2], result.DroppedOrdinals);
        Assert.Empty(result.DroppedPerStrip);
        Assert.False(RepeatsAnIndex(result.Indices));

        Assert.True(RepeatsAnIndex(list.Select(i => (int)i).ToArray()));
    }

    /// <summary>The rule reads indices only: a list triangle of three distinct indices is kept whatever its area.</summary>
    [Fact]
    public void List_DistinctIndices_AreAlwaysKept()
    {
        ushort[] list = [3, 2, 1];

        var result = NifStripTriangulator.FilterList(list);

        Assert.Equal([3, 2, 1], result.Indices);
        Assert.Equal(0, result.DroppedTriangles);
    }

    [Fact]
    public void Strip_ShorterThanThree_StoresNothing()
    {
        var result = NifStripTriangulator.Triangulate([[1, 2], [4], []]);

        Assert.Empty(result.Indices);
        Assert.Equal(0, result.StoredTriangles);
        Assert.Equal([0, 0, 0], result.DroppedPerStrip);
    }

    /// <summary>Vertex k of a strip sits at (k / 2, 1 for even k else 0): a correctly wound strip faces +Z.</summary>
    private static (float X, float Y) Zigzag(int k)
    {
        return (k / 2, k % 2 == 0 ? 1f : 0f);
    }

    /// <summary>Vertices 0-2 are strip (a b c) and 3-6 strip (d e f g), each its own zigzag.</summary>
    private static (float X, float Y) Stitched(int k)
    {
        return k < 3 ? Zigzag(k) : (10f + (k - 3) / 2, (k - 3) % 2 == 0 ? 1f : 0f);
    }

    private static bool AllFacePositiveZ(int[] indices, Func<int, (float X, float Y)> position)
    {
        for (var t = 0; t < indices.Length; t += 3)
        {
            var (ax, ay) = position(indices[t]);
            var (bx, by) = position(indices[t + 1]);
            var (cx, cy) = position(indices[t + 2]);
            if ((bx - ax) * (cy - ay) - (by - ay) * (cx - ax) <= 0f)
            {
                return false;
            }
        }

        return true;
    }

    private static bool RepeatsAnIndex(int[] indices)
    {
        for (var t = 0; t + 2 < indices.Length; t += 3)
        {
            if (indices[t] == indices[t + 1] || indices[t + 1] == indices[t + 2] || indices[t] == indices[t + 2])
            {
                return true;
            }
        }

        return false;
    }

    private static void Add(List<int> indices, int a, int b, int c)
    {
        indices.Add(a);
        indices.Add(b);
        indices.Add(c);
    }

    private static void Emit(List<int> indices, ushort[] p, int i, bool flip)
    {
        if (flip)
        {
            Add(indices, p[i], p[i + 2], p[i + 1]);
        }
        else
        {
            Add(indices, p[i], p[i + 1], p[i + 2]);
        }
    }

    /// <summary>Control: a triangulation that never flips odd triangles.</summary>
    private static int[] NoFlip(ushort[][] strips)
    {
        var indices = new List<int>();
        foreach (var p in strips)
        {
            for (var i = 0; i + 2 < p.Length; i++)
            {
                if (p[i] != p[i + 1] && p[i + 1] != p[i + 2] && p[i] != p[i + 2])
                {
                    Add(indices, p[i], p[i + 1], p[i + 2]);
                }
            }
        }

        return [.. indices];
    }

    /// <summary>Control: parity counted over emitted triangles instead of stored positions.</summary>
    private static int[] EmittedParity(ushort[][] strips)
    {
        var indices = new List<int>();
        foreach (var p in strips)
        {
            var emitted = 0;
            for (var i = 0; i + 2 < p.Length; i++)
            {
                if (p[i] == p[i + 1] || p[i + 1] == p[i + 2] || p[i] == p[i + 2])
                {
                    continue;
                }

                Emit(indices, p, i, emitted++ % 2 == 1);
            }
        }

        return [.. indices];
    }

    /// <summary>Control: one parity counter carried across strips.</summary>
    private static int[] CarriedParity(ushort[][] strips)
    {
        var indices = new List<int>();
        var position = 0;
        foreach (var p in strips)
        {
            for (var i = 0; i + 2 < p.Length; i++, position++)
            {
                if (p[i] == p[i + 1] || p[i + 1] == p[i + 2] || p[i] == p[i + 2])
                {
                    continue;
                }

                Emit(indices, p, i, position % 2 == 1);
            }
        }

        return [.. indices];
    }

    /// <summary>Control: the parity rule without dropping repeated-index triangles.</summary>
    private static int[] KeepAll(ushort[][] strips)
    {
        var indices = new List<int>();
        foreach (var p in strips)
        {
            for (var i = 0; i + 2 < p.Length; i++)
            {
                Emit(indices, p, i, i % 2 == 1);
            }
        }

        return [.. indices];
    }
}
