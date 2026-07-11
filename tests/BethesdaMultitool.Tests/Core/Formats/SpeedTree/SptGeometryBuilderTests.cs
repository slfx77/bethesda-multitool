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

    [Fact]
    public void Build_WindWeights_RampWithBranchLevel()
    {
        // Design doc B.3 (windLevel = 1): trunk-spawned geometry is rigid (weight 0); leaves on
        // level >= 1 branches carry weight = 1 − raw > 0, ramping toward 1 at branch tips. The weight
        // rides in the packed aBitangent.z fraction.
        var model = new SptModel
        {
            General = new SptGeneralParams { BarkTexturePath = @"C:\x\OakBark.tga", Float2006 = 100f },
            Branches =
            [
                MakeBranch(1f, 0.02f, 2.6f, 3, 1),
                MakeLeafyBranch(),
                new SptBranch()
            ],
            Leaves = [new SptLeaf { Material = @"C:\x\OakFoliage.dds", Corner1 = new Vector3(0.01f) }],
            LeafTable = new SptLeafTable { Float3007 = 0.5f, UInt3008 = 0 }
        };

        var result = SptGeometryBuilder.Build(model, 1, BillboardOptions());
        var offsets = result.Submeshes.Single(s => s.ShapeName == "spt:leaves").Bitangents!;
        var sawPositive = false;
        for (var v = 2; v < offsets.Length; v += 3)
        {
            var weight = offsets[v] - MathF.Floor(offsets[v]);
            Assert.InRange(weight, 0f, 0.996f);
            sawPositive |= weight > 0f;
        }

        Assert.True(sawPositive, "level-1 leaves must carry a nonzero wind-matrix weight");
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
        // for our LB,RB,RT,LT emission order → corner slots 2,3,0,1), FRACTION = the wind-matrix lerp
        // weight. Card size = cardScale·Corner1 with cardScale = treeSizeMid = Float2006·10.
        // Vertex order follows the ENGINE's zip (BSTreeModel::CreateLeafGeometry pairs texcoord j with
        // CLeafGeometry::Update corner slot (j+2)&3). Seed 1 picks the ODD doubled-entry here
        // (authored pivot 0.25 → x0=−50, x1=150); the EVEN variant would flip the pivot.
        AssertOffsetXy(offsets, 0, -50f, -300f);
        AssertOffsetXy(offsets, 1, 150f, -300f);
        AssertOffsetXy(offsets, 2, 150f, 100f);
        AssertOffsetXy(offsets, 3, -50f, 100f);
        // Packed integer = 48·windMatrixIndex + slotBase·4 + cornerSlot; fraction = the wind-matrix
        // lerp weight (design doc B.3). This single-level model spawns its leaves from the TRUNK
        // (level 0 = below the wind level), so the authentic weight is exactly 0 — rigid under the
        // sway matrices, like the engine.
        var packed = (int)MathF.Floor(offsets[2]);
        var windIdx = packed / 48;
        Assert.InRange(windIdx, 0, 3);
        var slotBase = packed % 48 / 4 * 4;
        Assert.InRange(slotBase, 0, 44);
        var windWeight = offsets[2] - packed;
        Assert.Equal(0f, windWeight);
        // Corner slots walk (j+2)&3 = 2,3,0,1 off the shared base.
        var cardBase = packed - 2;
        Assert.Equal(cardBase + 3 + windWeight, offsets[5], 3);
        Assert.Equal(cardBase + 0 + windWeight, offsets[8], 3);
        Assert.Equal(cardBase + 1 + windWeight, offsets[11], 3);
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
