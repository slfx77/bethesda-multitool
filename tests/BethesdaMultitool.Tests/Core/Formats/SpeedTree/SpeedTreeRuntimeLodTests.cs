using System.Numerics;
using BethesdaMultitool.Core.Formats.SpeedTree;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.SpeedTree;

public sealed class SpeedTreeRuntimeLodTests
{
    [Theory]
    [InlineData(0f, 0)]
    [InlineData(100f, 0)]
    [InlineData(299.999f, 2)]
    [InlineData(300f, 3)]
    [InlineData(float.PositiveInfinity, 3)]
    public void Selector_MapsEveryDistanceToOneBranchLevelOrBillboard(float distance, int expected)
    {
        Assert.Equal(expected, SpeedTreeRuntimeLod.SelectLevel(distance, 3, 100f, 300f));
    }

    [Fact]
    public void Selector_DoesNotCreateBoundaryGapsOrOverlaps()
    {
        var selected = new int[5];
        for (var i = 0; i <= 10_000; i++)
        {
            var distance = i * 0.1f;
            var level = SpeedTreeRuntimeLod.SelectLevel(distance, 4, 100f, 900f);
            Assert.InRange(level, 0, 4);
            selected[level]++;
        }

        Assert.All(selected, count => Assert.True(count > 0));
    }

    [Fact]
    public void PlacementSelector_ScalesTransitionDistancesWithReferenceScale()
    {
        var metadata = new SpeedTreeLodMetadata(
            Level: 0,
            BranchLevelCount: 2,
            NearDistance: 100f,
            FarDistance: 300f,
            Component: SpeedTreeLodComponent.Branch);

        Assert.Equal(1, SpeedTreeRuntimeLod.SelectLevelForPlacement(250f, 1f, metadata));
        Assert.Equal(0, SpeedTreeRuntimeLod.SelectLevelForPlacement(250f, 2f, metadata));
        Assert.Equal(2, SpeedTreeRuntimeLod.SelectLevelForPlacement(600f, 2f, metadata));
    }

    [Fact]
    public void BranchFractions_UseRecoveredNearToFarInterpolation()
    {
        var lod = new SptLodInfo
        {
            NumBranchLods = 6,
            BranchNearFraction = 1f,
            BranchFarFraction = 0.45f,
        };

        Assert.Equal(1f, SpeedTreeRuntimeLod.BranchKeepFraction(lod, 0));
        Assert.Equal(0.89f, SpeedTreeRuntimeLod.BranchKeepFraction(lod, 1), 6);
        Assert.Equal(0.45f, SpeedTreeRuntimeLod.BranchKeepFraction(lod, 5), 6);
    }

    [Fact]
    public void ApproximateLeafLevels_AreDeterministicNestedSubsets()
    {
        var lod0 = Enumerable.Range(0, 64)
            .Where(i => SpeedTreeRuntimeLod.KeepApproximateLeaf(i, 0))
            .ToArray();
        var lod1 = Enumerable.Range(0, 64)
            .Where(i => SpeedTreeRuntimeLod.KeepApproximateLeaf(i, 1))
            .ToArray();
        var lod2 = Enumerable.Range(0, 64)
            .Where(i => SpeedTreeRuntimeLod.KeepApproximateLeaf(i, 2))
            .ToArray();

        Assert.Equal(64, lod0.Length);
        Assert.Equal(32, lod1.Length);
        Assert.Equal(16, lod2.Length);
        Assert.All(lod2, i => Assert.Contains(i, lod1));
        Assert.Equal(lod2, Enumerable.Range(0, 64)
            .Where(i => SpeedTreeRuntimeLod.KeepApproximateLeaf(i, 2)));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(3, 1)]
    [InlineData(4, 2)]
    [InlineData(5, 3)]
    public void LeafLevelMapping_EndsOnAuthoredLastLevel(int branchLevel, int expectedLeafLevel)
    {
        Assert.Equal(expectedLeafLevel, SpeedTreeRuntimeLod.MapLeafLevel(branchLevel, 6, 4));
    }

    [Theory]
    [InlineData(@"\WastelandShrub01.spt", @"textures\trees\billboards\wastelandshrub01.dds")]
    [InlineData(@"trees/OasisElm01.SPT", @"textures\trees\billboards\oasiselm01.dds")]
    public void BillboardPath_UsesConventionalTreeStem(string modelPath, string expected)
    {
        Assert.Equal(expected, SpeedTreeRuntimeLod.BillboardTexturePath(modelPath));
    }

    [Fact]
    public void Builder_DefaultStaysSingleLodWhileOptInEmitsAuthoredLevelsAndBillboard()
    {
        var model = MakeModel();
        var established = SptGeometryBuilder.Build(model, 7, new SptGeometryOptions { LeafBillboard = true });
        var runtime = SptGeometryBuilder.Build(model, 7, new SptGeometryOptions
        {
            LeafBillboard = true,
            RuntimeLod = true,
            BillboardTexturePath = @"textures\trees\billboards\testtree.dds",
        });

        Assert.DoesNotContain(established.Submeshes, sub => sub.SpeedTreeLod is not null);
        Assert.DoesNotContain(established.Submeshes, sub => sub.ShapeName == "spt:billboard");

        var metadata = runtime.Submeshes.Select(sub => sub.SpeedTreeLod).ToArray();
        Assert.All(metadata, value => Assert.True(value is { IsValid: true }));
        Assert.Equal([0, 1, 2], runtime.Submeshes
            .Where(sub => sub.SpeedTreeLod?.Component == SpeedTreeLodComponent.Branch)
            .Select(sub => sub.SpeedTreeLod!.Value.Level)
            .Order()
            .ToArray());

        var billboard = Assert.Single(runtime.Submeshes,
            sub => sub.SpeedTreeLod?.Component == SpeedTreeLodComponent.Billboard);
        Assert.Equal(3, billboard.SpeedTreeLod!.Value.Level);
        Assert.Equal(@"textures\trees\billboards\testtree.dds", billboard.DiffuseTexturePath);
        Assert.True(billboard.IsLeafBillboard);
        Assert.Equal(4, billboard.VertexCount);
        Assert.Equal(2, billboard.TriangleCount);
    }

    [Fact]
    public void Builder_InstalledFnvFixtures_EmitEveryAuthoredBranchLevel()
    {
        BucketBTestGuard.SkipUnlessEnabled();

        var first = SampleFileFixture.FindSamplePath(
            @"TestOutput\fnv_spt\trees\wastelandshrub01.spt");
        Assert.SkipWhen(first is null, "Missing local TestOutput/fnv_spt fixture set.");

        var directory = Path.GetDirectoryName(first!)!;
        var paths = Directory.GetFiles(directory, "*.spt");
        Assert.NotEmpty(paths);
        foreach (var path in paths)
        {
            var model = SptFile.Parse(File.ReadAllBytes(path));
            Assert.NotNull(model.Lod);
            var runtime = SptGeometryBuilder.Build(model, model.General.Token2005, new SptGeometryOptions
            {
                LeafBillboard = true,
                RuntimeLod = true,
                BillboardTexturePath = SpeedTreeRuntimeLod.BillboardTexturePath(path),
            });

            var branchLevels = runtime.Submeshes
                .Where(sub => sub.SpeedTreeLod?.Component == SpeedTreeLodComponent.Branch)
                .Select(sub => sub.SpeedTreeLod!.Value.Level)
                .Distinct()
                .Order()
                .ToArray();
            Assert.Equal(Enumerable.Range(0, model.Lod!.NumBranchLods), branchLevels);
            Assert.Single(runtime.Submeshes,
                sub => sub.SpeedTreeLod?.Component == SpeedTreeLodComponent.Billboard);
            Assert.All(runtime.Submeshes, sub => Assert.True(sub.SpeedTreeLod is { IsValid: true }));
        }
    }

    private static SptModel MakeModel()
    {
        var slots = new SptBezierSpline?[9];
        slots[4] = new SptBezierSpline { Header = new Vector3(1f, 1f, 0f) };
        slots[5] = new SptBezierSpline { Header = new Vector3(0.04f, 0.04f, 0f) };
        slots[6] = new SptBezierSpline { Header = new Vector3(1f, 0.2f, 0f) };
        return new SptModel
        {
            General = new SptGeneralParams
            {
                BarkTexturePath = @"C:\x\TestTreeBark.tga",
                Float2006 = 10f,
                Token2005 = 7,
            },
            Branches =
            [
                new SptBranch
                {
                    Splines = slots,
                    UInt6008 = 5,
                    UInt6009 = 5,
                    Float6010 = 0f,
                    Float6011 = 1f,
                    Float6012 = 40f,
                    Float6014 = 1f,
                }
            ],
            Leaves =
            [
                new SptLeaf
                {
                    Material = @"C:\x\TestTreeFoliage.dds",
                    Position = Vector3.One,
                    Corner0 = new Vector3(0.5f, 0.25f, 0f),
                    Corner1 = new Vector3(0.1f, 0.1f, 0f),
                    Corner2 = new Vector3(1f, 1f, 0f),
                }
            ],
            Lod = new SptLodInfo
            {
                NumBranchLods = 3,
                BranchNearFraction = 1f,
                BranchFarFraction = 0.25f,
                NumLeafLods = 2,
                LeafLodSizeIncrease = 0.2f,
            },
        };
    }
}
