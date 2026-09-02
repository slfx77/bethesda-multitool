using System.Numerics;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif.Materials;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using SharpGLTF.Schema2;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

public sealed class StarfieldGlbVertexLerpProjectionTests
{
    private const string DiffusePath = @"materials\test\vertex_lerp.mat";

    [Fact]
    public void Resolve_ClassifiesExactPortableAndEmbeddedViewerCases()
    {
        var noOp = StarfieldGlbVertexLerpProjection.Resolve(Submesh(
        [
            10, 20, 30, 0,
            50, 60, 70, 0,
            90, 100, 110, 0
        ]));
        Assert.Equal(StarfieldGlbVertexLerpProjectionMode.NoOp, noOp.Mode);

        var fullWeight = StarfieldGlbVertexLerpProjection.Resolve(Submesh(
        [
            10, 20, 30, 255,
            50, 60, 70, 255,
            90, 100, 110, 255
        ]));
        Assert.Equal(StarfieldGlbVertexLerpProjectionMode.VertexRgb, fullWeight.Mode);
        Assert.True(fullWeight.OmitDiffuseTexture);

        var uniform = StarfieldGlbVertexLerpProjection.Resolve(Submesh(
            Enumerable.Repeat(new byte[] { 64, 128, 192, 102 }, 3).SelectMany(x => x).ToArray()));
        Assert.Equal(StarfieldGlbVertexLerpProjectionMode.UniformTextureBake, uniform.Mode);
        Assert.True(uniform.IsUniformTextureBake);
        Assert.Equal(
            new Vector4(64f / 255f, 128f / 255f, 192f / 255f, 102f / 255f),
            uniform.ConstantLerpState.LinearTint);

        var varying = StarfieldGlbVertexLerpProjection.Resolve(Submesh(
        [
            10, 20, 30, 40,
            50, 60, 70, 80,
            90, 100, 110, 120
        ]));
        Assert.Equal(StarfieldGlbVertexLerpProjectionMode.VaryingViewerShader, varying.Mode);
        Assert.True(varying.RequiresViewerShader);
        Assert.False(varying.IsUnsupported);
    }

    [Fact]
    public void WriteToBytes_FullWeightExportsVertexRgbAndOmitsAlbedoTexture()
    {
        byte[] colors =
        [
            10, 20, 30, 255,
            50, 60, 70, 255,
            90, 100, 110, 255
        ];
        var scene = Scene(Submesh(colors));
        using var resolver = ResolverWithDiffuse();

        var model = Read(GlbWriter.WriteToBytes(scene, resolver));

        var material = Assert.Single(model.LogicalMaterials);
        var baseColor = Assert.IsType<MaterialChannel>(material.FindChannel("BaseColor"));
        Assert.Null(baseColor.Texture);
        var primitive = Assert.Single(model.LogicalMeshes).Primitives.Single();
        Assert.False(primitive.VertexAccessors.ContainsKey("COLOR_1"));
        var exported = primitive.GetVertexAccessor("COLOR_0").AsVector4Array().ToArray();
        Assert.Equal(3, exported.Length);
        for (var i = 0; i < exported.Length; i++)
        {
            Assert.Equal(colors[i * 4] / 255f, exported[i].X, 6);
            Assert.Equal(colors[i * 4 + 1] / 255f, exported[i].Y, 6);
            Assert.Equal(colors[i * 4 + 2] / 255f, exported[i].Z, 6);
            Assert.Equal(1f, exported[i].W);
        }
    }

    [Fact]
    public void WriteToBytes_VaryingAffineLerpEmitsExactViewerLaneAndNeutralPortableFallback()
    {
        byte[] colors =
        [
            10, 20, 30, 40,
            50, 60, 70, 80,
            90, 100, 110, 120
        ];
        var scene = Scene(Submesh(colors));
        using var resolver = ResolverWithDiffuse();

        var model = Read(GlbWriter.WriteToBytes(scene, resolver));

        var material = Assert.Single(model.LogicalMaterials);
        Assert.Contains("exact in embedded Mesh Viewer", material.Name, StringComparison.Ordinal);
        Assert.True(
            material.Extras is JsonObject extras &&
            extras[StarfieldGlbVertexLerpProjection.ViewerMaterialExtrasKey]?.GetValue<bool>() == true);
        Assert.NotNull(Assert.IsType<MaterialChannel>(material.FindChannel("BaseColor")).Texture);
        var primitive = Assert.Single(model.LogicalMeshes).Primitives.Single();
        Assert.All(
            primitive.GetVertexAccessor("COLOR_0").AsVector4Array(),
            color => Assert.Equal(Vector4.One, color));

        var viewerColors = primitive.GetVertexAccessor("COLOR_1").AsVector4Array().ToArray();
        Assert.Equal(3, viewerColors.Length);
        for (var i = 0; i < viewerColors.Length; i++)
        {
            Assert.Equal(colors[i * 4] / 255f, viewerColors[i].X, 6);
            Assert.Equal(colors[i * 4 + 1] / 255f, viewerColors[i].Y, 6);
            Assert.Equal(colors[i * 4 + 2] / 255f, viewerColors[i].Z, 6);
            Assert.Equal(colors[i * 4 + 3] / 255f, viewerColors[i].W, 6);
        }
    }

    [Fact]
    public void WriteToBytes_MalformedVertexLerpNeverReceivesViewerMarkerOrSecondColorLane()
    {
        var scene = Scene(Submesh([10, 20, 30, 40]));
        using var resolver = ResolverWithDiffuse();

        var model = Read(GlbWriter.WriteToBytes(scene, resolver));

        var material = Assert.Single(model.LogicalMaterials);
        Assert.Contains("missing or incomplete RGBA stream", material.Name, StringComparison.Ordinal);
        Assert.False(
            material.Extras is JsonObject extras &&
            extras[StarfieldGlbVertexLerpProjection.ViewerMaterialExtrasKey]?.GetValue<bool>() == true);
        var primitive = Assert.Single(model.LogicalMeshes).Primitives.Single();
        Assert.False(primitive.VertexAccessors.ContainsKey("COLOR_1"));
        Assert.All(
            primitive.GetVertexAccessor("COLOR_0").AsVector4Array(),
            color => Assert.Equal(Vector4.One, color));
    }

    [Fact]
    public void WriteToBytes_SkinnedVaryingAffineLerpRetainsExactViewerLane()
    {
        var scene = SkinnedScene(Submesh(
        [
            10, 20, 30, 40,
            50, 60, 70, 80,
            90, 100, 110, 120
        ]));
        using var resolver = ResolverWithDiffuse();

        var model = Read(GlbWriter.WriteToBytes(scene, resolver));

        var primitive = Assert.Single(model.LogicalMeshes).Primitives.Single();
        Assert.True(primitive.VertexAccessors.ContainsKey("JOINTS_0"));
        Assert.Equal(3, primitive.GetVertexAccessor("COLOR_1").AsVector4Array().Count);
    }

    [Fact]
    public void WriteToBytes_NonStarfieldMeshNeverReceivesViewerMarkerOrSecondColorLane()
    {
        var scene = Scene(Submesh(
        [
            10, 20, 30, 40,
            50, 60, 70, 80,
            90, 100, 110, 120
        ], isVertexLerp: false));
        using var resolver = ResolverWithDiffuse();

        var model = Read(GlbWriter.WriteToBytes(scene, resolver));

        var material = Assert.Single(model.LogicalMaterials);
        Assert.False(
            material.Extras is JsonObject extras &&
            extras[StarfieldGlbVertexLerpProjection.ViewerMaterialExtrasKey]?.GetValue<bool>() == true);
        Assert.False(
            Assert.Single(model.LogicalMeshes).Primitives.Single().VertexAccessors.ContainsKey("COLOR_1"));
    }

    private static RenderableSubmesh Submesh(byte[] colors, bool isVertexLerp = true)
    {
        return new RenderableSubmesh
        {
            ShapeName = "VertexLerp",
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
            VertexColors = colors,
            UseVertexColors = true,
            UseVertexAlphaForOpacity = false,
            DiffuseTexturePath = DiffusePath,
            StarfieldMaterialColor = isVertexLerp
                ? new StarfieldMaterialColorRenderState(
                    StarfieldMaterialColorRenderMode.VertexLerp,
                    Vector4.Zero)
                : default
        };
    }

    private static GlbScene Scene(RenderableSubmesh submesh)
    {
        var scene = new GlbScene();
        scene.MeshParts.Add(new GlbMeshPart
        {
            Name = "VertexLerp",
            Submesh = submesh
        });
        return scene;
    }

    private static GlbScene SkinnedScene(RenderableSubmesh submesh)
    {
        var scene = new GlbScene();
        var joint = scene.AddNode(
            "Joint",
            GlbScene.RootNodeIndex,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            GlbNodeKind.Skeleton);
        scene.MeshParts.Add(new GlbMeshPart
        {
            Name = "VertexLerp",
            Submesh = submesh,
            Skin = new GlbSkinBinding
            {
                JointNodeIndices = [joint],
                InverseBindMatrices = [Matrix4x4.Identity],
                PerVertexInfluences =
                [
                    [(BoneIdx: 0, Weight: 1f)],
                    [(BoneIdx: 0, Weight: 1f)],
                    [(BoneIdx: 0, Weight: 1f)]
                ]
            }
        });
        return scene;
    }

    private static NifTextureResolver ResolverWithDiffuse()
    {
        var texture = DecodedTexture.FromBaseLevel([32, 64, 128, 255], 1, 1, false);
        return new NifTextureResolver(
            path => string.Equals(path, DiffusePath, StringComparison.OrdinalIgnoreCase)
                ? texture
                : null);
    }

    private static ModelRoot Read(byte[] glb)
    {
        using var stream = new MemoryStream(glb, writable: false);
        return ModelRoot.ReadGLB(stream);
    }
}
