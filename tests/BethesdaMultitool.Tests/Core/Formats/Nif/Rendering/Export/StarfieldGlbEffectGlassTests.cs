using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using BethesdaMultitool.Tests.Core.Formats.Nif.Materials;
using SharpGLTF.Schema2;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

public sealed class StarfieldGlbEffectGlassTests
{
    private const string MaterialPath = @"materials\test\orm.mat";
    private const string DatabasePath = @"materials\materialsbeta.cdb";

    [Fact]
    public void WriteToBytes_AuthoredEffectGlassUsesBlendAndOverallAlpha()
    {
        using var resolver = Resolver("AlphaBlend", 0.35f);

        var model = Read(GlbWriter.WriteToBytes(Scene(), resolver));

        var material = Assert.Single(model.LogicalMaterials);
        Assert.Equal(AlphaMode.BLEND, material.Alpha);
        var baseColor = Assert.IsType<MaterialChannel>(material.FindChannel("BaseColor"));
        Assert.Null(baseColor.Texture);
        Assert.Equal(0.35f, baseColor.Color.W);
        Assert.DoesNotContain("glass alpha omitted", material.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteToBytes_AdditiveGlassFailsClosedWithVisibleDiagnostic()
    {
        using var resolver = Resolver("Additive", 0.35f);

        var model = Read(GlbWriter.WriteToBytes(Scene(), resolver));

        var material = Assert.Single(model.LogicalMaterials);
        Assert.Equal(AlphaMode.OPAQUE, material.Alpha);
        Assert.Contains("glass alpha omitted", material.Name, StringComparison.Ordinal);
    }

    private static NifTextureResolver Resolver(string blendMode, float materialAlpha)
    {
        var database = StarfieldMaterialOrmPolicyTests.BuildDatabase(
            useDiffChunks: true,
            shaderRoute: "Effect",
            shaderModel: "1LayerEffectGlass",
            effectSettings: new StarfieldEffectSettingsFixture(
                IsGlass: true,
                HasFrosting: false,
                UsesVertexColor: false,
                MaterialOverallAlpha: materialAlpha,
                BlendingMode: blendMode));
        return new NifTextureResolver([new MaterialDatabaseSource(database)]);
    }

    private static GlbScene Scene()
    {
        var submesh = new RenderableSubmesh
        {
            ShapeName = "ConstellationHelmetVisor",
            Positions = [0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f],
            Triangles = [0, 1, 2],
            Normals = [0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f],
            UVs = [0f, 0f, 1f, 0f, 0f, 1f],
            DiffuseTexturePath = MaterialPath
        };
        var scene = new GlbScene();
        scene.MeshParts.Add(new GlbMeshPart
        {
            Name = submesh.ShapeName,
            NodeIndex = GlbScene.RootNodeIndex,
            Submesh = submesh
        });
        return scene;
    }

    private static ModelRoot Read(byte[] glb)
    {
        using var stream = new MemoryStream(glb, writable: false);
        return ModelRoot.ReadGLB(stream);
    }

    private sealed class MaterialDatabaseSource(byte[] database) : INifTextureSource
    {
        public DecodedTexture? TryLoad(string path) => null;

        public byte[]? TryLoadRaw(string path) =>
            string.Equals(path, DatabasePath, StringComparison.OrdinalIgnoreCase) ? database : null;

        public bool Exists(string path) =>
            string.Equals(path, DatabasePath, StringComparison.OrdinalIgnoreCase);

        public bool TryGetAssetMetadata(string path, out NifTextureSourceAssetMetadata metadata)
        {
            metadata = new NifTextureSourceAssetMetadata("fixture-materialsbeta.cdb", database.Length, 1);
            return Exists(path);
        }

        public void Dispose()
        {
        }
    }
}
