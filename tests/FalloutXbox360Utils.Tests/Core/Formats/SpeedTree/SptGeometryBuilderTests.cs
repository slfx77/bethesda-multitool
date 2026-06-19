using System.Numerics;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.SpeedTree;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.SpeedTree;

public class SptGeometryBuilderTests
{
    // ---- Texture path remapping ----

    [Theory]
    [InlineData(@"C:\Noah\Fallout\Trees\WastelandShrub01\WastelandShrub01Bark.tga",
        @"textures\trees\branches\wastelandshrub01bark.dds")]
    [InlineData(@"C:\Noah\Fallout\Trees\WastelandShrub01\WastelandShrub01Foliage01.dds",
        @"textures\trees\leaves\wastelandshrub01foliage.dds")]
    [InlineData(@"C:\Noah\Fallout\Trees\WastelandShrub01\WastelandShrub01Foliage02.dds",
        @"textures\trees\leaves\wastelandshrub01foliage.dds")]
    [InlineData("WastelandShrub01.tga", @"textures\trees\wastelandshrub01.dds")]
    public void TexturePath_MapsDevPathToGamePath(string dev, string expected)
    {
        Assert.Equal(expected, SpeedTreeTexturePath.ToGamePath(dev));
    }

    [Fact]
    public void TexturePath_NullOrEmpty_ReturnsNull()
    {
        Assert.Null(SpeedTreeTexturePath.ToGamePath(null));
        Assert.Null(SpeedTreeTexturePath.ToGamePath("   "));
    }

    // ---- Archive path mapping (the bug that made trees invisible: .spt lives under trees\, not meshes\) ----

    [Theory]
    [InlineData(@"\WastelandShrub01.spt", @"trees\WastelandShrub01.spt")]
    [InlineData("WastelandShrub01.spt", @"trees\WastelandShrub01.spt")]
    [InlineData(@"trees\Pine01.spt", @"trees\Pine01.spt")]
    [InlineData(@"Trees/OasisElm01.spt", @"Trees\OasisElm01.spt")]
    [InlineData(@"meshes\trees\Sycamore.spt", @"trees\Sycamore.spt")]
    public void ModelPath_MapsTreeModlToTreesArchivePath(string modl, string expected)
    {
        Assert.True(SpeedTreeModelPath.IsSpt(modl));
        Assert.Equal(expected, SpeedTreeModelPath.ToArchivePath(modl));
    }

    // ---- Geometry generation (synthetic model — no sample file needed) ----

    [Fact]
    public void Build_SyntheticModel_ProducesValidBarkAndLeafGeometry()
    {
        var model = new SptModel
        {
            General = new SptGeneralParams { BarkTexturePath = @"C:\x\OakBark.tga" },
            Branches = [MakeLeafyBranch()],
            Leaves =
            [
                new SptLeaf
                {
                    Material = @"C:\x\OakFoliage01.dds",
                    Corner0 = new Vector3(0.5f, 0.25f, 0),
                    Corner1 = new Vector3(0.1f, 0.1f, 0),
                    Corner2 = new Vector3(6f, 6f, 0)
                }
            ]
        };

        var result = SptGeometryBuilder.Build(model, 12345);

        Assert.True(result.HasGeometry);

        var bark = result.Submeshes.Single(s => s.ShapeName == "spt:bark");
        Assert.Contains("branches", bark.DiffuseTexturePath!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bark", bark.DiffuseTexturePath!, StringComparison.OrdinalIgnoreCase);
        Assert.False(bark.IsDoubleSided);

        var leaf = result.Submeshes.Single(s => s.ShapeName == "spt:leaves");
        Assert.Contains("leaves", leaf.DiffuseTexturePath!, StringComparison.OrdinalIgnoreCase);
        Assert.True(leaf.IsDoubleSided);
        Assert.True(leaf.HasAlphaTest);

        foreach (var sub in result.Submeshes)
        {
            AssertValidMesh(sub);
        }

        // Bounds are finite and non-degenerate.
        Assert.True(float.IsFinite(result.Width) && result.Width > 0);
        Assert.True(float.IsFinite(result.Height) && result.Height > 0);
    }

    [Fact]
    public void Build_ScalesTreeToTargetHeightFromObnd()
    {
        var model = new SptModel
        {
            General = new SptGeneralParams { BarkTexturePath = @"C:\x\OakBark.tga" },
            Branches = [MakeLeafyBranch()],
            Leaves = [new SptLeaf { Material = @"C:\x\OakFoliage.dds", Corner2 = new Vector3(6, 6, 0) }]
        };

        var result = SptGeometryBuilder.Build(model, 7, new SptGeometryOptions { TargetHeight = 175f });

        // The tree grows along +Z (FNV world-up); NifRenderableModel labels the Z-extent "Depth".
        // The whole tree (branches + leaves) is resized so its vertical extent matches the OBND value.
        Assert.InRange(result.Depth, 175f * 0.98f, 175f * 1.02f);
    }

    [Fact]
    public void Build_IsDeterministicForSameSeed()
    {
        var model = new SptModel
        {
            General = new SptGeneralParams { BarkTexturePath = @"C:\x\OakBark.tga" },
            Branches = [MakeLeafyBranch()],
            Leaves = [new SptLeaf { Material = @"C:\x\OakFoliage.dds", Corner2 = new Vector3(5, 5, 0) }]
        };

        var a = SptGeometryBuilder.Build(model, 999);
        var b = SptGeometryBuilder.Build(model, 999);

        Assert.Equal(a.Submeshes.Count, b.Submeshes.Count);
        for (var i = 0; i < a.Submeshes.Count; i++)
        {
            Assert.Equal(a.Submeshes[i].Positions, b.Submeshes[i].Positions);
            Assert.Equal(a.Submeshes[i].Triangles, b.Submeshes[i].Triangles);
        }
    }

    [Fact]
    public void Build_RealShrub_ProducesTexturedTree()
    {
        var path = SampleFileFixture.FindSamplePath(@"Sample\Meshes\meshes_360_final\trees\wastelandshrub01.spt")
                   ?? SampleFileFixture.FindSamplePath(@"Sample\Meshes\meshes_360_proto\trees\wastelandshrub01.spt");
        Assert.SkipWhen(path is null, "Missing sample: wastelandshrub01.spt");

        var model = SptFile.Parse(File.ReadAllBytes(path!));
        var result = SptGeometryBuilder.Build(model, 0x1234);

        Assert.True(result.HasGeometry);
        Assert.Contains(result.Submeshes, s => s.ShapeName == "spt:bark");
        Assert.Contains(result.Submeshes, s => s.ShapeName == "spt:leaves");
        foreach (var sub in result.Submeshes)
        {
            AssertValidMesh(sub);
        }
    }

    [Fact]
    public void Build_LeafCount_TruncatesRawSpawnCount()
    {
        var model = MakeSingleLevelLeafModel(leafFrequency: 2.6f);

        var result = SptGeometryBuilder.Build(model, 1, BillboardOptions());

        Assert.Equal(2, CountLeafQuads(result));
    }

    [Fact]
    public void Build_ChildCount_TruncatesRawSpawnCount()
    {
        var model = new SptModel
        {
            General = new SptGeneralParams { BarkTexturePath = @"C:\x\OakBark.tga", Float2006 = 100f },
            Branches =
            [
                MakeBranch(1f, 0.02f, 2.6f, 3, 1),
                MakeBranch(0.5f, 0.01f, 0f, 3, 1),
                MakeBranch(0.05f, 0.005f, 0f, 3, 1)
            ],
            Leaves = [new SptLeaf { Material = @"C:\x\OakFoliage.dds", Corner1 = new Vector3(0.01f) }],
            LeafTable = new SptLeafTable { Float3007 = 0.5f, UInt3008 = 0 }
        };

        var result = SptGeometryBuilder.Build(model, 1, BillboardOptions());

        var bark = result.Submeshes.Single(s => s.ShapeName == "spt:bark");
        Assert.Equal(30, bark.Positions.Length / 3); // root + two children, 10 vertices each
    }

    [Fact]
    public void Build_ChildSpawn_SamplesByCumulativeBranchDistance()
    {
        var root = MakeBranch(1f, 0.001f, 1f, 3, 2,
                5f) with
            {
                Float6010 = 0.5f,
                Float6011 = 0.5f
            };
        var model = new SptModel
        {
            General = new SptGeneralParams { BarkTexturePath = @"C:\x\OakBark.tga", Float2006 = 100f },
            Branches =
            [
                root,
                MakeBranch(0.1f, 0.001f, 0f, 3, 1),
                MakeBranch(0f, 0.001f, 0f, 3, 1)
            ],
            Leaves = [new SptLeaf { Material = @"C:\x\OakFoliage.dds", Corner1 = new Vector3(0.01f) }],
            LeafTable = new SptLeafTable { Float3007 = 0.5f, UInt3008 = 0 }
        };

        var result = SptGeometryBuilder.Build(model, 1, BillboardOptions());

        var bark = result.Submeshes.Single(s => s.ShapeName == "spt:bark");
        var childBase = AveragePosition(bark.Positions, BranchVertexCount(3, 2), RingVertexCount(3));
        Assert.InRange(childBase.X, 45f, 55f);
    }

    [Fact]
    public void Build_ChildSpawn_RestartsRngFromBranchSeedPlusThree()
    {
        const uint seed = 100u;
        const float start = 0.2f;
        const float end = 0.8f;
        var root = MakeBranch(1f, 0.0001f, 1f, 3, 4) with
        {
            Float6010 = start,
            Float6011 = end
        };
        var model = new SptModel
        {
            General = new SptGeneralParams { BarkTexturePath = @"C:\x\OakBark.tga", Float2006 = 100f },
            Branches =
            [
                root,
                MakeBranch(0.1f, 0.0001f, 0f, 3, 1),
                MakeBranch(0f, 0.0001f, 0f, 3, 1)
            ],
            Leaves = [new SptLeaf { Material = @"C:\x\OakFoliage.dds", Corner1 = new Vector3(0.01f) }],
            LeafTable = new SptLeafTable { Float3007 = 0.5f, UInt3008 = 0 }
        };

        var result = SptGeometryBuilder.Build(model, seed, BillboardOptions());

        // CIdvBranch::Compute increments the branch-local seed by 3, reseeds, then uses the first child
        // range's 85%-95% window for child 0. A continuous parent RNG stream produces a different X.
        var firstChildMin = start + (end - start) * 0.85f;
        var firstChildMax = start + (end - start) * 0.95f;
        var expectedFrac = SpeedTreeRangeAfterReseed(seed + 3u, firstChildMin, firstChildMax);
        var bark = result.Submeshes.Single(s => s.ShapeName == "spt:bark");
        var childBase = AveragePosition(bark.Positions, BranchVertexCount(3, 4), RingVertexCount(3));

        Assert.Equal(expectedFrac * 100f, childBase.X, 2);
    }

    [Fact]
    public void Build_BudReach_DividesTerminalLengthByTerminalUInt6009()
    {
        var model = MakeSingleLevelLeafModel(0.4f, 2.5f, 4);

        var result = SptGeometryBuilder.Build(model, 1, BillboardOptions());

        var center = ReadVector3(result.Submeshes.Single(s => s.ShapeName == "spt:leaves").Tangents!, 0);
        Assert.Equal(10f, center.Length(), 4);
    }

    [Fact]
    public void Build_LeafCardOffsets_UseCorner0PivotAndDirectCorner1Size()
    {
        var model = MakeSingleLevelLeafModel(
            leafFrequency: 1f,
            corner0: new Vector3(0.25f, 0.75f, 0f),
            corner1: new Vector3(0.2f, 0.4f, 0f));

        var result = SptGeometryBuilder.Build(model, 1, BillboardOptions());

        var offsets = result.Submeshes.Single(s => s.ShapeName == "spt:leaves").Bitangents!;
        AssertVector3(new Vector3(-5f, -30f, 0f), ReadVector3(offsets, 0));
        AssertVector3(new Vector3(15f, -30f, 0f), ReadVector3(offsets, 1));
        AssertVector3(new Vector3(15f, 10f, 0f), ReadVector3(offsets, 2));
        AssertVector3(new Vector3(-5f, 10f, 0f), ReadVector3(offsets, 3));
    }

    [Fact]
    public void Build_LeafCardUvs_UseParsedTextureCoordBlock()
    {
        var uv = new SptLeafTextureCoords(
            new Vector2(0.75f, 0.9f),
            new Vector2(0.25f, 0.9f),
            new Vector2(0.25f, 0.1f),
            new Vector2(0.75f, 0.1f));
        var model = MakeSingleLevelLeafModel(
            leafFrequency: 1f,
            leafTextureCoords: [uv],
            corner0: new Vector3(0.5f, 0.5f, 0f),
            corner1: new Vector3(0.1f, 0.2f, 0f));

        var result = SptGeometryBuilder.Build(model, 1, BillboardOptions());

        var uvs = result.Submeshes.Single(s => s.ShapeName == "spt:leaves").UVs!;
        Assert.Equal(0.75f, uvs[0], 4);
        Assert.Equal(0.9f, uvs[1], 4);
        Assert.Equal(0.25f, uvs[2], 4);
        Assert.Equal(0.9f, uvs[3], 4);
        Assert.Equal(0.25f, uvs[4], 4);
        Assert.Equal(0.1f, uvs[5], 4);
        Assert.Equal(0.75f, uvs[6], 4);
        Assert.Equal(0.1f, uvs[7], 4);
    }

    [Theory]
    [InlineData(0u, 4)]
    [InlineData(1u, 1)]
    [InlineData(2u, 2)]
    public void Build_RoomForLeaf_HonorsPlacementModeScope(uint placementMode, int expectedLeaves)
    {
        var model = MakePlacementModeModel(placementMode);

        var result = SptGeometryBuilder.Build(model, 1, BillboardOptions());

        Assert.Equal(expectedLeaves, CountLeafQuads(result));
    }

    /// <summary>
    ///     A minimal but realistic single-level branch: a length spline (slot 4), radius (slot 5), constant
    ///     taper (slot 6), and a leaf frequency (Float6012). The builder spawns leaves as a branch's
    ///     "children" only when the child level would be the terminal leaf-stub record, with count =
    ///     Float6012·length/treeSize — so a default <c>SptBranch()</c> (Float6012 = 0, no length spline)
    ///     correctly produces no leaves. These tests need real values to exercise the leaf path.
    /// </summary>
    private static SptBranch MakeLeafyBranch(float leafFrequency = 40f)
    {
        return new SptBranch
        {
            Splines = BuildSlots(1f, 0.04f),
            UInt6008 = 5,
            UInt6009 = 5,
            Float6010 = 0f,
            Float6011 = 1f,
            Float6012 = leafFrequency,
            Float6014 = 1f
        };
    }

    private static SptModel MakeSingleLevelLeafModel(
        float length = 1f,
        float leafFrequency = 1f,
        uint terminalUInt6009 = 1,
        string material = @"C:\x\OakFoliage.dds",
        IReadOnlyList<SptLeafTextureCoords>? leafTextureCoords = null,
        Vector3? corner0 = null,
        Vector3? corner1 = null)
    {
        return new SptModel
        {
            General = new SptGeneralParams { BarkTexturePath = @"C:\x\OakBark.tga", Float2006 = 100f },
            Branches =
            [
                MakeBranch(length, 0.02f, leafFrequency, 3, terminalUInt6009)
            ],
            Leaves =
            [
                new SptLeaf
                {
                    Material = material,
                    Corner0 = corner0 ?? new Vector3(0.5f, 0.5f, 0f),
                    Corner1 = corner1 ?? new Vector3(0.01f, 0.01f, 0f)
                }
            ],
            LeafTextureCoords = leafTextureCoords ?? [],
            LeafTable = new SptLeafTable { Float3007 = 0.5f, UInt3008 = 0 }
        };
    }

    private static SptModel MakePlacementModeModel(uint placementMode)
    {
        return new SptModel
        {
            General = new SptGeneralParams { BarkTexturePath = @"C:\x\OakBark.tga", Float2006 = 100f },
            Branches =
            [
                MakeBranch(1f, 0.02f, 2f, 3, 1),
                MakeBranch(1f, 0.01f, 2f, 3, 1),
                MakeBranch(0f, 0.005f, 0f, 3, 1)
            ],
            Leaves = [new SptLeaf { Material = @"C:\x\OakFoliage.dds", Corner1 = new Vector3(0.5f, 0.5f, 0f) }],
            LeafTable = new SptLeafTable { Float3007 = 1f, UInt3008 = placementMode }
        };
    }

    private static SptBranch MakeBranch(float length, float radius, float frequency, uint u6008, uint u6009,
        float pathExponent = 1f)
    {
        return new SptBranch
        {
            Splines = BuildSlots(length, radius),
            UInt6008 = u6008,
            UInt6009 = u6009,
            Float6010 = 0f,
            Float6011 = 0f,
            Float6012 = frequency,
            Float6014 = pathExponent
        };
    }

    private static SptGeometryOptions BillboardOptions()
    {
        return new SptGeometryOptions
        {
            LeafBillboard = true,
            CrossedLeafCards = false
        };
    }

    private static int CountLeafQuads(NifRenderableModel model)
    {
        var leaf = model.Submeshes.Single(s => s.ShapeName == "spt:leaves");
        return leaf.Positions.Length / 12;
    }

    private static Vector3 ReadVector3(float[] values, int vertex)
    {
        var i = vertex * 3;
        return new Vector3(values[i], values[i + 1], values[i + 2]);
    }

    private static int RingVertexCount(uint u6008)
    {
        return (int)u6008 + 2;
    }

    private static int BranchVertexCount(uint u6008, uint u6009)
    {
        return ((int)u6009 + 1) * RingVertexCount(u6008);
    }

    private static Vector3 AveragePosition(float[] positions, int startVertex, int count)
    {
        var sum = Vector3.Zero;
        for (var i = 0; i < count; i++)
        {
            sum += ReadVector3(positions, startVertex + i);
        }

        return sum / count;
    }

    private static void AssertVector3(Vector3 expected, Vector3 actual, int precision = 4)
    {
        Assert.Equal(expected.X, actual.X, precision);
        Assert.Equal(expected.Y, actual.Y, precision);
        Assert.Equal(expected.Z, actual.Z, precision);
    }

    private static float SpeedTreeRangeAfterReseed(uint seed, float min, float max)
    {
        var state = seed <= 1 ? 1 : (int)seed;
        var shuffle = new float[128];
        for (var i = 0; i < shuffle.Length; i++)
        {
            shuffle[i] = Raw(ref state);
        }

        var index = (int)(Raw(ref state) * 128f);
        var value = shuffle[index];
        return min + (max - min) * value;

        static float Raw(ref int state)
        {
            var hi = state / 127773;
            var lo = state - hi * 127773;
            state = 16807 * lo - 2836 * hi;
            if (state < 1)
            {
                state += 2147483647;
            }

            return state * 4.656613e-10f;
        }
    }

    private static SptBezierSpline?[] BuildSlots(float length, float radius)
    {
        var slots = new SptBezierSpline?[9];
        slots[4] = new SptBezierSpline { Header = new Vector3(length, length, 0f) };
        slots[5] = new SptBezierSpline { Header = new Vector3(radius, radius, 0f) };
        slots[6] = new SptBezierSpline { Header = new Vector3(1f, 1f, 0f) };
        return slots;
    }

    private static void AssertValidMesh(RenderableSubmesh sub)
    {
        Assert.True(sub.Positions.Length > 0 && sub.Positions.Length % 3 == 0);
        Assert.True(sub.Triangles.Length > 0 && sub.Triangles.Length % 3 == 0);
        Assert.Equal(sub.Positions.Length, sub.Normals!.Length);
        Assert.Equal(sub.Positions.Length / 3 * 2, sub.UVs!.Length);

        var vertexCount = sub.Positions.Length / 3;
        Assert.True(vertexCount <= 65535, "vertex count must stay within ushort index range");
        foreach (var p in sub.Positions)
        {
            Assert.True(float.IsFinite(p));
        }

        foreach (var idx in sub.Triangles)
        {
            Assert.True(idx < vertexCount);
        }
    }
}