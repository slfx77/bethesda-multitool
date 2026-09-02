using System.Numerics;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Atmosphere;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using BethesdaMultitool.Core.Games;
using SharpGLTF.Schema2;
using Xunit;
using NpcSceneBuilder = BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Assembly.NpcExportSceneBuilder;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

public sealed class AuthoredSkyGlbPreviewProjectionTests
{
    private const string DiffusePath = @"textures\sky\must-not-be-sampled.dds";

    [Fact]
    public void Projection_UsesWorldViewerWeightOrderAndOpaqueHorizonComposite()
    {
        var submesh = Triangle(
        [
            255, 0, 0, 255,
            0, 0, 255, 255,
            0, 0, 255, 128
        ]);
        var palette = AtmosphereState.Resolve(12f, game: BethesdaGame.Fallout76);

        Assert.True(AuthoredSkyGlbPreviewProjection.TryBuildVertexColor(submesh, 0, out var horizon));
        Assert.True(AuthoredSkyGlbPreviewProjection.TryBuildVertexColor(submesh, 1, out var upper));
        Assert.True(AuthoredSkyGlbPreviewProjection.TryBuildVertexColor(submesh, 2, out var coveredUpper));

        AssertColor(palette.AuthoredHorizonColor, horizon);
        AssertColor(palette.SkyTopColor, upper);
        AssertColor(
            Vector3.Lerp(palette.AuthoredHorizonColor, palette.SkyTopColor, 128f / 255f),
            coveredUpper);
        Assert.Equal(1f, horizon.W);
        Assert.Equal(1f, upper.W);
        Assert.Equal(1f, coveredUpper.W);
    }

    [Fact]
    public void Projection_FailsClosedUnlessTypedSkyHasOneExactRgbaPerVertex()
    {
        var clouds = Triangle(Enumerable.Repeat((byte)255, 12).ToArray());
        clouds.SkyType = SkyObjectType.Clouds;
        Assert.False(AuthoredSkyGlbPreviewProjection.AppliesTo(clouds));

        var incomplete = Triangle(Enumerable.Repeat((byte)255, 11).ToArray());
        Assert.False(AuthoredSkyGlbPreviewProjection.AppliesTo(incomplete));
        Assert.False(AuthoredSkyGlbPreviewProjection.TryBuildVertexColor(incomplete, 0, out _));

        var extra = Triangle(Enumerable.Repeat((byte)255, 13).ToArray());
        Assert.False(AuthoredSkyGlbPreviewProjection.AppliesTo(extra));

        var empty = new RenderableSubmesh
        {
            Positions = [],
            Triangles = [],
            VertexColors = [],
            SkyType = SkyObjectType.Sky
        };
        Assert.False(AuthoredSkyGlbPreviewProjection.AppliesTo(empty));

        var exact = Triangle(Enumerable.Repeat((byte)255, 12).ToArray());
        Assert.True(AuthoredSkyGlbPreviewProjection.AppliesTo(exact));
        Assert.False(AuthoredSkyGlbPreviewProjection.TryBuildVertexColor(exact, -1, out _));
        Assert.False(AuthoredSkyGlbPreviewProjection.TryBuildVertexColor(exact, 3, out _));
    }

    [Fact]
    public void ModernSceneBridge_ExportsLabeledOpaqueUnlitDoubleSidedFallbackWithoutMutatingRawWeights()
    {
        byte[] rawWeights =
        [
            255, 0, 0, 17,
            0, 0, 255, 255,
            0, 0, 255, 128
        ];
        var expectedRawWeights = (byte[])rawWeights.Clone();
        var submesh = Triangle(rawWeights);
        submesh.DiffuseTexturePath = DiffusePath;
        submesh.HasAlphaBlend = true;
        submesh.HasAlphaTest = true;
        submesh.MaterialAlpha = 0.2f;
        submesh.IsDoubleSided = false;

        var renderable = new NifRenderableModel();
        renderable.Submeshes.Add(submesh);
        var scene = Assert.IsType<GlbScene>(
            NifExportSceneBuilder.BuildRenderableModel(renderable, @"meshes\sky\Atmosphere.nif"));
        Assert.Same(submesh, Assert.Single(scene.MeshParts).Submesh);

        var textureRequests = 0;
        using var resolver = new NifTextureResolver(_ =>
        {
            textureRequests++;
            return DecodedTexture.FromBaseLevel([255, 0, 0, 255], 1, 1, false);
        });
        var model = Read(GlbWriter.WriteToBytes(scene, resolver));

        Assert.Equal(0, textureRequests);
        Assert.Same(rawWeights, submesh.VertexColors);
        Assert.True(expectedRawWeights.SequenceEqual(Assert.IsType<byte[]>(submesh.VertexColors)));

        var material = Assert.Single(model.LogicalMaterials);
        Assert.Contains(AuthoredSkyGlbPreviewProjection.NameSuffix, material.Name, StringComparison.Ordinal);
        Assert.Equal(AlphaMode.OPAQUE, material.Alpha);
        Assert.True(material.Unlit);
        Assert.True(material.DoubleSided);
        var baseColor = Assert.IsType<MaterialChannel>(material.FindChannel("BaseColor"));
        Assert.Null(baseColor.Texture);
        Assert.Equal(Vector4.One, baseColor.Color);

        var mesh = Assert.Single(model.LogicalMeshes);
        Assert.Contains(AuthoredSkyGlbPreviewProjection.NameSuffix, mesh.Name, StringComparison.Ordinal);
        var colors = mesh.Primitives.Single()
            .GetVertexAccessor("COLOR_0")
            .AsVector4Array()
            .ToArray();
        var palette = AtmosphereState.Resolve(12f, game: BethesdaGame.Fallout76);
        AssertColor(palette.AuthoredHorizonColor, colors[0]);
        AssertColor(palette.SkyTopColor, colors[1]);
        AssertColor(
            Vector3.Lerp(palette.AuthoredHorizonColor, palette.SkyTopColor, 128f / 255f),
            colors[2]);
        Assert.All(colors, static color => Assert.Equal(1f, color.W));
    }

    [Fact]
    public void OtherTypedSkyLayersKeepLiteralVertexColorMaterialPath()
    {
        var clouds = Triangle(
        [
            255, 0, 0, 255,
            0, 255, 0, 255,
            0, 0, 255, 255
        ]);
        clouds.SkyType = SkyObjectType.Clouds;
        var scene = new GlbScene();
        scene.MeshParts.Add(new GlbMeshPart
        {
            Name = "Clouds",
            Submesh = clouds
        });
        using var resolver = new NifTextureResolver();

        var model = Read(GlbWriter.WriteToBytes(scene, resolver));

        var material = Assert.Single(model.LogicalMaterials);
        Assert.DoesNotContain(AuthoredSkyGlbPreviewProjection.NameSuffix, material.Name, StringComparison.Ordinal);
        Assert.False(material.Unlit);
        var mesh = Assert.Single(model.LogicalMeshes);
        Assert.Equal("Clouds", mesh.Name);
        var colors = mesh.Primitives.Single()
            .GetVertexAccessor("COLOR_0")
            .AsVector4Array()
            .ToArray();
        Assert.Equal(new Vector4(1f, 0f, 0f, 1f), colors[0]);
        Assert.Equal(new Vector4(0f, 1f, 0f, 1f), colors[1]);
        Assert.Equal(new Vector4(0f, 0f, 1f, 1f), colors[2]);
    }

    [Fact]
    public void MaterialCache_DoesNotShareAuthoredSkyFallbackWithOtherwiseIdenticalGeometry()
    {
        byte[] colors =
        [
            255, 0, 0, 255,
            0, 255, 0, 255,
            0, 0, 255, 255
        ];
        var authoredSky = Triangle((byte[])colors.Clone());
        var ordinary = Triangle((byte[])colors.Clone());
        ordinary.SkyType = null;
        var scene = new GlbScene();
        scene.MeshParts.Add(new GlbMeshPart { Name = "Shared", Submesh = authoredSky });
        scene.MeshParts.Add(new GlbMeshPart { Name = "Shared", Submesh = ordinary });
        using var resolver = new NifTextureResolver();

        var materials = Read(GlbWriter.WriteToBytes(scene, resolver)).LogicalMaterials;

        Assert.Equal(2, materials.Count);
        Assert.Single(materials, static material => material.Unlit);
        Assert.Single(materials, static material => !material.Unlit);
        Assert.Single(
            materials,
            material => (material.Name ?? string.Empty).Contains(
                AuthoredSkyGlbPreviewProjection.NameSuffix,
                StringComparison.Ordinal));
    }

    [Fact]
    public void SceneCloneBridges_PreserveTypedSkyClassification()
    {
        var source = Triangle([255, 0, 0, 255, 0, 0, 255, 255, 0, 0, 255, 255]);
        source.SourceBlockIndex = 7;

        var npcClone = NpcSceneBuilder.CloneSubmesh(source);
        Assert.Equal(SkyObjectType.Sky, npcClone.SkyType);

        var hierarchyGeometry = Triangle((byte[])source.VertexColors!.Clone());
        hierarchyGeometry.SkyType = null;
        hierarchyGeometry.SourceBlockIndex = 7;
        var scene = new GlbScene();
        scene.MeshParts.Add(new GlbMeshPart
        {
            Name = "Atmosphere",
            Submesh = hierarchyGeometry
        });
        var modernModel = new NifRenderableModel();
        modernModel.Submeshes.Add(source);

        NifExportSceneBuilder.ApplyModernMaterialState(scene, modernModel);

        Assert.Equal(SkyObjectType.Sky, Assert.Single(scene.MeshParts).Submesh.SkyType);
    }

    private static RenderableSubmesh Triangle(byte[] vertexColors)
    {
        return new RenderableSubmesh
        {
            ShapeName = "Atmosphere",
            Positions =
            [
                0f, 0f, 0f,
                1f, 0f, 0f,
                0f, 1f, 0f
            ],
            Triangles = [0, 1, 2],
            Normals =
            [
                0f, 0f, 1f,
                0f, 0f, 1f,
                0f, 0f, 1f
            ],
            UVs = [0f, 0f, 1f, 0f, 0f, 1f],
            VertexColors = vertexColors,
            UseVertexColors = true,
            UseVertexAlphaForOpacity = true,
            SkyType = SkyObjectType.Sky
        };
    }

    private static void AssertColor(Vector3 expected, Vector4 actual)
    {
        // SharpGLTF may retain float COLOR_0 or pack it to normalized bytes. One UNORM8 step
        // accepts either representation without weakening the semantic row/coverage assertions.
        const float tolerance = (1f / 255f) + 1e-6f;
        Assert.InRange(MathF.Abs(expected.X - actual.X), 0f, tolerance);
        Assert.InRange(MathF.Abs(expected.Y - actual.Y), 0f, tolerance);
        Assert.InRange(MathF.Abs(expected.Z - actual.Z), 0f, tolerance);
    }

    private static ModelRoot Read(byte[] glb)
    {
        using var stream = new MemoryStream(glb, writable: false);
        return ModelRoot.ReadGLB(stream);
    }
}
