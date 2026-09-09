using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Procedural;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Scene;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Procedural;

public sealed class BendableSplineGeometryTests
{
    [Fact]
    public void TryBuild_StraightRetailDefaults_EmitDistanceSpacedSeamedTube()
    {
        var mesh = BendableSplineGeometry.TryBuild(
            0x00123456,
            Definition(4, 2f),
            Placement(new Vector3(50f, 0f, 0f), 10f),
            null);

        Assert.NotNull(mesh);
        Assert.Equal(25, mesh.SegmentCount);
        Assert.Equal(4, mesh.SliceCount);
        Assert.Equal((25 + 1) * (4 + 1), mesh.Vertices.Length);
        Assert.Equal(25 * 4 * 6, mesh.Indices.Length);
        Assert.Equal("fallout:generated/bnds/00123456", mesh.CacheKey);

        var ringSize = mesh.SliceCount + 1;
        AssertRingAt(mesh.Vertices.AsSpan(0, ringSize), new Vector3(-50f, 0f, 0f), 5f);
        AssertRingAt(mesh.Vertices.AsSpan(mesh.Vertices.Length - ringSize), new Vector3(50f, 0f, 0f), 5f);

        // Retail duplicates theta zero for the UV seam and emits this exact first quad winding.
        Assert.Equal(mesh.Vertices[0].Position, mesh.Vertices[ringSize - 1].Position);
        Assert.Equal(0f, mesh.Vertices[0].TexCoord.Y, 5);
        Assert.Equal(1f, mesh.Vertices[ringSize - 1].TexCoord.Y, 5);
        Assert.Equal(new ushort[] { 5, 0, 6, 1, 6, 0 }, mesh.Indices[..6]);
        Assert.Equal(2f, mesh.Vertices[^1].TexCoord.X, 5);
    }

    [Fact]
    public void ComputeControlPoint_HorizontalSpan_UsesRecoveredSagEquation()
    {
        var start = new Vector3(-50f, 0f, 10f);
        var end = new Vector3(50f, 0f, 10f);

        var control = BendableSplineGeometry.ComputeControlPoint(start, end, 0.25f);
        var curveCenter = BendableSplineGeometry.EvaluateQuadratic(start, control, end, 0.5f);

        Assert.Equal(0f, control.X, 5);
        Assert.Equal(0f, control.Y, 5);
        Assert.Equal(10f - 100f * BendableSplineGeometry.SagPercentage * 0.25f, control.Z, 4);
        // The recovered midpoint is the quadratic CONTROL point, so t=.5 receives half its sag.
        Assert.Equal(-7.5f, curveCenter.Z, 4);
    }

    [Fact]
    public void ComputeControlPoint_StrictlyVerticalSpan_DoesNotSag()
    {
        var control = BendableSplineGeometry.ComputeControlPoint(
            new Vector3(0f, 0f, -40f),
            new Vector3(0f, 0f, 40f),
            1f);

        Assert.Equal(Vector3.Zero, control);
    }

    [Fact]
    public void TryBuild_TilesRelativeToLength_ScalesRecoveredTextureU()
    {
        var mesh = BendableSplineGeometry.TryBuild(
            7,
            Definition(6, 0.05f, true),
            Placement(new Vector3(50f, 0f, 0f), 4f),
            null);

        Assert.NotNull(mesh);
        Assert.Equal(5f, mesh.TextureTileCount, 3);
        Assert.Equal(mesh.TextureTileCount, mesh.Vertices[^1].TexCoord.X, 5);
    }

    [Fact]
    public void TryBuild_MapsTnamTexturesAndOpaqueDnamVertexColor()
    {
        var textureSet = new TextureSetRecord
        {
            DiffuseTexture = @"textures\utility\wire_d.dds",
            NormalTexture = @"textures\utility\wire_n.dds"
        };
        var definition = Definition(4, 1f) with
        {
            DefaultColor = new Vector3(0.25f, 0.5f, 0.75f)
        };

        var mesh = BendableSplineGeometry.TryBuild(
            9,
            definition,
            Placement(new Vector3(20f, 0f, 0f), 2f),
            textureSet);

        Assert.NotNull(mesh);
        Assert.Equal(textureSet.DiffuseTexture, mesh.DiffuseTexturePath);
        Assert.Equal(textureSet.NormalTexture, mesh.NormalMapTexturePath);
        var color = GpuMeshUploader.UnpackColor(mesh.Vertices[0].VertexColorRgba);
        Assert.Equal(0.25f, color.X, 2);
        Assert.Equal(0.5f, color.Y, 2);
        Assert.Equal(0.75f, color.Z, 2);
        Assert.Equal(1f, color.W, 5);
        Assert.Null(mesh.SolidDiffuseColor);
    }

    [Fact]
    public void TryBuild_NoTnam_UsesRecoveredOpaqueMidGraySplineMap()
    {
        var mesh = BendableSplineGeometry.TryBuild(
            10,
            Definition(3, 1f),
            Placement(new Vector3(20f, 0f, 0f), 2f),
            null);

        Assert.NotNull(mesh);
        Assert.Null(mesh.DiffuseTexturePath);
        Assert.Null(mesh.NormalMapTexturePath);
        Assert.Equal(BendableSplineGeometry.DefaultSplineMapColor, mesh.SolidDiffuseColor);
        Assert.Equal(128f / 255f, mesh.SolidDiffuseColor!.Value.X, 6);
    }

    [Fact]
    public void TryBuild_WindWithTnam_UsesWhiteRgbAndRecoveredAlphaPayload()
    {
        var definition = Definition(4, 1f) with
        {
            DefaultColor = new Vector3(0.25f, 0.5f, 0.75f),
            WindSensibility = 2f,
            WindFlexibility = 0.5f
        };
        var textureSet = new TextureSetRecord
        {
            DiffuseTexture = @"textures\utility\wire_d.dds",
            NormalTexture = @"textures\utility\wire_n.dds"
        };

        var mesh = BendableSplineGeometry.TryBuild(
            11,
            definition,
            Placement(new Vector3(50f, 0f, 0f), 10f, 0.25f),
            textureSet);

        Assert.NotNull(mesh);
        var colors = mesh.Vertices
            .Select(vertex => GpuMeshUploader.UnpackColor(vertex.VertexColorRgba))
            .ToArray();
        Assert.All(colors, color => Assert.Equal(Vector3.One, new Vector3(color.X, color.Y, color.Z)));
        Assert.Equal(0f, colors[0].W, 5);
        Assert.Equal(0f, colors[^1].W, 5);
        // Retail normalizes the bend-distance lane, then scales by slack*wind/thickness = 0.05.
        // R8_UNORM packing rounds that peak to 13/255.
        Assert.Equal(13f / 255f, colors.Max(color => color.W), 6);
        Assert.True(mesh.UsesWindShader);
        Assert.Equal(0.5f, mesh.WindFlexibility);
        Assert.Equal(13f / 255f, mesh.MaximumPackedWindWeight, 6);
        var staticMesh = BendableSplineGeometry.TryBuild(
            13,
            definition with { WindSensibility = 0f },
            Placement(new Vector3(50f, 0f, 0f), 10f, 0.25f),
            textureSet);
        Assert.NotNull(staticMesh);
        Assert.Equal(
            staticMesh.LocalBoundsRadius +
            Fo4BendableSplineWind.MaximumDefaultDisplacement * mesh.MaximumPackedWindWeight,
            mesh.LocalBoundsRadius,
            4);
        // The retail loop projects every tube-surface vertex. A sagged ring is not perpendicular
        // to the endpoint chord, so its wind weights are intentionally not constant around V.
        var firstRingAlphas = colors
            .Take(mesh.SliceCount + 1)
            .Select(color => color.W)
            .Distinct()
            .ToArray();
        Assert.True(firstRingAlphas.Length > 1);
    }

    [Fact]
    public void TryBuild_WindWithoutTnam_RetainsDnamRgb()
    {
        var definition = Definition(4, 1f) with
        {
            DefaultColor = new Vector3(0.25f, 0.5f, 0.75f),
            WindSensibility = 2f
        };

        var mesh = BendableSplineGeometry.TryBuild(
            12,
            definition,
            Placement(new Vector3(50f, 0f, 0f), 10f, 0.25f),
            null);

        Assert.NotNull(mesh);
        var color = GpuMeshUploader.UnpackColor(mesh.Vertices[mesh.Vertices.Length / 2].VertexColorRgba);
        Assert.Equal(0.25f, color.X, 2);
        Assert.Equal(0.5f, color.Y, 2);
        Assert.Equal(0.75f, color.Z, 2);
        Assert.Equal(BendableSplineGeometry.DefaultSplineMapColor, mesh.SolidDiffuseColor);
        Assert.True(mesh.UsesWindShader);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void TryBuild_DegenerateSliceCount_ReturnsNull(ushort slices)
    {
        var mesh = BendableSplineGeometry.TryBuild(
            1,
            Definition(slices, 1f),
            Placement(new Vector3(10f, 0f, 0f), 2f),
            null);

        Assert.Null(mesh);
    }

    [Fact]
    public void TryBuild_NonWindSplineRetainsStaticBoundsAndNoWindMetadata()
    {
        var mesh = BendableSplineGeometry.TryBuild(
            14,
            Definition(4, 1f) with { WindFlexibility = 123f },
            Placement(new Vector3(50f, 0f, 0f), 10f, 0.25f),
            null);

        Assert.NotNull(mesh);
        Assert.False(mesh.UsesWindShader);
        Assert.Equal(0f, mesh.WindFlexibility);
        Assert.Equal(0f, mesh.MaximumPackedWindWeight);
        Assert.All(mesh.Vertices, static vertex => Assert.Equal(1f, vertex.VertexColor.W));
    }

    [Fact]
    public void TryBuildBendableSpline_ModelLessPlacement_ProducesOrdinaryBatchIdentityAndBounds()
    {
        var definition = new BendableSplineRecord
        {
            FormId = 0x100,
            Data = Definition(4, 1f)
        };
        var placement = new PlacedReference
        {
            FormId = 0x200,
            BaseFormId = definition.FormId,
            RecordType = "REFR",
            ModelPath = null,
            X = 100f,
            Y = 200f,
            Z = 300f,
            Scale = 2f,
            BendableSpline = Placement(new Vector3(30f, 0f, 0f), 4f)
        };

        var renderable = RenderableReference.TryBuildBendableSpline(
            placement,
            definition,
            null,
            PlacedObjectCategory.Unknown);

        Assert.NotNull(renderable);
        Assert.NotNull(renderable.Value.BendableSplineMesh);
        Assert.Equal("fallout:generated/bnds/00000200", renderable.Value.ModelPath);
        Assert.Equal(RenderableReference.ComputeMeshId(renderable.Value.ModelPath), renderable.Value.MeshId);
        Assert.Equal(new Vector3(100f, 200f, 300f), renderable.Value.BoundsCenter);
        Assert.True(renderable.Value.BoundsRadius > 60f);
    }

    private static BendableSplineDefinitionData Definition(
        ushort slices,
        float tiles,
        bool relative = false)
    {
        return new BendableSplineDefinitionData
        {
            DefaultTileCount = tiles,
            DefaultSliceCount = slices,
            TilesRelativeToLengthRaw = relative ? (ushort)1 : (ushort)0,
            DefaultColor = Vector3.One
        };
    }

    private static BendableSplinePlacementData Placement(
        Vector3 halfExtents,
        float thickness,
        float slack = 0f)
    {
        return new BendableSplinePlacementData
        {
            HalfExtents = halfExtents,
            Thickness = thickness,
            Slack = slack
        };
    }

    private static void AssertRingAt(
        ReadOnlySpan<GpuMeshUploader.GpuVertex> ring,
        Vector3 center,
        float radius)
    {
        foreach (var vertex in ring)
        {
            var radial = vertex.Position - center;
            Assert.Equal(radius, radial.Length(), 4);
            Assert.Equal(0f, Vector3.Dot(radial, Vector3.UnitX), 4);
        }
    }
}