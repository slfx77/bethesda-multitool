using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Bsa.Ba2;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif.Materials;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Water;
using BethesdaMultitool.Tests.Core.Formats.Nif.Materials;
using BethesdaMultitool.Tests.Helpers;
using SharpGLTF.Schema2;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Water;

/// <summary>
///     Contracts for carrying CE2's decoded ShaderRoute::Water through the shared modern-NIF
///     extraction path. The route is deliberately bounded to classification and the viewer's
///     existing water approximations; this does not claim recovery of Starfield's retail shader.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
public sealed class StarfieldWaterMaterialRouteTests
{
    private const string MaterialPath = @"materials\test\orm.mat";
    private const string DatabasePath = @"materials\materialsbeta.cdb";
    private const string ExternalMeshPath = @"geometries\test\water.mesh";
    private const string RetailNifPath =
        @"meshes\setdressing\paradiso\paradiso_waterfall01.nif";
    private const string RetailWaterMaterialPath = @"Materials\Water\WaterCalm.mat";
    private const string RetailWaterMeshPath =
        @"geometries\faf9712ae20b4af66067\71b8ddac844a03aef57d.mesh";
    private const string StaleNormalPath = @"textures\test\stale_normal.dds";
    private const string OtherStaleNormalPath = @"textures\test\other_stale_normal.dds";
    private const string StaleGlowPath = @"textures\test\stale_glow.dds";
    private const string StaleHeightPath = @"textures\test\stale_height.dds";
    private const string StaleEnvironmentMaskPath = @"textures\test\stale_envmask.dds";
    private const string OrmRoughnessPath = @"textures\test\surface_rough.dds";
    private const string OrmAmbientOcclusionPath = @"textures\test\surface_ao.dds";

    [Fact]
    public void ExtractionSeam_ResolvedWaterPathSelectsSentinelBlendAndNoCutout()
    {
        using var resolver = CreateResolver("Water", "Water1Layer");
        string? diffusePath = MaterialPath;
        var colorPolicy = new StarfieldMaterialColorPolicy(
            true,
            false,
            StarfieldMaterialColorOverrideMode.Lerp,
            new Vector4(0.2f, 0.3f, 0.4f, 0.75f));
        var alphaPolicy = default(StarfieldMaterialAlphaPolicy) with { IsResolved = true };
        var hasAlphaBlend = false;
        var hasAlphaTest = true;
        var materialAlpha = 1f;

        var applied = StarfieldWaterMaterialRoute.TryApply(
            MaterialPath,
            resolver,
            ref diffusePath,
            ref colorPolicy,
            ref alphaPolicy,
            ref hasAlphaBlend,
            ref hasAlphaTest,
            ref materialAlpha);

        Assert.True(applied);
        Assert.Equal(RenderableSubmesh.WaterSurfaceTexturePath, diffusePath);
        Assert.Equal(default(StarfieldMaterialColorPolicy), colorPolicy);
        Assert.Equal(default(StarfieldMaterialAlphaPolicy), alphaPolicy);
        Assert.True(hasAlphaBlend);
        Assert.False(hasAlphaTest);
        Assert.Equal(0.5f, materialAlpha);
    }

    [Theory]
    [InlineData("Deferred", "Water1Layer")]
    [InlineData("Water1Layer", "Water1Layer")]
    public void ExtractionSeam_ModelOnlyOrMalformedRouteLeavesOrdinaryMaterialStateUnchanged(
        string shaderRoute,
        string shaderModel)
    {
        using var resolver = CreateResolver(shaderRoute, shaderModel);
        string? diffusePath = MaterialPath;
        var colorPolicy = new StarfieldMaterialColorPolicy(
            true,
            false,
            StarfieldMaterialColorOverrideMode.Lerp,
            new Vector4(0.2f, 0.3f, 0.4f, 0.75f));
        var expectedColorPolicy = colorPolicy;
        var alphaPolicy = default(StarfieldMaterialAlphaPolicy) with { IsResolved = true };
        var expectedAlphaPolicy = alphaPolicy;
        var hasAlphaBlend = false;
        var hasAlphaTest = true;
        var materialAlpha = 0.75f;

        var applied = StarfieldWaterMaterialRoute.TryApply(
            MaterialPath,
            resolver,
            ref diffusePath,
            ref colorPolicy,
            ref alphaPolicy,
            ref hasAlphaBlend,
            ref hasAlphaTest,
            ref materialAlpha);

        Assert.False(applied);
        Assert.Equal(MaterialPath, diffusePath);
        Assert.Equal(expectedColorPolicy, colorPolicy);
        Assert.Equal(expectedAlphaPolicy, alphaPolicy);
        Assert.False(hasAlphaBlend);
        Assert.True(hasAlphaTest);
        Assert.Equal(0.75f, materialAlpha);
    }

    [Fact]
    public void ModernExtractor_ResolvedWaterMaterialClassifiesExternalGeometryAsBlendWithoutCutout()
    {
        using var resolver = CreateResolver("Water", "Water1Layer");
        var (data, nif) = BuildExternalBsGeometryNif();
        var mesh = BuildExternalTriangleMesh();

        var model = Assert.IsType<NifRenderableModel>(NifGeometryExtractor.Extract(
            data,
            nif,
            resolver,
            externalMeshLoader: path =>
                string.Equals(path, ExternalMeshPath, StringComparison.Ordinal) ? mesh : null));

        var water = Assert.Single(model.Submeshes);
        Assert.Equal(RenderableSubmesh.WaterSurfaceTexturePath, water.DiffuseTexturePath);
        Assert.True(water.HasAlphaBlend);
        Assert.False(water.HasAlphaTest);
        Assert.Equal(0.5f, water.MaterialAlpha);
        Assert.Equal(default(StarfieldMaterialColorRenderState), water.StarfieldMaterialColor);
        Assert.Equal(default(StarfieldMaterialAlphaRenderState), water.StarfieldMaterialAlpha);
    }

    /// <summary>
    ///     Installed-retail seam from a named NIF through its actual external geometry and compiled
    ///     material. This is intentionally Bucket B: resolving the route parses Starfield's large
    ///     material database, so it must run only under the suite's explicit real-asset opt-in and
    ///     sequential integration collection.
    /// </summary>
    [Fact]
    [Trait("Category", BucketBTestGuard.Category)]
    public void RetailParadisoWaterCalm_ResolvesWaterRouteAndExtractsSentinel()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var meshesPath = RealAssetPaths.SteamGameFile(
            "Starfield", @"Data\Starfield - MeshesPatch.ba2");
        var materialsPath = RealAssetPaths.SteamGameFile(
            "Starfield", @"Data\Starfield - Materials.ba2");
        Assert.SkipUnless(meshesPath is not null,
            RealAssetPaths.SkipMessage("Starfield - MeshesPatch.ba2"));
        Assert.SkipUnless(materialsPath is not null,
            RealAssetPaths.SkipMessage("Starfield - Materials.ba2"));

        using var meshes = new Ba2Extractor(meshesPath!);
        var nifEntry = meshes.Archive.FindFile(RetailNifPath);
        Assert.NotNull(nifEntry);

        var nifData = meshes.ExtractFile(nifEntry!);
        var nif = Assert.IsType<NifInfo>(NifParser.Parse(nifData));
        using var resolver = new NifTextureResolver(materialsPath!);

        Assert.Equal(
            StarfieldMaterialShaderRoute.Water,
            resolver.ResolveStarfieldShaderRoute(RetailWaterMaterialPath));

        var requestedWaterBlob = false;
        byte[]? LoadExternalMesh(string path)
        {
            var normalized = path.Replace('/', '\\').Trim().TrimStart('\\');
            if (!normalized.StartsWith("geometries\\", StringComparison.OrdinalIgnoreCase))
            {
                normalized = "geometries\\" + normalized;
            }

            if (!normalized.EndsWith(".mesh", StringComparison.OrdinalIgnoreCase))
            {
                normalized += ".mesh";
            }

            var isRetailWaterBlob = string.Equals(
                normalized,
                RetailWaterMeshPath,
                StringComparison.OrdinalIgnoreCase);
            requestedWaterBlob |= isRetailWaterBlob;
            if (!isRetailWaterBlob)
            {
                // Isolate the seam: any surviving geometry must have come from WaterCalm's exact
                // external blob, not merely from some other BSGeometry in the same retail NIF.
                return null;
            }

            var entry = meshes.Archive.FindFile(normalized);
            return entry is null ? null : meshes.ExtractFile(entry);
        }

        var model = Assert.IsType<NifRenderableModel>(NifGeometryExtractor.Extract(
            nifData,
            nif,
            resolver,
            externalMeshLoader: LoadExternalMesh));
        var water = Assert.Single(model.Submeshes.Where(submesh => string.Equals(
            submesh.ShaderMetadata?.MaterialPath,
            RetailWaterMaterialPath,
            StringComparison.OrdinalIgnoreCase)));

        Assert.True(requestedWaterBlob);
        Assert.NotEmpty(water.Positions);
        Assert.NotEmpty(water.Triangles);
        Assert.Equal(RenderableSubmesh.WaterSurfaceTexturePath, water.DiffuseTexturePath);
        Assert.True(water.HasAlphaBlend);
        Assert.False(water.HasAlphaTest);
        Assert.Equal(0.5f, water.MaterialAlpha);
        Assert.Equal(default(StarfieldMaterialColorRenderState), water.StarfieldMaterialColor);
        Assert.Equal(default(StarfieldMaterialAlphaRenderState), water.StarfieldMaterialAlpha);
    }

    [Fact]
    public void ModernExtractorClassifiesOnlyResolvedWaterRouteOntoExistingSentinel()
    {
        var extractor = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering",
            "NifGeometryExtractor.cs");
        var route = SourceContract.Extract(
            extractor,
            "// Starfield's external .mesh colour stream",
            "if (materialPath is not null && textureResolver?.TryGetMaterial");

        SourceContract.AssertOrder(
            route,
            "starfieldColorPolicy = textureResolver.ResolveStarfieldBaseColorPolicy(materialPath);",
            "starfieldAlphaPolicy = textureResolver.ResolveStarfieldAlphaPolicy(materialPath);",
            "StarfieldWaterMaterialRoute.TryApply(",
            "ref diffusePath,",
            "ref starfieldColorPolicy,",
            "ref starfieldAlphaPolicy,",
            "ref hasAlphaBlend,",
            "ref hasAlphaTest,",
            "ref materialAlpha);");

        var materialResolver = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Textures",
            "MaterialTexturePathResolver.cs");
        Assert.Contains(
            "GetMaterialDatabase(sources)?.ResolveShaderRoute(materialPath)",
            materialResolver,
            StringComparison.Ordinal);
    }

    [Fact]
    public void WorldViewerDivertsSentinelGeometryAndSkipsOrdinaryReferenceDraw()
    {
        var cache = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "ReferenceMeshCache12.cs");
        var diversion = SourceContract.Extract(
            cache,
            "// Placed water geometry is NOT drawn as a reference slab.",
            "// Refraction geometry");

        SourceContract.AssertOrder(
            diversion,
            "RenderableSubmesh.WaterSurfaceTexturePath",
            "AppendWaterGeometry(sub.Vertices, sub.Indices, ref waterPlanesLocal);",
            "continue;");
    }

    [Fact]
    public void MeshViewerModernOverlayClonesWaterPathAndTransparencyState()
    {
        var browser = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering",
            "NifBrowserService.cs");
        Assert.Contains("NifExportSceneBuilder.ApplyModernMaterialState(scene, model);", browser,
            StringComparison.Ordinal);

        var sceneBuilder = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Export",
            "NifExportSceneBuilder.cs");
        Assert.Contains(
            "RenderableSubmeshCloner.CloneGeometryWithRenderState(geometry, materialSource)",
            sceneBuilder,
            StringComparison.Ordinal);
        var overlayClone = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering",
            "RenderableSubmeshCloner.cs");

        Assert.Contains("DiffuseTexturePath = renderState.DiffuseTexturePath", overlayClone,
            StringComparison.Ordinal);
        Assert.Contains("HasAlphaBlend = renderState.HasAlphaBlend", overlayClone,
            StringComparison.Ordinal);
        Assert.Contains("HasAlphaTest = renderState.HasAlphaTest", overlayClone,
            StringComparison.Ordinal);
        Assert.Contains("MaterialAlpha = renderState.MaterialAlpha", overlayClone,
            StringComparison.Ordinal);

        var glbWriter = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Export",
            "GlbWriter.cs");
        Assert.Contains("NpcGlbAlphaTexturePacker.Prepare(submesh, diffuseTexture)", glbWriter,
            StringComparison.Ordinal);
        Assert.Contains("NpcGlbTintColorEncoder.BuildBaseColor(submesh, preparedAlpha.Texture != null)",
            glbWriter,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MeshViewerGlb_WaterSentinelUsesCanonicalPhysicalCoverageAndRejectsStaleMaterialLanes()
    {
        using var resolver = CreateResolver(
            "Deferred",
            "BaseMaterial",
            includeMeshViewerWaterNormal: true,
            variableMeshViewerWaterNormalAlpha: true,
            includeStaleMaterialTextures: true);
        var staleOrmPolicy = resolver.ResolveStarfieldOrmPolicy(MaterialPath);
        Assert.True(staleOrmPolicy.TryResolveStaticLayer0Orm(out _),
            "The adversarial material fixture must expose an otherwise-applicable static ORM lane.");

        var scene = new GlbScene();
        scene.MeshParts.Add(new GlbMeshPart
        {
            Name = "WaterFallback",
            Submesh = new RenderableSubmesh
            {
                ShapeName = "WaterFallback",
                Positions = [0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f],
                Triangles = [0, 1, 2],
                Normals = [0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f],
                UVs = [0f, 0f, 1f, 0f, 0f, 1f],
                DiffuseTexturePath = RenderableSubmesh.WaterSurfaceTexturePath,
                NormalMapTexturePath = StaleNormalPath,
                ShaderMetadata = new NifShaderTextureMetadata
                {
                    MaterialPath = MaterialPath,
                    ShaderFlags = 1u << 7,
                    TextureSlots =
                    [
                        null,
                        StaleNormalPath,
                        StaleGlowPath,
                        StaleHeightPath,
                        null,
                        StaleEnvironmentMaskPath
                    ]
                },
                IsEmissive = true,
                IsDoubleSided = false,
                BgsmGlowMapTexturePath = StaleGlowPath,
                BgsmEmissionColor = new Vector3(4f, 2f, 1f),
                HasAlphaBlend = true,
                HasAlphaTest = true,
                AlphaTestFunction = 7,
                MaterialAlpha = 0.5f,
                // Specialized/older callers can preserve stale ordinary CE2 state. The sentinel is
                // authoritative and must still export only the marked physical water preview.
                StarfieldMaterialAlpha = new StarfieldMaterialAlphaRenderState(
                    StarfieldMaterialAlphaRenderMode.Layer0OpacityCutout,
                    0.25f),
                StarfieldMaterialColor = new StarfieldMaterialColorRenderState(
                    StarfieldMaterialColorRenderMode.ConstantLerp,
                    new Vector4(0.2f, 0.3f, 0.4f, 0.75f))
            }
        });
        scene.MeshParts.Add(new GlbMeshPart
        {
            Name = "WaterFallbackDuplicate",
            Submesh = new RenderableSubmesh
            {
                ShapeName = "WaterFallbackDuplicate",
                Positions = [2f, 0f, 0f, 3f, 0f, 0f, 2f, 1f, 0f],
                Triangles = [0, 1, 2],
                Normals = [0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f],
                UVs = [0f, 0f, 1f, 0f, 0f, 1f],
                DiffuseTexturePath = RenderableSubmesh.WaterSurfaceTexturePath,
                NormalMapTexturePath = OtherStaleNormalPath,
                HasAlphaBlend = false,
                HasAlphaTest = true,
                AlphaTestFunction = 2,
                MaterialAlpha = 0.1f,
                IsDoubleSided = true
            }
        });

        var bytes = GlbWriter.WriteToBytes(scene, resolver);

        using var stream = new MemoryStream(bytes, writable: false);
        var model = ModelRoot.ReadGLB(stream);
        var material = Assert.Single(model.LogicalMaterials);
        var baseColor = Assert.IsType<MaterialChannel>(material.FindChannel("BaseColor"));
        Assert.Equal(SharpGLTF.Schema2.AlphaMode.OPAQUE, material.Alpha);
        Assert.True(material.DoubleSided);
        Assert.False(material.Unlit);
        Assert.Null(baseColor.Texture);
        Assert.Equal(Vector4.One, baseColor.Color);
        var metallicRoughness = Assert.IsType<MaterialChannel>(
            material.FindChannel("MetallicRoughness"));
        Assert.Null(metallicRoughness.Texture);
        Assert.Equal(0f, metallicRoughness.Parameter.X);
        Assert.Equal(
            StarfieldWaterMaterialRoute.MeshViewerRoughness,
            metallicRoughness.Parameter.Y,
            3);
        Assert.Equal(
            StarfieldWaterMaterialRoute.MeshViewerIndexOfRefraction,
            material.IndexOfRefraction,
            3);
        Assert.Equal(
            StarfieldWaterMaterialRoute.MeshViewerTransmission,
            Assert.IsType<MaterialChannel>(material.FindChannel("Transmission")).Parameter.X,
            3);
        Assert.Equal(
            StarfieldWaterMaterialRoute.MeshViewerClearCoat,
            Assert.IsType<MaterialChannel>(material.FindChannel("ClearCoat")).Parameter.X,
            3);
        Assert.Equal(
            StarfieldWaterMaterialRoute.MeshViewerClearCoatRoughness,
            Assert.IsType<MaterialChannel>(material.FindChannel("ClearCoatRoughness")).Parameter.X,
            3);
        var normal = Assert.IsType<MaterialChannel>(material.FindChannel("Normal"));
        var normalTexture = Assert.IsType<SharpGLTF.Schema2.Texture>(normal.Texture);
        var normalSampler = Assert.IsType<TextureSampler>(normal.TextureSampler);
        Assert.Equal(TextureWrapMode.REPEAT, normalSampler.WrapS);
        Assert.Equal(TextureWrapMode.REPEAT, normalSampler.WrapT);
        Assert.Equal("defaultwater_normal.normal.png", normalTexture.PrimaryImage.Name);
        var occlusion = Assert.IsType<MaterialChannel>(material.FindChannel("Occlusion"));
        Assert.True(occlusion.HasDefaultContent);
        Assert.Null(occlusion.Texture);
        Assert.Null(material.FindChannel("SpecularFactor"));
        var emissive = Assert.IsType<MaterialChannel>(material.FindChannel("Emissive"));
        Assert.True(emissive.HasDefaultContent);
        Assert.Null(emissive.Texture);
        Assert.Contains("global-normal physical preview (approx.)", material.Name,
            StringComparison.Ordinal);
        var extras = Assert.IsType<JsonObject>(material.Extras);
        Assert.True(extras[StarfieldWaterMaterialRoute.MeshViewerMaterialExtrasKey]!.GetValue<bool>());
    }

    private static NifTextureResolver CreateResolver(
        string shaderRoute,
        string shaderModel,
        bool includeMeshViewerWaterNormal = false,
        bool variableMeshViewerWaterNormalAlpha = false,
        bool includeStaleMaterialTextures = false)
    {
        var database = StarfieldMaterialOrmPolicyTests.BuildDatabase(
            useDiffChunks: true,
            shaderRoute: shaderRoute,
            shaderModel: shaderModel);
        var waterNormal = includeMeshViewerWaterNormal
            ? DecodedTexture.FromBaseLevel(
            [
                128, 128, 255, variableMeshViewerWaterNormalAlpha ? (byte)0 : byte.MaxValue,
                144, 112, 252, 255
            ], 2, 1, false)
            : null;
        var staleMaterialTexture = includeStaleMaterialTextures
            ? DecodedTexture.FromBaseLevel(
            [
                32, 96, 192, 0,
                224, 160, 64, 255
            ], 2, 1, false)
            : null;
        return new NifTextureResolver(
            [new MaterialDatabaseSource(database, waterNormal, staleMaterialTexture)]);
    }

    private static (byte[] Data, NifInfo Nif) BuildExternalBsGeometryNif()
    {
        var shape = new List<byte>();
        shape.AddRange(BitConverter.GetBytes(-1)); // unnamed NiObjectNET
        shape.AddRange(BitConverter.GetBytes(0u)); // no extra data
        shape.AddRange(BitConverter.GetBytes(-1)); // no controller
        shape.AddRange(BitConverter.GetBytes(0u)); // external-mesh NiAVObject flags
        shape.AddRange(new byte[12]); // translation
        foreach (var value in new[] { 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f })
        {
            shape.AddRange(BitConverter.GetBytes(value));
        }

        shape.AddRange(BitConverter.GetBytes(1f)); // scale
        shape.AddRange(BitConverter.GetBytes(-1)); // no collision object
        shape.AddRange(new byte[16 + 24]); // bounding sphere + box
        shape.AddRange(BitConverter.GetBytes(-1)); // no skin
        shape.AddRange(BitConverter.GetBytes(1)); // BSLightingShaderProperty block
        shape.AddRange(BitConverter.GetBytes(-1)); // no NiAlphaProperty
        shape.Add(1); // LOD 0 has an external mesh
        shape.AddRange(new byte[12]); // redundant index/vertex/flags summary
        var pathBytes = Encoding.ASCII.GetBytes(ExternalMeshPath);
        shape.AddRange(BitConverter.GetBytes((uint)pathBytes.Length));
        shape.AddRange(pathBytes);

        var shader = BitConverter.GetBytes(0); // String-table index of MaterialPath
        var data = shape.Concat(shader).ToArray();
        var nif = new NifInfo
        {
            BinaryVersion = 0x14020007,
            BsVersion = 173,
            BlockCount = 2
        };
        nif.Strings.Add(MaterialPath);
        nif.BlockTypeNames.Add("BSGeometry");
        nif.BlockTypeNames.Add("BSLightingShaderProperty");
        nif.Blocks.Add(new BlockInfo
        {
            Index = 0,
            TypeIndex = 0,
            TypeName = "BSGeometry",
            Size = shape.Count,
            DataOffset = 0
        });
        nif.Blocks.Add(new BlockInfo
        {
            Index = 1,
            TypeIndex = 1,
            TypeName = "BSLightingShaderProperty",
            Size = shader.Length,
            DataOffset = shape.Count
        });
        return (data, nif);
    }

    private static byte[] BuildExternalTriangleMesh()
    {
        var mesh = new List<byte>();
        mesh.AddRange(BitConverter.GetBytes(2u)); // container version
        mesh.AddRange(BitConverter.GetBytes(3u));
        foreach (ushort index in new ushort[] { 0, 1, 2 })
        {
            mesh.AddRange(BitConverter.GetBytes(index));
        }

        mesh.AddRange(BitConverter.GetBytes(1f)); // position scale
        mesh.AddRange(BitConverter.GetBytes(0u)); // weights per vertex
        mesh.AddRange(BitConverter.GetBytes(3u));
        foreach (var (x, y, z) in new[]
                 {
                     ((short)0, (short)0, (short)0),
                     (short.MaxValue, (short)0, (short)0),
                     ((short)0, short.MaxValue, (short)0)
                 })
        {
            mesh.AddRange(BitConverter.GetBytes((uint)((ushort)x | ((uint)(ushort)y << 16))));
            mesh.AddRange(BitConverter.GetBytes((ushort)z));
        }

        // UV0, UV1, colours, normals, tangents, skin weights, LODs, meshlets, and cull records.
        for (var i = 0; i < 9; i++)
        {
            mesh.AddRange(BitConverter.GetBytes(0u));
        }

        return [.. mesh];
    }

    private sealed class MaterialDatabaseSource(
        byte[] database,
        DecodedTexture? meshViewerWaterNormal = null,
        DecodedTexture? staleMaterialTexture = null) : INifTextureSource
    {
        public DecodedTexture? TryLoad(string path)
        {
            if (string.Equals(
                path,
                StarfieldWaterMaterialRoute.MeshViewerPrimaryNormalTexturePath,
                StringComparison.OrdinalIgnoreCase))
            {
                return meshViewerWaterNormal;
            }

            return staleMaterialTexture is not null && IsStaleMaterialTexture(path)
                ? staleMaterialTexture
                : null;
        }

        public byte[]? TryLoadRaw(string path) =>
            string.Equals(path, DatabasePath, StringComparison.OrdinalIgnoreCase) ? database : null;

        public bool Exists(string path) =>
            string.Equals(path, DatabasePath, StringComparison.OrdinalIgnoreCase) ||
            meshViewerWaterNormal is not null && string.Equals(
                path,
                StarfieldWaterMaterialRoute.MeshViewerPrimaryNormalTexturePath,
                StringComparison.OrdinalIgnoreCase) ||
            staleMaterialTexture is not null && IsStaleMaterialTexture(path);

        public bool TryGetAssetMetadata(string path, out NifTextureSourceAssetMetadata metadata)
        {
            if (string.Equals(path, DatabasePath, StringComparison.OrdinalIgnoreCase))
            {
                metadata = new NifTextureSourceAssetMetadata(
                    "fixture-materialsbeta.cdb",
                    database.Length,
                    1);
                return true;
            }

            if (meshViewerWaterNormal is not null && string.Equals(
                    path,
                    StarfieldWaterMaterialRoute.MeshViewerPrimaryNormalTexturePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                metadata = new NifTextureSourceAssetMetadata(
                    "fixture-defaultwater_normal.dds",
                    meshViewerWaterNormal.Pixels.Length,
                    1);
                return true;
            }

            if (staleMaterialTexture is not null && IsStaleMaterialTexture(path))
            {
                metadata = new NifTextureSourceAssetMetadata(
                    "fixture-stale-material-texture.dds",
                    staleMaterialTexture.Pixels.Length,
                    1);
                return true;
            }

            metadata = default;
            return false;
        }

        public void Dispose()
        {
        }

        private static bool IsStaleMaterialTexture(string path) =>
            string.Equals(path, StaleNormalPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, OtherStaleNormalPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, StaleGlowPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, StaleHeightPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, StaleEnvironmentMaskPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, OrmRoughnessPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, OrmAmbientOcclusionPath, StringComparison.OrdinalIgnoreCase);
    }
}
