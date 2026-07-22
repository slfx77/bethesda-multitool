using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.SpeedTree;
using BethesdaMultitool.Tests.Helpers;
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
        Assert.True(bark.IsSpeedTreeBranch);
        Assert.NotNull(bark.Tangents);
        Assert.NotNull(bark.Bitangents);
        Assert.EndsWith("_n.dds", bark.NormalMapTexturePath, StringComparison.OrdinalIgnoreCase);

        var leaf = result.Submeshes.Single(s => s.ShapeName == "spt:leaves");
        Assert.Contains("leaves", leaf.DiffuseTexturePath!, StringComparison.OrdinalIgnoreCase);
        Assert.True(leaf.IsDoubleSided);
        Assert.True(leaf.HasAlphaTest);
        Assert.Equal((byte)84, leaf.AlphaTestThreshold);

        foreach (var sub in result.Submeshes)
        {
            AssertValidMesh(sub);
        }

        // Bounds are finite and non-degenerate.
        Assert.True(float.IsFinite(result.Width) && result.Width > 0);
        Assert.True(float.IsFinite(result.Height) && result.Height > 0);
    }

    [Theory]
    [InlineData(83, false)]
    [InlineData(84, false)]
    [InlineData(95, true)]
    [InlineData(96, true)]
    public void LegacyLeafAlphaRamp_UsesRecoveredGreaterThan84Cutout(int alphaByte, bool survives)
    {
        var threshold = SptGeometryOptions.Default.LeafAlphaThreshold;
        Assert.Equal((byte)84, threshold);
        Assert.Equal(survives, alphaByte > threshold);
    }

    [Fact]
    public void Build_KeepsNaturalEngineScale_IgnoresTargetHeight()
    {
        var model = new SptModel
        {
            General = new SptGeneralParams { BarkTexturePath = @"C:\x\OakBark.tga" },
            Branches = [MakeLeafyBranch()],
            Leaves = [new SptLeaf { Material = @"C:\x\OakFoliage.dds", Corner2 = new Vector3(6, 6, 0) }]
        };

        // The engine renders the loft at its natural world scale — there is NO rescale to the TREE
        // OBND/BNAM height (verified against 15 runtime SptMeshDumper dumps: e.g. WastelandShrub01
        // dumps 64.8 units tall while its OBND says 175). TargetHeight is a retained-but-ignored
        // option until the last in-flight caller drops it.
        var natural = SptGeometryBuilder.Build(model, 7, new SptGeometryOptions());
        var withTarget = SptGeometryBuilder.Build(model, 7, new SptGeometryOptions { TargetHeight = 175f });

        // The tree grows along +Z (FNV world-up); NifRenderableModel labels the Z-extent "Depth".
        Assert.Equal(natural.Depth, withTarget.Depth, 3);
        Assert.True(MathF.Abs(withTarget.Depth - 175f) > 5f,
            $"Depth {withTarget.Depth} should be the natural loft height, not the OBND value.");
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
        BucketBTestGuard.SkipUnlessEnabled();
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
    public void Build_FrondGate_GatedLevelsLoftNoBark_LeavesPersist()
    {
        // CIdvBranch::Compute (all three binaries): with the 13000 section enabled, branches at
        // level >= frondLevel still GENERATE (same RNG; their leaves persist in the pools) but are
        // destroyed instead of linked — no bark tube, no LOD ranking. Bethesda never renders fronds.
        SptModel MakeModel(SptFrond? frond) => new()
        {
            General = new SptGeneralParams { BarkTexturePath = @"C:\x\OakBark.tga", Float2006 = 100f },
            Branches =
            [
                MakeBranch(1f, 0.02f, 2.6f, 3, 1),
                MakeBranch(0.5f, 0.01f, 2.6f, 3, 1) with { Float6011 = 1f },
                MakeLeafyBranch(),
                new SptBranch()
            ],
            Leaves = [new SptLeaf { Material = @"C:\x\OakFoliage.dds", Corner1 = new Vector3(0.01f) }],
            LeafTable = new SptLeafTable { Float3007 = 0.5f, UInt3008 = 0 },
            Frond = frond
        };

        var ungated = SptGeometryBuilder.Build(MakeModel(null), 1, BillboardOptions());
        var gated = SptGeometryBuilder.Build(MakeModel(new SptFrond { Enabled = true, Level = 1 }), 1, BillboardOptions());
        var disabled = SptGeometryBuilder.Build(MakeModel(new SptFrond { Enabled = false, Level = 1 }), 1, BillboardOptions());

        var ungatedBark = ungated.Submeshes.Single(s => s.ShapeName == "spt:bark").Positions.Length;
        var gatedBark = gated.Submeshes.Single(s => s.ShapeName == "spt:bark").Positions.Length;
        Assert.Equal(RingVertexCount(3) * 2 * 3, gatedBark);            // trunk tube only (2 rings × 3+1 verts × xyz)
        Assert.True(gatedBark < ungatedBark, "frond gate removed no bark");
        Assert.Equal(CountLeafQuads(ungated), CountLeafQuads(gated));   // accepted leaves persist
        Assert.Equal(ungatedBark,
            disabled.Submeshes.Single(s => s.ShapeName == "spt:bark").Positions.Length); // disabled = no gate
    }

    [Theory]
    [InlineData(0f, 0f, 0.002f)]     // zero slot-2 flex → rigid under the matrices (WastelandShrub01 case)
    [InlineData(0.3f, 0.05f, 0.31f)] // real flex → positive weight bounded by t·slot2·slot3 ≈ 0.3
    public void Build_WindWeights_ScaleWithSlot2Slot3Flex(float slot2Flex, float minMaxWeight, float maxWeightCap)
    {
        // Decompile-exact (ComputeVertexWeight 0x82975DF8): the wind-matrix weight at windLevel(=1) is
        // a·b = t · Eval(slot2, parentT) · Eval(slot3, t). Slot 2 (per-branch) and slot 3 (per-ring) are
        // the wind-flex response — authored ~0 on stiff plants, which keeps them rigid under the sway
        // matrices. Approximating a·b ≈ t (dropping both flex splines) over-swayed low-flex plants ~20×.
        var leafy = MakeLeafyBranch();
        var slots = (SptBezierSpline?[])leafy.Splines;
        slots[2] = new SptBezierSpline { Header = new Vector3(slot2Flex, slot2Flex, 0f) }; // constant flex
        slots[3] = new SptBezierSpline { Header = new Vector3(0f, 1f, 0f) };               // ramps 0→1 with t
        var model = new SptModel
        {
            General = new SptGeneralParams { BarkTexturePath = @"C:\x\OakBark.tga", Float2006 = 100f },
            Branches = [MakeBranch(1f, 0.02f, 2.6f, 3, 1), leafy, new SptBranch()],
            Leaves = [new SptLeaf { Material = @"C:\x\OakFoliage.dds", Corner1 = new Vector3(0.01f) }],
            LeafTable = new SptLeafTable { Float3007 = 0.5f, UInt3008 = 0 }
        };

        var result = SptGeometryBuilder.Build(model, 1, BillboardOptions());
        var leafSubmesh = result.Submeshes.Single(s => s.ShapeName == "spt:leaves");
        var normals = leafSubmesh.Normals!;
        var maxWeight = 0f;
        for (var vertex = 0; vertex < leafSubmesh.VertexCount; vertex++)
        {
            // Engine v3.x (wind-matrix weight) is independent from v3.z's dimmer fraction. The
            // renderer-preserving payload stores it as |normal|-1 and normalizes before lighting.
            var weight = ReadVector3(normals, vertex).Length() - 1f;
            Assert.InRange(weight, 0f, maxWeightCap); // never exceeds the t·flex product
            maxWeight = MathF.Max(maxWeight, weight);
        }

        Assert.True(maxWeight >= minMaxWeight,
            $"expected peak leaf wind weight ≥ {minMaxWeight}, got {maxWeight}");
    }

    [Fact]
    public void Build_BarkCarriesOrthonormalTbnAndRecoveredBranchWindWeights()
    {
        var child = MakeBranch(0.5f, 0.01f, 0f, 3, 4);
        var childSlots = (SptBezierSpline?[])child.Splines;
        childSlots[2] = new SptBezierSpline { Header = new Vector3(1f, 1f, 0f) };
        childSlots[3] = new SptBezierSpline { Header = new Vector3(0f, 1f, 0f) };
        var model = new SptModel
        {
            General = new SptGeneralParams { BarkTexturePath = @"C:\x\OakBark.tga", Float2006 = 100f },
            Branches =
            [
                MakeBranch(1f, 0.02f, 1f, 3, 2),
                child,
                new SptBranch()
            ],
            Leaves = [new SptLeaf { Material = @"C:\x\OakFoliage.dds", Corner1 = new Vector3(0.01f) }]
        };

        var bark = SptGeometryBuilder.Build(model, 17).Submeshes.Single(s => s.ShapeName == "spt:bark");

        Assert.True(bark.IsSpeedTreeBranch);
        Assert.NotNull(bark.NormalMapTexturePath);
        Assert.NotNull(bark.Tangents);
        Assert.NotNull(bark.Bitangents);

        var minWeight = float.MaxValue;
        var maxWeight = float.MinValue;
        for (var vertex = 0; vertex < bark.VertexCount; vertex++)
        {
            var n = Vector3.Normalize(ReadVector3(bark.Normals!, vertex));
            var encodedT = ReadVector3(bark.Tangents!, vertex);
            var encodedB = ReadVector3(bark.Bitangents!, vertex);
            var tangentLength = encodedT.Length();
            var bitangentLength = encodedB.Length();
            var t = encodedT / tangentLength;
            var b = encodedB / bitangentLength;

            Assert.InRange(tangentLength, 1f - 1e-4f, 4f + 1e-4f);
            Assert.InRange(bitangentLength, 1f - 1e-4f, 2f + 1e-4f);
            Assert.InRange(MathF.Abs(Vector3.Dot(n, t)), 0f, 1e-4f);
            Assert.InRange(MathF.Abs(Vector3.Dot(n, b)), 0f, 1e-4f);
            Assert.InRange(MathF.Abs(Vector3.Dot(t, b)), 0f, 1e-4f);
            // U advances around the ring while V advances toward the branch tip. That authored UV
            // orientation is left-handed relative to the outward normal; preserve the explicit
            // bitangent instead of silently flipping the normal map's green axis.
            Assert.InRange(Vector3.Dot(Vector3.Cross(t, b), n), -1.0001f, -0.9999f);

            var weight = bitangentLength - 1f;
            minWeight = MathF.Min(minWeight, weight);
            maxWeight = MathF.Max(maxWeight, weight);
        }

        // Level-0 trunk vertices remain rigid; flexible level-1 branch tips carry greater motion.
        Assert.InRange(minWeight, 0f, 1e-4f);
        Assert.True(maxWeight > 0.5f, $"expected a flexible branch tip, peak weight was {maxWeight}");
    }

    [Fact]
    public void Build_CnamWindSpeedsChangeOnlyPerTreePhaseMetadata()
    {
        var model = new SptModel
        {
            General = new SptGeneralParams { BarkTexturePath = @"C:\x\OakBark.tga" },
            Branches = [MakeLeafyBranch()],
            Leaves = [new SptLeaf { Material = @"C:\x\OakFoliage.dds", Corner2 = new Vector3(6, 6, 0) }]
        };

        var baseline = SptGeometryBuilder.Build(model, 99,
            new SptGeometryOptions { LeafBillboard = true, CrossedLeafCards = false });
        var doubled = SptGeometryBuilder.Build(model, 99,
            new SptGeometryOptions
            {
                LeafBillboard = true,
                CrossedLeafCards = false,
                RockSpeed = 2f,
                RustleSpeed = 3f
            });

        Assert.Equal(baseline.Submeshes.Count, doubled.Submeshes.Count);
        for (var i = 0; i < baseline.Submeshes.Count; i++)
        {
            Assert.Equal(baseline.Submeshes[i].Positions, doubled.Submeshes[i].Positions);
            Assert.Equal(baseline.Submeshes[i].Triangles, doubled.Submeshes[i].Triangles);
            Assert.Equal(new Vector2(2f, 3f), doubled.Submeshes[i].SpeedTreeWindSpeeds);
        }

        const float baseRockPhase = 0.375f;
        Assert.Equal(0.75f, baseRockPhase * doubled.Submeshes[0].SpeedTreeWindSpeeds.X, 6);
    }

    [Fact]
    public void RecoveredLeafWind_DeformsFullCornerRatherThanRigidCenter()
    {
        // Independent recovered-STLEAF vector: construct the corner first, rotate the complete
        // model-space point 90 degrees around X, then interpolate by the authored wind weight.
        // A center-only implementation leaves the one-unit card edge rigid and lands elsewhere.
        var center = new Vector3(0f, 0f, 2f);
        var offset = new Vector3(0f, 1f, 0f);
        var corner = center + offset;
        static Vector3 RotateX90(Vector3 p) => new(p.X, -p.Z, p.Y);

        var recovered = Vector3.Lerp(corner, RotateX90(corner), 0.5f);
        var centerOnly = Vector3.Lerp(center, RotateX90(center), 0.5f) + offset;

        Assert.Equal(new Vector3(0f, -0.5f, 1.5f), recovered);
        Assert.Equal(new Vector3(0f, 0f, 1f), centerOnly);
        Assert.NotEqual(centerOnly, recovered);
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
        // bitangent.xy = the card corner offset (Corner0 pivot + Corner1 size). bitangent.z packs the
        // engine's STLEAF v3.z: INTEGER = the LeafBase phase slot (slotBase·4 + the (j+2)&3 corner slot
        // for our LB,RB,RT,LT emission order → corner slots 2,3,0,1), FRACTION = the recovered canopy
        // dimmer, which also jitters the wind phase. Card size = cardScale·Corner1 with
        // cardScale = treeSizeMid = Float2006·10.
        // Vertex order follows the ENGINE's zip (BSTreeModel::CreateLeafGeometry pairs texcoord j with
        // CLeafGeometry::Update corner slot (j+2)&3). Seed 1 picks the ODD doubled-entry here
        // (authored pivot 0.25 → x0=−50, x1=150); the EVEN variant would flip the pivot.
        AssertOffsetXy(offsets, 0, -50f, -300f);
        AssertOffsetXy(offsets, 1, 150f, -300f);
        AssertOffsetXy(offsets, 2, 150f, 100f);
        AssertOffsetXy(offsets, 3, -50f, 100f);
        // Packed integer = 48·windMatrixIndex + slotBase·4 + cornerSlot. A fully-lit dimmer byte is
        // clamped to .99 so floor(v3.z) still selects the authored corner, exactly like the engine.
        var packed = (int)MathF.Floor(offsets[2]);
        var windIdx = packed / 48;
        Assert.InRange(windIdx, 0, 3);
        var slotBase = packed % 48 / 4 * 4;
        Assert.InRange(slotBase, 0, 44);
        var dimmerFraction = offsets[2] - packed;
        Assert.Equal(0.99f, dimmerFraction, 3);
        // This fixture's leaf sits below the wind level, so independent v3.x is exactly zero.
        var encodedNormal = ReadVector3(
            result.Submeshes.Single(s => s.ShapeName == "spt:leaves").Normals!, 0);
        Assert.Equal(0f, encodedNormal.Length() - 1f, 4);
        // Corner slots walk (j+2)&3 = 2,3,0,1 off the shared base.
        var cardBase = packed - 2;
        Assert.Equal(cardBase + 3 + dimmerFraction, offsets[5], 3);
        Assert.Equal(cardBase + 0 + dimmerFraction, offsets[8], 3);
        Assert.Equal(cardBase + 1 + dimmerFraction, offsets[11], 3);
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
        // U-mirrors the pairs (u of pairs 0↔1 and 2↔3 swap, v's stay — SetTextureCoords' second entry).
        // This fixture has ONE leaf map, so BSTreeModel::CreateLeafGeometry's identical-material path
        // applies: vertex j takes pair j VERBATIM — buffer order LB, RB, RT, LT gets pairs 0..3.
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
    public void Build_LeafCardUvs_DistinctLeafMapsApplyCardVerticalSwap()
    {
        // Two leaf maps with DIFFERENT material names: BSTreeModel::CreateLeafGeometry's pairwise
        // strcmp (0x8249C0F0 L634-656) then routes every card through the (u_j, v_{3−j}) texcoord
        // path (L869-878) — for rectangular pair layouts, the vertical mirror of the verbatim zip.
        // Both maps share the same coords/corners so the pin holds whichever template the pick draws.
        var uv = new SptLeafTextureCoords(
            new Vector2(0.75f, 0.9f),
            new Vector2(0.25f, 0.9f),
            new Vector2(0.25f, 0.1f),
            new Vector2(0.75f, 0.1f));
        var model = MakeSingleLevelLeafModel(
            leafFrequency: 1f,
            leafTextureCoords: [uv, uv],
            corner0: new Vector3(0.5f, 0.5f, 0f),
            corner1: new Vector3(0.1f, 0.2f, 0f)) with
        {
            Leaves =
            [
                new SptLeaf
                {
                    Material = @"C:\x\OakFoliage.dds",
                    Corner0 = new Vector3(0.5f, 0.5f, 0f),
                    Corner1 = new Vector3(0.1f, 0.2f, 0f)
                },
                new SptLeaf
                {
                    Material = @"C:\x\OakBlossom.dds",
                    Corner0 = new Vector3(0.5f, 0.5f, 0f),
                    Corner1 = new Vector3(0.1f, 0.2f, 0f)
                }
            ]
        };

        var result = SptGeometryBuilder.Build(model, 1, BillboardOptions());

        var uvs = result.Submeshes.Single(s => s.ShapeName == "spt:leaves").UVs!;
        // Same flipped/odd-mirrored pairs as the single-map pin, but zipped (u_j, v_{3−j}):
        // LB=(u0,v3), RB=(u1,v2), RT=(u2,v1), LT=(u3,v0) — the vertical mirror.
        Assert.Equal(0.25f, uvs[0], 4);
        Assert.Equal(0.9f, uvs[1], 4);
        Assert.Equal(0.75f, uvs[2], 4);
        Assert.Equal(0.9f, uvs[3], 4);
        Assert.Equal(0.75f, uvs[4], 4);
        Assert.Equal(0.1f, uvs[5], 4);
        Assert.Equal(0.25f, uvs[6], 4);
        Assert.Equal(0.1f, uvs[7], 4);
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
    ///     <c>CIdvBranch::MakeLeaf</c>'s RoomForLeaf scope is MODE-dependent: mode 0 disables rejection
    ///     (all 4 candidates kept); mode 1 tests the list <c>CIdvBranch::Compute</c> CLEARS after every
    ///     leaf-spawning branch (FNV 360 L2476-2483; Oblivion FUN_007925b0 tail erase of the
    ///     &amp;DAT_00b429fc vector) → one card survives PER BRANCH (this fixture spawns leaves on
    ///     2 branches → 2); mode 2 tests the never-cleared caller-provided list → one card per TREE
    ///     (Oblivion FUN_007919d0: mode 1 → the global cleared list, mode 2 → param_8). (An earlier
    ///     reading pinned both modes global off a mode-2 WastelandShrub oracle — right for mode 2,
    ///     wrong for mode 1.)
    /// </summary>
    [Theory]
    [InlineData(0u, 4)]
    [InlineData(1u, 2)]
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
    public void Build_TrunkNoise_WalkStaysBounded()
    {
        // Japanesemaple-class trunk: many rings × large slot-0 angular noise, applied UNDAMPED like the
        // engine (the old trunk-only damping/restoring correctives were deleted once the ApplyGravity
        // reference vector was corrected to world-DOWN — the engine has no restoring term, and with the
        // correct bend sign the trace-exact noise keeps real trunks upright). The two-angle walk still
        // stays visually bounded across seeds.
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
    public void Build_TrunkNoise_CharacterSurvives()
    {
        // A noisy trunk must still end measurably off the axis — per-ring character is real geometry,
        // not smoothed away.
        var result = SptGeometryBuilder.Build(MakeVerticalNoisyTrunkModel(12f, rings: 25), 7, BillboardOptions());
        var bark = result.Submeshes.Single(s => s.ShapeName == "spt:bark");
        var (tipXy, height) = TrunkTipOffset(bark);
        Assert.True(tipXy > height * 0.005f,
            $"trunk tip pinned to the axis (offset {tipXy:0.##} over height {height:0.#})");
    }

    [Fact]
    public void Build_ApplyGravity_NegativeRollCurlsBranchUpward()
    {
        // CIdvBranch::Compute measures the bend angle FROM WORLD-DOWN and rotates about cross(dir, DOWN)
        // (Oblivion .data 0x00B2B724 = (0,0,-1); 360 agrees). With slot-8 evaluating above 0.5 the roll
        // contribution -(s8-0.5)*2 is negative and a horizontal limb must curl UP, never dive below its
        // spawn height (the pre-derivation port bent the opposite way - the sprawl defect).
        var branch = MakeBranch(1f, 0.02f, 0f, 4, 24);
        var slots = (SptBezierSpline?[])branch.Splines;
        slots[1] = new SptBezierSpline { Header = new Vector3(1f, 1f, 0f) };  // bend gain 1
        slots[7] = new SptBezierSpline { Header = new Vector3(0f, 0f, 0f) };  // horizontal spawn
        slots[8] = new SptBezierSpline { Header = new Vector3(1f, 1f, 0f) };  // s8 = 1 -> roll = -1
        var model = new SptModel
        {
            General = new SptGeneralParams { BarkTexturePath = @"C:\x\OakBark.tga", Float2006 = 100f },
            Branches = [branch],
            LeafTable = new SptLeafTable { Float3007 = 0.5f, UInt3008 = 0 }
        };

        var result = SptGeometryBuilder.Build(model, 7, BillboardOptions());
        var bark = result.Submeshes.Single(s => s.ShapeName == "spt:bark");
        var (tip, baseZ) = (Vector3.Zero, float.MaxValue);
        var maxRadial = 0f;
        foreach (var i in Enumerable.Range(0, bark.Positions.Length / 3))
        {
            var v = ReadVector3(bark.Positions, i);
            maxRadial = MathF.Max(maxRadial, MathF.Sqrt(v.X * v.X + v.Y * v.Y));
            if (v.Z > tip.Z) tip = v;
            baseZ = MathF.Min(baseZ, v.Z);
        }

        Assert.True(tip.Z > maxRadial * 0.5f,
            $"limb did not curl upward (tipZ {tip.Z:0.##} vs radial reach {maxRadial:0.##})");
        Assert.True(baseZ > -maxRadial * 0.25f,
            $"limb dove below its spawn plane (minZ {baseZ:0.##} vs radial reach {maxRadial:0.##})");
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
            // RoomForLeaf spacing = Corner1 (token 4005) × treeSizeMid × factor — CTreeEngine::Compute
            // overwrites the map's +0x48/+0x4C with sizeMid·Corner1 before generation (FNV 360 L3679 /
            // Oblivion FUN_007a45f0), which RoomForLeaf then reads. Here Corner1=50 × sizeMid(100·10) ×
            // factor(1) = 50 000: huge, so the candidate cluster rejects down to one card per scope.
            Leaves =
            [
                new SptLeaf
                {
                    Material = @"C:\x\OakFoliage.dds",
                    Corner1 = new Vector3(50f, 50f, 0f)
                }
            ],
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

    /// <summary>Asserts a leaf-card bitangent's x/y (the card corner offset); z carries the recovered
    /// integer phase slot plus authored dimmer fraction and is checked separately.</summary>
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
