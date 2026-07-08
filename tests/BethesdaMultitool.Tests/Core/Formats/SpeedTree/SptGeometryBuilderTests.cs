using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.SpeedTree;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.SpeedTree;

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
        // The builder lofts at the engine's ×10 world scale (treeSizeMid = Float2006·10 = 1000), so the
        // child attaches at frac·1000 ≈ 500 rather than the pre-×10 ≈ 50.
        Assert.InRange(childBase.X, 450f, 550f);
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

        Assert.Equal(expectedFrac * 1000f, childBase.X, 2); // ×10 engine world scale (Float2006·10)
    }

    [Fact]
    public void Build_ChildBranchRadius_ClampsToRecoveredParentRadiusLimit()
    {
        var model = new SptModel
        {
            General = new SptGeneralParams { BarkTexturePath = @"C:\x\OakBark.tga", Float2006 = 100f },
            Branches =
            [
                MakeBranch(1f, 0.02f, 1f, 3, 1, taperEnd: 0.25f) with { Float6010 = 0.5f, Float6011 = 0.5f },
                MakeBranch(0.5f, 0.05f, 0f, 3, 1),
                MakeBranch(0f, 0.005f, 0f, 3, 1)
            ],
            LeafTable = new SptLeafTable { Float3007 = 0.5f, UInt3008 = 0 }
        };

        var result = SptGeometryBuilder.Build(model, 1, BillboardOptions());

        var bark = result.Submeshes.Single(s => s.ShapeName == "spt:bark");
        var childBase = BranchVertexCount(3, 1);
        var uniqueRingVertices = RingVertexCount(3) - 1;
        var center = AveragePosition(bark.Positions, childBase, uniqueRingVertices);
        var radius = Vector3.Distance(center, ReadVector3(bark.Positions, childBase));

        // ×10 engine world scale (Float2006·10). Tolerance (not 2-decimal rounding): the measured radius is
        // 10.625 ± a 1e-6 float wobble from the engine-exact deg→rad division, which straddles the x.625 boundary.
        Assert.Equal(1000f * 0.02f * 0.625f * 0.85f, radius, 0.01f);
    }

    [Fact]
    public void Build_BudReach_DividesTerminalLengthByTerminalUInt6009()
    {
        var model = MakeSingleLevelLeafModel(0.4f, 2.5f, 4);

        var result = SptGeometryBuilder.Build(model, 1, BillboardOptions());

        var center = ReadVector3(result.Submeshes.Single(s => s.ShapeName == "spt:leaves").Tangents!, 0);
        Assert.Equal(100f, center.Length(), 4); // ×10 engine world scale (budReach ∝ treeSize = Float2006·10)
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
        // bitangent.xy = the card corner offset (Corner0 pivot + Corner1 size). bitangent.z now carries
        // the per-leaf wind weight (a [0.15,1] blend factor for the leaf-billboard VS sway), shared by a
        // leaf's four corners, so it is asserted separately from the offset math under test here.
        // Card size = cardScale·Corner1 with cardScale = treeSizeMid = Float2006·10 (×10 engine world scale).
        // Vertex order follows the ENGINE's zip (BSTreeModel::CreateLeafGeometry pairs texcoord j with
        // CLeafGeometry::Update corner slot (j+2)&3): pairs 0..3 land on (L,B), (R,B), (R,T), (L,T) —
        // around the quad. Seed 1 picks the ODD doubled-entry here (authored pivot 0.25 → x0=−50, x1=150);
        // the EVEN variant would flip the pivot (x0=−150, x1=50).
        AssertOffsetXy(offsets, 0, -50f, -300f);
        AssertOffsetXy(offsets, 1, 150f, -300f);
        AssertOffsetXy(offsets, 2, 150f, 100f);
        AssertOffsetXy(offsets, 3, -50f, 100f);
        var windWeight = offsets[2];
        Assert.InRange(windWeight, 0.15f, 1f);
        Assert.Equal(windWeight, offsets[5], 4);
        Assert.Equal(windWeight, offsets[8], 4);
        Assert.Equal(windWeight, offsets[11], 4);
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
        // The builder flips V (v → 1−v) on the .spt's leaf UVs (the shipped composite DDS is V-flipped
        // vs the token-10002 table; the engine's own mechanism is SetTextureCoords' global vSign=−1),
        // so the parsed 0.9 → 0.1 and 0.1 → 0.9. Seed 1 picks the ODD doubled-entry, which additionally
        // U-mirrors the pairs (u of pairs 0↔1 and 2↔3 swap, v's stay — SetTextureCoords' second entry):
        // pair0.u = parsed pair1's 0.25, pair1.u = parsed pair0's 0.75, and likewise for pairs 2/3.
        Assert.Equal(0.25f, uvs[0], 4);
        Assert.Equal(0.1f, uvs[1], 4);
        Assert.Equal(0.75f, uvs[2], 4);
        Assert.Equal(0.1f, uvs[3], 4);
        Assert.Equal(0.75f, uvs[4], 4);
        Assert.Equal(0.9f, uvs[5], 4);
        Assert.Equal(0.25f, uvs[6], 4);
        Assert.Equal(0.9f, uvs[7], 4);
    }

    [Fact]
    public void Build_LeafBillboards_CarryMakeLeafLightingNormal()
    {
        var model = MakeSingleLevelLeafModel(leafFrequency: 1f);

        var result = SptGeometryBuilder.Build(model, 1, BillboardOptions());

        // The leaf lighting normal is CIdvBranch::MakeLeaf's: lerp(outward-from-branch-origin,
        // bud direction, texture variance), normalized — the per-leaf normal the engine's STLEAF
        // shaders consume. For this fixture (a single vertical branch with the leaf near its top),
        // outward-from-origin points up-hemisphere, so the normal must be unit length with Z > 0.
        // The previous pin asserted an invented up-DOMINANT "canopy normal" (adversarial review:
        // that constant blend has no engine counterpart).
        var normal = ReadVector3(result.Submeshes.Single(s => s.ShapeName == "spt:leaves").Normals!, 0);
        Assert.InRange(normal.Length(), 0.99f, 1.01f);
        Assert.True(normal.Z > 0f, $"outward-from-origin normal on a vertical branch should point up-hemisphere, was {normal}");
    }

    [Fact]
    public void Build_BlossomGate_ReplacesLeafBudInsteadOfAddingSecondPopulation()
    {
        var model = MakeLeafBlossomGateModel(blossomProbability: 1f);

        var result = SptGeometryBuilder.Build(model, 1, BillboardOptions());

        Assert.Equal(4, CountAllLeafQuads(result));
        Assert.Equal(0, CountLeafQuads(result, "leafatlas"));
        Assert.Equal(4, CountLeafQuads(result, "blossomatlas"));
    }

    [Fact]
    public void Build_BlossomGate_ProbabilityZeroUsesLeafPool()
    {
        var model = MakeLeafBlossomGateModel(blossomProbability: 0f);

        var result = SptGeometryBuilder.Build(model, 1, BillboardOptions());

        Assert.Equal(4, CountAllLeafQuads(result));
        Assert.Equal(4, CountLeafQuads(result, "leafatlas"));
        Assert.Equal(0, CountLeafQuads(result, "blossomatlas"));
    }

    /// <summary>
    ///     <c>CIdvBranch::RoomForLeaf</c> (360 0x829753D8) + <c>MakeLeaf</c> reject a candidate against ONE
    ///     global leaf-manager array (<c>CTreeEngine+0x84+0x10</c>), not a per-branch list — confirmed by the
    ///     runtime oracle (WastelandShrub: per-branch scoping over-spawned 404 cards vs the engine's 53; global
    ///     rejection yields 62). So mode 0 disables rejection (all 4 candidates kept) and BOTH non-zero modes
    ///     reject globally (the mode-2 "scope" is per-tree-instance, which collapses to global for a single
    ///     built tree) → 1 card survives the cluster.
    /// </summary>
    [Theory]
    [InlineData(0u, 4)]
    [InlineData(1u, 1)]
    [InlineData(2u, 1)]
    public void Build_RoomForLeaf_HonorsPlacementMode(uint placementMode, int expectedLeaves)
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

    [Fact]
    public void Build_TrunkNoise_RestoringPullBoundsLean()
    {
        // Japanesemaple-class trunk: many rings × large slot-0 angular noise. Without the restoring
        // pull the per-ring noise random-walks the stem into a visible lean; with it the walk
        // saturates, so the tip stays near the vertical axis across seeds.
        foreach (var seed in new uint[] { 1, 7, 1234, 99999 })
        {
            var result = SptGeometryBuilder.Build(MakeVerticalNoisyTrunkModel(12f, rings: 25), seed, BillboardOptions());
            var bark = result.Submeshes.Single(s => s.ShapeName == "spt:bark");
            var (tipXy, height) = TrunkTipOffset(bark);
            Assert.True(tipXy < height * 0.30f,
                $"seed {seed}: trunk tip drifted {tipXy:0.#} off-axis over height {height:0.#}");
        }
    }

    [Fact]
    public void Build_TrunkNoise_RestoringPullKeepsCharacter()
    {
        // The pull must BOUND the walk, not pin the stem dead-straight: a noisy trunk still ends
        // measurably off the axis (per-ring character survives).
        var result = SptGeometryBuilder.Build(MakeVerticalNoisyTrunkModel(12f, rings: 25), 7, BillboardOptions());
        var bark = result.Submeshes.Single(s => s.ShapeName == "spt:bark");
        var (tipXy, height) = TrunkTipOffset(bark);
        Assert.True(tipXy > height * 0.005f,
            $"trunk tip pinned to the axis (offset {tipXy:0.##} over height {height:0.#}) — over-restored");
    }

    /// <summary>A childless VERTICAL trunk (slot 7 = −90° declination, like real trees) whose slot-0
    /// noise ramps to ±<paramref name="noiseVarianceDeg" /> along the stem.</summary>
    private static SptModel MakeVerticalNoisyTrunkModel(float noiseVarianceDeg, uint rings)
    {
        var branch = MakeBranch(1f, 0.02f, 0f, 4, rings - 1);
        var slots = (SptBezierSpline?[])branch.Splines;
        slots[0] = new SptBezierSpline { Header = new Vector3(0f, 1f, noiseVarianceDeg) };
        slots[7] = new SptBezierSpline { Header = new Vector3(-90f, -90f, 0f) };
        return new SptModel
        {
            General = new SptGeneralParams { BarkTexturePath = @"C:\x\OakBark.tga", Float2006 = 100f },
            Branches = [branch],
            LeafTable = new SptLeafTable { Float3007 = 0.5f, UInt3008 = 0 }
        };
    }

    /// <summary>The highest bark vertex's horizontal distance from the trunk base axis, plus the trunk height.</summary>
    private static (float TipXy, float Height) TrunkTipOffset(RenderableSubmesh bark)
    {
        var tip = Vector3.Zero;
        foreach (var i in Enumerable.Range(0, bark.Positions.Length / 3))
        {
            var v = ReadVector3(bark.Positions, i);
            if (v.Z > tip.Z) tip = v;
        }

        return (MathF.Sqrt(tip.X * tip.X + tip.Y * tip.Y), tip.Z);
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

    private static SptModel MakeLeafBlossomGateModel(float blossomProbability)
    {
        return new SptModel
        {
            General = new SptGeneralParams { BarkTexturePath = @"C:\x\OakBark.tga", Float2006 = 100f },
            Branches =
            [
                // Spawn window (0.8, 1): IsBlossom gates on the bud's RAW percent along its branch
                // (the engine's percentAlongBranch), so the buds must genuinely sit beyond the 0.75
                // blossom-distance threshold — a remapped template parameter no longer qualifies.
                MakeBranch(1f, 0.02f, 4f, 3, 1) with { Float6010 = 0.8f, Float6011 = 1f }
            ],
            Leaves =
            [
                new SptLeaf
                {
                    Type = 0,
                    Material = @"C:\x\LeafAtlas.dds",
                    Corner0 = new Vector3(0.5f, 0.5f, 0f),
                    Corner1 = new Vector3(0.01f, 0.01f, 0f)
                },
                new SptLeaf
                {
                    Type = 1,
                    Material = @"C:\x\BlossomAtlas.dds",
                    Corner0 = new Vector3(0.5f, 0.5f, 0f),
                    Corner1 = new Vector3(0.01f, 0.01f, 0f)
                }
            ],
            LeafSize = 0.75f,
            LeafTable = new SptLeafTable { Float3002 = blossomProbability, Float3007 = 0.5f, UInt3008 = 0 }
        };
    }

    private static SptBranch MakeBranch(float length, float radius, float frequency, uint u6008, uint u6009,
        float pathExponent = 1f, float taperEnd = 1f)
    {
        return new SptBranch
        {
            Splines = BuildSlots(length, radius, taperEnd),
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

    private static int CountLeafQuads(NifRenderableModel model, string texturePart)
    {
        return model.Submeshes
            .Where(s => s.ShapeName == "spt:leaves" &&
                        s.DiffuseTexturePath?.Contains(texturePart, StringComparison.OrdinalIgnoreCase) == true)
            .Sum(s => s.Positions.Length / 12);
    }

    private static int CountAllLeafQuads(NifRenderableModel model)
    {
        return model.Submeshes
            .Where(s => s.ShapeName == "spt:leaves")
            .Sum(s => s.Positions.Length / 12);
    }

    private static Vector3 ReadVector3(float[] values, int vertex)
    {
        var i = vertex * 3;
        return new Vector3(values[i], values[i + 1], values[i + 2]);
    }

    /// <summary>Asserts a leaf-card bitangent's x/y (the card corner offset); z carries the wind weight,
    /// checked separately.</summary>
    private static void AssertOffsetXy(float[] values, int vertex, float x, float y)
    {
        var i = vertex * 3;
        Assert.Equal(x, values[i], 4);
        Assert.Equal(y, values[i + 1], 4);
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

    private static SptBezierSpline?[] BuildSlots(float length, float radius, float taperEnd = 1f)
    {
        var slots = new SptBezierSpline?[9];
        slots[4] = new SptBezierSpline { Header = new Vector3(length, length, 0f) };
        slots[5] = new SptBezierSpline { Header = new Vector3(radius, radius, 0f) };
        slots[6] = new SptBezierSpline { Header = new Vector3(1f, taperEnd, 0f) };
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
