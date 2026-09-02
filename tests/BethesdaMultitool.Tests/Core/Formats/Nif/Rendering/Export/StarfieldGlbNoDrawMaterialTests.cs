using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using BethesdaMultitool.Tests.Core.Formats.Nif.Materials;
using BethesdaMultitool.Tests.Helpers;
using SharpGLTF.Schema2;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

public sealed class StarfieldGlbNoDrawMaterialTests
{
    private const string MaterialPath = @"materials\test\orm.mat";
    private const string DatabasePath = @"materials\materialsbeta.cdb";

    [Fact]
    public void WriteToBytes_DatabaseConfirmedDeferredNoAlbedoHelperDoesNotBecomeWhiteSurface()
    {
        using var resolver = Resolver("Deferred");
        var helper = Triangle("NormalOnlyProxy", MaterialPath);
        helper.NormalMapTexturePath =
            MaterialTexturePathResolver.BuildStarfieldNormalMapRequest(MaterialPath);
        var scene = Scene(helper, Triangle("VisibleSurface", null));

        Assert.True(resolver.IsStarfieldNoDrawMaterial(MaterialPath));
        Assert.True(GlbWriter.ShouldSkipStarfieldNoDrawSubmesh(helper, resolver));

        var glb = GlbWriter.WriteToBytes(scene, resolver);

        using var stream = new MemoryStream(glb, writable: false);
        var model = ModelRoot.ReadGLB(stream);
        var mesh = Assert.Single(model.LogicalMeshes);
        Assert.Equal("VisibleSurface", mesh.Name);
        Assert.DoesNotContain(
            model.LogicalMaterials,
            material => string.Equals(material.Name, "NormalOnlyProxy", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Effect")]
    [InlineData("PlanetaryRing")]
    [InlineData("PrecomputedScattering")]
    [InlineData("Water")]
    public void SpecializedNoAlbedoRouteRemainsExportable(string shaderRoute)
    {
        using var resolver = Resolver(shaderRoute);
        var submesh = Triangle($"{shaderRoute}Surface", MaterialPath);

        Assert.True(resolver.IsStarfieldNoDrawMaterial(MaterialPath));
        Assert.False(GlbWriter.ShouldSkipStarfieldNoDrawSubmesh(submesh, resolver));

        var glb = GlbWriter.WriteToBytes(Scene(submesh), resolver);

        using var stream = new MemoryStream(glb, writable: false);
        Assert.Equal(submesh.ShapeName, Assert.Single(ModelRoot.ReadGLB(stream).LogicalMeshes).Name);
    }

    [Fact]
    public void MissingMaterialDatabaseRecordRemainsVisibleAsDiagnostic()
    {
        using var resolver = new NifTextureResolver(_ => null);
        var submesh = Triangle("MissingMaterial", @"materials\test\missing.mat");

        Assert.False(resolver.IsStarfieldNoDrawMaterial(submesh.DiffuseTexturePath!));
        Assert.False(GlbWriter.ShouldSkipStarfieldNoDrawSubmesh(submesh, resolver));
    }

    [Fact]
    public void MalformedShaderRoute_RemainsVisibleAsDiagnostic()
    {
        using var resolver = Resolver("Water1Layer");
        var submesh = Triangle("MalformedRoute", MaterialPath);

        Assert.True(resolver.IsStarfieldNoDrawMaterial(MaterialPath));
        Assert.Null(resolver.ResolveStarfieldShaderRoute(MaterialPath));
        Assert.False(GlbWriter.ShouldSkipStarfieldNoDrawSubmesh(submesh, resolver));

        var glb = GlbWriter.WriteToBytes(Scene(submesh), resolver);

        using var stream = new MemoryStream(glb, writable: false);
        Assert.Equal(submesh.ShapeName, Assert.Single(ModelRoot.ReadGLB(stream).LogicalMeshes).Name);
    }

    [Fact]
    public void MetadataOnlyMaterialIdentity_IsSuppressedAndAllHelperSceneRemainsValidGlb()
    {
        using var resolver = Resolver("Deferred");
        var helper = Triangle("MetadataOnlyProxy", null, MaterialPath);

        Assert.True(GlbWriter.ShouldSkipStarfieldNoDrawSubmesh(helper, resolver));

        var glb = GlbWriter.WriteToBytes(Scene(helper), resolver);

        using var stream = new MemoryStream(glb, writable: false);
        Assert.Empty(ModelRoot.ReadGLB(stream).LogicalMeshes);
    }

    [Fact]
    public void ExportFilterRunsBeforeMaterialOrVertexProjectionAndUsesSharedDatabaseClassifier()
    {
        var writer = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Export", "GlbWriter.cs");
        var loop = SourceContract.Extract(
            writer,
            "foreach (var meshPart in scene.MeshParts)",
            "return sceneBuilder.ToGltf2();");

        SourceContract.AssertOrder(
            loop,
            "ShouldSkipStarfieldNoDrawSubmesh(meshPart.Submesh, textureResolver)",
            "NormalizeWinding(meshPart.Submesh)",
            "BuildSkinnedMesh(meshPart, textureResolver, materialCache)");
        Assert.Contains(
            "!textureResolver.IsStarfieldNoDrawMaterial(materialPath)",
            writer,
            StringComparison.Ordinal);
        Assert.Contains(
            "var materialPath = submesh.ShaderMetadata?.MaterialPath",
            writer,
            StringComparison.Ordinal);
        Assert.Contains(
            "StarfieldMaterialShaderRoute.Deferred",
            writer,
            StringComparison.Ordinal);

        var resolver = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "NifTextureResolver.cs");
        Assert.Contains(
            "MaterialTexturePathResolver.IsStarfieldNoDrawMaterial(materialPath, _sources)",
            resolver,
            StringComparison.Ordinal);
    }

    private static NifTextureResolver Resolver(string shaderRoute)
    {
        var database = StarfieldMaterialOrmPolicyTests.BuildDatabase(
            useDiffChunks: true,
            shaderRoute: shaderRoute);
        return new NifTextureResolver([new MaterialDatabaseSource(database)]);
    }

    private static GlbScene Scene(params RenderableSubmesh[] submeshes)
    {
        var scene = new GlbScene();
        foreach (var submesh in submeshes)
        {
            scene.MeshParts.Add(new GlbMeshPart
            {
                Name = submesh.ShapeName!,
                NodeIndex = GlbScene.RootNodeIndex,
                Submesh = submesh
            });
        }

        return scene;
    }

    private static RenderableSubmesh Triangle(
        string name,
        string? materialPath,
        string? metadataMaterialPath = null)
    {
        return new RenderableSubmesh
        {
            ShapeName = name,
            Positions = [0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f],
            Triangles = [0, 1, 2],
            Normals = [0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f],
            UVs = [0f, 0f, 1f, 0f, 0f, 1f],
            DiffuseTexturePath = materialPath,
            ShaderMetadata = metadataMaterialPath is null
                ? null
                : new NifShaderTextureMetadata
                {
                    MaterialPath = metadataMaterialPath
                }
        };
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
