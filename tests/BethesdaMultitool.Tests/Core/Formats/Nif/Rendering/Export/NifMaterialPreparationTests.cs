using System.Numerics;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif;
using BethesdaMultitool.Core.Formats.Nif.Materials;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Water;
using BethesdaMultitool.Tests.Core.Formats.Nif.Materials;
using ImageMagick;
using SharpGLTF.Schema2;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Media.Models;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

/// <summary>Checks independent pixel/factor expectations before comparing both material consumers.</summary>
public sealed class NifMaterialPreparationTests
{
    /// <summary>Normal/gloss and height inputs keep their channel packing, factors, coverage and addressing.</summary>
    [Fact]
    public void MultiMapSurface_ReachesBothWritersWithExpectedPixelsAndFactors()
    {
        var textures = new Dictionary<string, DecodedTexture>
        {
            ["surface.dds"] = Texture([10, 20, 30, 0, 40, 50, 60, 255]),
            ["surface_n.dds"] = Texture([128, 32, 255, 0, 128, 192, 255, 255]),
            ["height.dds"] = Texture([29, 29, 29, 255, 220, 220, 220, 255])
        };
        var normalizedTextures = textures.ToDictionary(pair => NifTexturePathUtility.Normalize(pair.Key), pair => pair.Value);
        using var resolver = new NifTextureResolver(path => normalizedTextures.GetValueOrDefault(path));
        var part = Surface("surface.dds", "surface_n.dds", new NifShaderTextureMetadata
        {
            TextureSlots = [null, null, null, "height.dds"]
        });
        part.HasAlphaTest = true;
        part.AlphaTestThreshold = 128;
        part.IsDoubleSided = true;
        part.ClampTextureU = true;
        var prepared = Prepare(part, resolver);

        Assert.False(prepared.Unlit);
        Assert.Equal(0.92f, prepared.RoughnessFactor, 6);
        Assert.Equal(0.21f, prepared.SpecularFactor, 6);
        Assert.Equal(0f, prepared.MetallicFactor);
        Assert.Equal(SceneAlphaMode.Mask, prepared.AlphaMode);
        Assert.Equal(129f / 255f, prepared.AlphaCutoff);
        Assert.Equal(new byte[] { 128, 223, 255, 0, 128, 63, 255, 255 }, Pixels(prepared.NormalImage!.Png));
        Assert.Equal(new byte[] { 255, 255, 0, 255, 255, 38, 0, 255 }, Pixels(prepared.MetallicRoughnessImage!.Png));
        Assert.Equal(new byte[] { 255, 255, 255, 0, 255, 255, 255, 255 }, Pixels(prepared.SpecularImage!.Png));
        Assert.Equal(new byte[] { 29, 29, 29, 255, 220, 220, 220, 255 }, Pixels(prepared.OcclusionImage!.Png));
        Assert.Equal(0.35f, prepared.OcclusionStrength);

        var document = Adapt(Scene(part), resolver);
        Assert.NotNull(document.Meshes[0].Primitives[0].Tangents);
        foreach (var model in new[] { Native(Scene(part), resolver), Shared(document) })
        {
            var material = Assert.Single(model.LogicalMaterials);
            Assert.Equal(AlphaMode.MASK, material.Alpha);
            Assert.Equal(129f / 255f, material.AlphaCutoff);
            Assert.True(material.DoubleSided);
            AssertChannel(material, "BaseColor", textures["surface.dds"].Pixels);
            AssertChannel(material, "Normal", Pixels(prepared.NormalImage.Png));
            AssertChannel(material, "MetallicRoughness", Pixels(prepared.MetallicRoughnessImage.Png));
            AssertChannel(material, "SpecularFactor", Pixels(prepared.SpecularImage.Png));
            AssertChannel(material, "Occlusion", Pixels(prepared.OcclusionImage.Png));
            Assert.Equal(0.92f, Channel(material, "MetallicRoughness").GetFactor("RoughnessFactor"), 6);
            Assert.Equal(0.21f, Channel(material, "SpecularFactor").GetFactor("SpecularFactor"), 6);
            Assert.Equal(0.35f, Channel(material, "Occlusion").GetFactor("OcclusionStrength"));
            foreach (var channel in new[] { "BaseColor", "Normal", "MetallicRoughness", "SpecularFactor", "Occlusion" })
            {
                var sampler = Channel(material, channel).TextureSampler!;
                Assert.Equal(TextureWrapMode.CLAMP_TO_EDGE, sampler.WrapS);
                Assert.Equal(TextureWrapMode.REPEAT, sampler.WrapT);
                Assert.Equal(TextureMipMapFilter.LINEAR, sampler.MinFilter);
                Assert.Equal(TextureInterpolationFilter.LINEAR, sampler.MagFilter);
            }
        }
    }

    /// <summary>The profile's cache identity must not manufacture a specular channel when no image was emitted.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingNormal_KeepsTheLegacyLitDefaultsWithoutFabricatedSpecular(bool requestMissingNormal)
    {
        using var resolver = new NifTextureResolver(_ => null);
        var part = Surface(normal: requestMissingNormal ? "missing_n.dds" : null);
        var prepared = Prepare(part, resolver);
        Assert.Equal(0.3f, prepared.Key.SpecularFactor);
        Assert.Equal(1f, prepared.SpecularFactor);
        Assert.Null(prepared.NormalImage);
        Assert.Null(prepared.SpecularImage);
        foreach (var model in new[] { Native(Scene(part), resolver), Shared(Adapt(Scene(part), resolver)) })
        {
            var material = Assert.Single(model.LogicalMaterials);
            Assert.False(material.Unlit);
            Assert.Equal(0.92f, Channel(material, "MetallicRoughness").GetFactor("RoughnessFactor"), 6);
            Assert.Null(material.FindChannel("SpecularFactor"));
        }
    }

    /// <summary>Swapped UV axes generate a source Y tangent with negative handedness, then rotate to negative glTF Z.</summary>
    [Fact]
    public void GeneratedTangents_PreserveTheConvertedDirectionAndHandedness()
    {
        using var resolver = new NifTextureResolver(_ => Texture([128, 128, 255, 255]));
        var part = Surface(normal: "surface_n.dds", uvs: [0, 0, 0, 1, 1, 0]);
        var document = Adapt(Scene(part), resolver);
        var tangents = document.Meshes[0].Primitives[0].Tangents!.Values;
        Assert.Equal(3, tangents.Count);
        Assert.All(tangents, AssertRotatedTangent);
        foreach (var model in new[] { Native(Scene(part), resolver), Shared(document) })
        {
            var primitive = Assert.Single(Assert.Single(model.LogicalMeshes).Primitives);
            var encoded = primitive.GetVertexAccessor("TANGENT").AsVector4Array();
            Assert.Equal(3, encoded.Count);
            Assert.All(encoded, AssertRotatedTangent);
        }
    }

    /// <summary>A baked tint replaces vertex modulation; copying raw green/alpha bytes would change the rendered surface.</summary>
    [Fact]
    public void TintedSurface_UsesTheLegacyVertexColorProjection()
    {
        using var resolver = new NifTextureResolver(_ => Texture([100, 120, 140, 255]));
        byte[] sourceColors = [0, 255, 0, 17, 0, 255, 0, 17, 0, 255, 0, 17];
        var part = Surface("surface.dds", colors: sourceColors, useVertexColors: true);
        part.TintColor = (0.25f, 0.125f, 0.5f);
        var document = Adapt(Scene(part), resolver);
        Assert.All(document.Meshes[0].Primitives[0].Vertices, vertex => Assert.Equal(Vector4.One, vertex.Color));
        Assert.Equal(new byte[] { 0, 255, 0, 17, 0, 255, 0, 17, 0, 255, 0, 17 }, sourceColors);
        foreach (var model in new[] { Native(Scene(part), resolver), Shared(document) })
        {
            AssertChannel(Assert.Single(model.LogicalMaterials), "BaseColor", [50, 30, 140, 255]);
            var primitive = Assert.Single(Assert.Single(model.LogicalMeshes).Primitives);
            Assert.All(primitive.GetVertexAccessor("COLOR_0").AsVector4Array(), color => Assert.Equal(Vector4.One, color));
        }
    }

    /// <summary>Different baked tints must not collide in the inherited path-based cache; identical tints still reuse it.</summary>
    /// <param name="neutral">Whether to inspect the neutral exporter instead of the native writer.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedDiffusePath_DifferentTintsKeepIndependentPixelsAndSameTintReuse(bool neutral)
    {
        byte[] sourcePixels = [100, 120, 140, 255, 200, 80, 40, 255];
        var texture = Texture(sourcePixels);
        using var resolver = new NifTextureResolver(_ => texture);
        var first = Surface("surface.dds", name: "first-tint");
        first.TintColor = (0.25f, 0.125f, 0.5f);
        var second = Surface("surface.dds", name: "second-tint");
        second.TintColor = (0.5f, 0.25f, 0.125f);
        var repeated = Surface("surface.dds", name: "repeated-tint");
        repeated.TintColor = first.TintColor;
        var scene = Scene(first, second, repeated);

        var model = neutral ? Shared(Adapt(scene, resolver)) : Native(scene, resolver);

        Assert.Equal(new byte[] { 100, 120, 140, 255, 200, 80, 40, 255 }, sourcePixels);
        Assert.Equal(new byte[] { 100, 120, 140, 255, 200, 80, 40, 255 }, texture.Pixels);
        var firstMaterial = MaterialFor("first-tint");
        var secondMaterial = MaterialFor("second-tint");
        Assert.Same(firstMaterial, MaterialFor("repeated-tint"));
        AssertChannel(firstMaterial, "BaseColor", [50, 30, 140, 255, 100, 20, 40, 255]);
        AssertChannel(secondMaterial, "BaseColor", [100, 60, 35, 255, 200, 40, 10, 255]);
        Assert.NotSame(firstMaterial, secondMaterial);
        Assert.Equal(2, model.LogicalMaterials.Count);

        Material MaterialFor(string name)
        {
            var node = Assert.Single(model.LogicalNodes, candidate => candidate.Name == name && candidate.Mesh is not null);
            return Assert.IsType<Material>(Assert.Single(node.Mesh!.Primitives).Material);
        }
    }

    /// <summary>An identical source key reuses the first result, including its label; addressing separates keys.</summary>
    [Fact]
    public void Cache_PreservesReuseAndAddressingIdentity()
    {
        using var resolver = new NifTextureResolver(_ => Texture([40, 80, 120, 255]));
        var cache = new Dictionary<NifMaterialCacheKey, NifPreparedMaterial>();
        var first = Surface("surface.dds", name: "first");
        var second = Surface("surface.dds", name: "second");
        var prepared = NifMaterialPreparation.Prepare(first, resolver, cache, default);
        Assert.Same(prepared, NifMaterialPreparation.Prepare(second, resolver, cache, default));
        Assert.Equal("first", prepared.Name);
        second.ClampTextureV = true;
        Assert.NotSame(prepared, NifMaterialPreparation.Prepare(second, resolver, cache, default));
        Assert.Equal(2, cache.Count);

        second.ClampTextureV = false;
        var document = Adapt(Scene(first, second), resolver);
        Assert.Single(document.Materials);
        Assert.Single(document.Images);
    }

    /// <summary>Two leaf names in different source directories cannot make the adapter publish the wrong pixels.</summary>
    [Fact]
    public void EqualImageLabels_DoNotAliasDifferentSourcePaths()
    {
        using var resolver = new NifTextureResolver(path => path.StartsWith(@"textures\a\", StringComparison.Ordinal)
            ? Texture([255, 0, 0, 255]) : Texture([0, 0, 255, 255]));
        var scene = Scene(Surface("a/surface.dds"), Surface("b/surface.dds"));
        var document = Adapt(scene, resolver);
        Assert.Equal(2, document.Images.Count);
        Assert.Equal(document.Images[0].Name, document.Images[1].Name);
        foreach (var model in new[] { Native(scene, resolver), Shared(document) })
        {
            Assert.Equal(2, model.LogicalMaterials.Count);
            AssertChannel(model.LogicalMaterials[0], "BaseColor", [255, 0, 0, 255]);
            AssertChannel(model.LogicalMaterials[1], "BaseColor", [0, 0, 255, 255]);
        }
    }

    /// <summary>The real database fixture reaches a drawable surface and shares its packed ORM payload across channels.</summary>
    [Fact]
    public void StaticStarfieldOrm_ReachesBothWritersAsOneImage()
    {
        using var resolver = OrmResolver(includeRoughness: true);
        var part = Surface("materials/test/orm.mat");
        var prepared = Prepare(part, resolver);
        Assert.True(prepared.Key.HasStaticStarfieldOrmPolicy);
        Assert.Same(prepared.MetallicRoughnessImage, prepared.OcclusionImage);
        Assert.Equal(new byte[] { 55, 11, 64, 255, 66, 22, 64, 255 }, Pixels(prepared.OcclusionImage!.Png));
        Assert.Equal(1f, prepared.MetallicFactor);
        Assert.Equal(1f, prepared.RoughnessFactor);
        var document = Adapt(Scene(part), resolver);
        Assert.Equal(document.Materials[0].MetallicRoughnessTexture!.Value.ImageIndex,
            document.Materials[0].OcclusionTexture!.Value.ImageIndex);
        foreach (var model in new[] { Native(Scene(part), resolver), Shared(document) })
        {
            var material = Assert.Single(model.LogicalMaterials);
            Assert.Same(Channel(material, "MetallicRoughness").Texture!.PrimaryImage,
                Channel(material, "Occlusion").Texture!.PrimaryImage);
            AssertChannel(material, "Occlusion", [55, 11, 64, 255, 66, 22, 64, 255]);
        }
    }

    /// <summary>A missing CE2 ORM image stays on the neutral constructor lane even when legacy gloss and height exist.</summary>
    [Fact]
    public void MissingStarfieldOrm_DoesNotFallThroughToLegacyPacking()
    {
        using var resolver = OrmResolver(includeRoughness: false);
        var part = Surface("materials/test/orm.mat", "textures/test/normal.dds", new NifShaderTextureMetadata
        {
            TextureSlots = [null, null, null, "textures/test/height.dds"]
        });
        foreach (var model in new[] { Native(Scene(part), resolver), Shared(Adapt(Scene(part), resolver)) })
        {
            Assert.Single(model.LogicalMeshes);
            var material = Assert.Single(model.LogicalMaterials);
            var orm = Channel(material, "MetallicRoughness");
            Assert.Null(orm.Texture);
            Assert.Equal(0f, orm.GetFactor("MetallicFactor"));
            Assert.Equal(0f, orm.GetFactor("RoughnessFactor"));
            Assert.Null(Channel(material, "Occlusion").Texture);
            Assert.Null(material.FindChannel("SpecularFactor"));
            Assert.NotNull(Channel(material, "Normal").Texture);
        }
    }

    /// <summary>Constant HDR emission retains its exact factor and separate strength through both encoded outputs.</summary>
    [Fact]
    public void ConstantEmission_ReachesBothWritersWithExactHdrStrength()
    {
        using var resolver = new NifTextureResolver(_ => null);
        var part = Surface();
        part.BgsmEmissionColor = new Vector3(2f, 1f, 4f);
        var prepared = Prepare(part, resolver);
        Assert.True(prepared.HasEmission);
        Assert.Equal(new Vector3(0.5f, 0.25f, 1f), prepared.EmissiveFactor);
        Assert.Equal(4f, prepared.EmissiveStrength);
        var source = Scene(part);
        var original = new NifCorpusSourceSnapshot(source);
        var document = Adapt(source, resolver);
        original.AssertUnchanged(source);
        var neutral = Assert.Single(document.Materials);
        Assert.Equal(new Vector3(0.5f, 0.25f, 1f), neutral.EmissiveFactor);
        Assert.Equal(4f, neutral.EmissiveStrength);
        Assert.Null(neutral.EmissiveTexture);
        Assert.Empty(document.Images);
        Assert.Empty(document.Samplers);
        foreach (var model in new[] { Native(source, resolver), Shared(document) })
        {
            Assert.Contains("KHR_materials_emissive_strength", model.ExtensionsUsed);
            var material = Assert.Single(model.LogicalMaterials);
            AssertEmission(material, new Vector3(0.5f, 0.25f, 1f), 4f);
            Assert.Null(Channel(material, "Emissive").Texture);
        }
    }

    /// <summary>An emission-only image owns a valid sampler and preserves both addressing axes and source pixels.</summary>
    /// <param name="clampU">Whether horizontal sampling clamps instead of repeating.</param>
    /// <param name="clampV">Whether vertical sampling clamps instead of repeating.</param>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void InlineGlowOnly_RetainsPixelsAndIndependentSamplerAddressing(bool clampU, bool clampV)
    {
        var texture = Texture([11, 22, 33, 74, 41, 52, 63, 198]);
        var originalPixels = texture.Pixels.ToArray();
        using var resolver = new NifTextureResolver(_ => texture);
        var part = Surface(metadata: new NifShaderTextureMetadata
        {
            TextureSlots = [null, null, "glow.dds"]
        });
        part.ClampTextureU = clampU;
        part.ClampTextureV = clampV;
        var prepared = Prepare(part, resolver);
        Assert.True(prepared.HasEmission);
        Assert.Equal(originalPixels, Pixels(prepared.EmissiveImage!.Png));
        var source = Scene(part);
        var original = new NifCorpusSourceSnapshot(source);
        var document = Adapt(source, resolver);
        original.AssertUnchanged(source);
        var neutral = Assert.Single(document.Materials);
        Assert.Null(neutral.Texture);
        Assert.Null(neutral.NormalTexture);
        Assert.NotNull(neutral.EmissiveTexture);
        Assert.Equal(0, neutral.EmissiveTexture.Value.SamplerIndex);
        Assert.Single(document.Images);
        var sampler = Assert.Single(document.Samplers);
        Assert.Equal(clampU ? SceneTextureWrap.ClampToEdge : SceneTextureWrap.Repeat, sampler.WrapU);
        Assert.Equal(clampV ? SceneTextureWrap.ClampToEdge : SceneTextureWrap.Repeat, sampler.WrapV);
        foreach (var model in new[] { Native(source, resolver), Shared(document) })
        {
            Assert.DoesNotContain("KHR_materials_emissive_strength", model.ExtensionsUsed);
            var material = Assert.Single(model.LogicalMaterials);
            AssertEmission(material, Vector3.One, 1f);
            AssertChannel(material, "Emissive", [11, 22, 33, 74, 41, 52, 63, 198]);
            var encodedSampler = Channel(material, "Emissive").TextureSampler!;
            Assert.Equal(clampU ? TextureWrapMode.CLAMP_TO_EDGE : TextureWrapMode.REPEAT, encodedSampler.WrapS);
            Assert.Equal(clampV ? TextureWrapMode.CLAMP_TO_EDGE : TextureWrapMode.REPEAT, encodedSampler.WrapT);
            Assert.Equal(TextureMipMapFilter.LINEAR, encodedSampler.MinFilter);
            Assert.Equal(TextureInterpolationFilter.LINEAR, encodedSampler.MagFilter);
        }
        Assert.Equal(originalPixels, texture.Pixels);
    }

    /// <summary>BGSM HDR emission keeps its selected RGB/strength even when its glow image uses the constant fallback.</summary>
    /// <param name="resolveGlow">Whether the authored glow image is available to the resolver.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BgsmHdrEmission_PreservesTextureOrMissingImageFallback(bool resolveGlow)
    {
        var texture = Texture([23, 54, 87, 90, 122, 153, 186, 219]);
        var originalPixels = texture.Pixels.ToArray();
        using var resolver = new NifTextureResolver(_ => resolveGlow ? texture : null);
        var part = Surface();
        part.BgsmEmissionColor = new Vector3(2f, 4f, 8f);
        part.BgsmGlowMapTexturePath = "glow.dds";
        var source = Scene(part);
        var original = new NifCorpusSourceSnapshot(source);
        var document = Adapt(source, resolver);
        original.AssertUnchanged(source);
        var neutral = Assert.Single(document.Materials);
        Assert.Equal(new Vector3(0.25f, 0.5f, 1f), neutral.EmissiveFactor);
        Assert.Equal(8f, neutral.EmissiveStrength);
        Assert.Equal(resolveGlow, neutral.EmissiveTexture is not null);
        Assert.Equal(resolveGlow ? 1 : 0, document.Images.Count);
        Assert.Equal(resolveGlow ? 1 : 0, document.Samplers.Count);
        foreach (var model in new[] { Native(source, resolver), Shared(document) })
        {
            Assert.Contains("KHR_materials_emissive_strength", model.ExtensionsUsed);
            var material = Assert.Single(model.LogicalMaterials);
            AssertEmission(material, new Vector3(0.25f, 0.5f, 1f), 8f);
            if (resolveGlow)
                AssertChannel(material, "Emissive", [23, 54, 87, 90, 122, 153, 186, 219]);
            else
                Assert.Null(Channel(material, "Emissive").Texture);
        }
        Assert.Equal(originalPixels, texture.Pixels);
    }

    /// <summary>Equal normalized RGB does not alias distinct HDR strengths; exactly repeated surfaces still share one material.</summary>
    [Fact]
    public void EmissionCache_DistinguishesStrengthAndReusesIdenticalMaterials()
    {
        using var resolver = new NifTextureResolver(_ => Texture([17, 34, 51, 255]));
        var first = Surface(name: "first");
        var brighter = Surface(name: "brighter");
        var repeated = Surface(name: "repeated");
        foreach (var part in new[] { first, brighter, repeated }) part.BgsmGlowMapTexturePath = "glow.dds";
        first.BgsmEmissionColor = repeated.BgsmEmissionColor = new Vector3(2f, 1f, 0f);
        brighter.BgsmEmissionColor = new Vector3(4f, 2f, 0f);
        var cache = new Dictionary<NifMaterialCacheKey, NifPreparedMaterial>();
        var firstPrepared = NifMaterialPreparation.Prepare(first, resolver, cache, default);
        var brighterPrepared = NifMaterialPreparation.Prepare(brighter, resolver, cache, default);
        var repeatedPrepared = NifMaterialPreparation.Prepare(repeated, resolver, cache, default);
        Assert.Same(firstPrepared, repeatedPrepared);
        Assert.NotSame(firstPrepared, brighterPrepared);
        Assert.Equal(2, cache.Count);
        var source = Scene(first, brighter, repeated);
        var original = new NifCorpusSourceSnapshot(source);
        var document = Adapt(source, resolver);
        original.AssertUnchanged(source);
        Assert.Equal(2, document.Materials.Count);
        Assert.Equal(0, document.Meshes[0].Primitives[0].MaterialIndex);
        Assert.Equal(1, document.Meshes[1].Primitives[0].MaterialIndex);
        Assert.Equal(0, document.Meshes[2].Primitives[0].MaterialIndex);
        foreach (var model in new[] { Native(source, resolver), Shared(document) })
        {
            Assert.Contains("KHR_materials_emissive_strength", model.ExtensionsUsed);
            var materials = model.LogicalMaterials.OrderBy(material => Channel(material, "Emissive").GetFactor("EmissiveStrength")).ToArray();
            Assert.Equal(2, materials.Length);
            AssertEmission(materials[0], new Vector3(1f, 0.5f, 0f), 2f);
            AssertEmission(materials[1], new Vector3(1f, 0.5f, 0f), 4f);
        }
    }

    /// <summary>Invalid authored emission remains a whole-scene decline rather than disappearing during preparation.</summary>
    /// <param name="component">The malformed red component, with finite nonnegative green and blue controls.</param>
    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(-0.25f)]
    public void MalformedBgsmEmission_RetainsNativeWriter(float component)
    {
        using var resolver = new NifTextureResolver(_ => null);
        var part = Surface();
        part.BgsmEmissionColor = new Vector3(component, 0.5f, 1f);
        Assert.False(NifNeutralSceneAdapter.TryAdapt(Scene(Surface(name: "ordinary"), part), resolver,
            "fixture", out var document, out var reason, TestContext.Current.CancellationToken));
        Assert.Null(document);
        Assert.Equal("Malformed BGSM emission retains the native material writer.", reason);
    }

    /// <summary>An unlit source with BGSM lit-emission state is still declined before preparation suppresses it.</summary>
    /// <param name="constant">Whether the unsupported state is a constant HDR term or a glow map.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BgsmEmissionOnUnlitSurface_RetainsNativeWriter(bool constant)
    {
        using var resolver = new NifTextureResolver(_ => null);
        var part = Surface();
        part.IsEmissive = true;
        if (constant) part.BgsmEmissionColor = new Vector3(2f, 1f, 4f);
        else part.BgsmGlowMapTexturePath = "glow.dds";
        var source = Scene(Surface(name: "ordinary"), part);
        var original = new NifCorpusSourceSnapshot(source);
        Assert.False(NifNeutralSceneAdapter.TryAdapt(source, resolver, "fixture", out var document,
            out var reason, TestContext.Current.CancellationToken));
        Assert.Null(document);
        Assert.Equal("BGSM emission on an unlit surface retains the native material writer.", reason);
        original.AssertUnchanged(source);
    }

    /// <summary>A prepared static-looking emission term cannot admit source state driven by external emittance.</summary>
    [Fact]
    public void ExternalEmittanceWithHdrEmission_RetainsNativeWriter()
    {
        using var resolver = new NifTextureResolver(_ => Texture([11, 22, 33, 255]));
        var part = Surface();
        part.BgsmGlowMapTexturePath = "glow.dds";
        part.BgsmEmissionColor = new Vector3(2f, 1f, 4f);
        part.UsesExternalEmittance = true;
        Assert.True(Prepare(part, resolver).HasEmission);
        var source = Scene(Surface(name: "ordinary"), part);
        var original = new NifCorpusSourceSnapshot(source);
        Assert.False(NifNeutralSceneAdapter.TryAdapt(source, resolver, "fixture", out var document,
            out var reason, TestContext.Current.CancellationToken));
        Assert.Null(document);
        Assert.Equal("Emissive and external-emittance state retains the native material writer.", reason);
        original.AssertUnchanged(source);
    }

    /// <summary>A material policy that forces unlit presentation cannot silently suppress its prepared emission.</summary>
    [Fact]
    public void AuthoredSkyWithPreparedEmission_RetainsNativeWriter()
    {
        using var resolver = new NifTextureResolver(_ => null);
        var part = Surface(colors: [255, 128, 64, 255, 255, 128, 64, 255, 255, 128, 64, 255]);
        part.SkyType = SkyObjectType.Sky;
        part.BgsmEmissionColor = new Vector3(2f, 1f, 4f);
        Assert.False(part.IsEmissive);
        var prepared = Prepare(part, resolver);
        Assert.True(prepared.Unlit);
        Assert.True(prepared.HasEmission);
        var source = Scene(Surface(name: "ordinary"), part);
        var original = new NifCorpusSourceSnapshot(source);
        Assert.False(NifNeutralSceneAdapter.TryAdapt(source, resolver, "fixture", out var document,
            out var reason, TestContext.Current.CancellationToken));
        Assert.Null(document);
        Assert.Equal("Emission on a prepared unlit surface retains the native material writer.", reason);
        original.AssertUnchanged(source);
    }

    /// <summary>An inactive regular BGSM must still suppress a stale inline glow slot.</summary>
    [Fact]
    public void ExternalRegularBgsm_DoesNotResurrectInlineGlow()
    {
        using var resolver = new NifTextureResolver(_ => Texture([11, 22, 33, 255]));
        var part = Surface(metadata: new NifShaderTextureMetadata
        {
            MaterialPath = "materials/inactive.bgsm",
            TextureSlots = [null, null, "stale_glow.dds"]
        });
        var prepared = Prepare(part, resolver);
        Assert.False(prepared.HasEmission);
        Assert.Null(prepared.EmissiveImage);
        foreach (var model in new[] { Native(Scene(part), resolver), Shared(Adapt(Scene(part), resolver)) })
        {
            var emissive = Channel(Assert.Single(model.LogicalMaterials), "Emissive");
            Assert.True(emissive.HasDefaultContent);
            Assert.Null(emissive.Texture);
        }
    }

    /// <summary>A source unlit route does not gain lighting channels just because its normal path resolves.</summary>
    [Fact]
    public void UnlitSurface_RetainsItsExistingChannelSelection()
    {
        using var resolver = new NifTextureResolver(_ => Texture([11, 22, 33, 255]));
        var part = Surface("surface.dds", "surface_n.dds", new NifShaderTextureMetadata
        {
            TextureSlots = [null, null, "inline_glow.dds"]
        });
        part.IsEmissive = true;
        var prepared = Prepare(part, resolver);
        Assert.True(prepared.Unlit);
        Assert.Null(prepared.NormalImage);
        Assert.Null(prepared.MetallicRoughnessImage);
        Assert.False(prepared.HasEmission);
        foreach (var model in new[] { Native(Scene(part), resolver), Shared(Adapt(Scene(part), resolver)) })
        {
            var material = Assert.Single(model.LogicalMaterials);
            Assert.True(material.Unlit);
            AssertChannel(material, "BaseColor", [11, 22, 33, 255]);
        }
    }

    /// <summary>The water sentinel preserves physical channels and its approximation marker in the native writer.</summary>
    [Fact]
    public void WaterOptics_ArePreservedAndRemainAnExplicitDecline()
    {
        using var resolver = new NifTextureResolver(_ => null);
        var part = Surface(RenderableSubmesh.WaterSurfaceTexturePath);
        var prepared = Prepare(part, resolver);
        Assert.Equal(StarfieldWaterMaterialRoute.MeshViewerIndexOfRefraction, prepared.IndexOfRefraction);
        Assert.Equal(StarfieldWaterMaterialRoute.MeshViewerTransmission, prepared.Transmission);
        Assert.Equal(StarfieldWaterMaterialRoute.MeshViewerClearCoat, prepared.ClearCoat);
        Assert.Equal(StarfieldWaterMaterialRoute.MeshViewerClearCoatRoughness, prepared.ClearCoatRoughness);
        Assert.Equal(SceneAlphaMode.Opaque, prepared.AlphaMode);
        var material = Assert.Single(Native(Scene(part), resolver).LogicalMaterials);
        Assert.Equal(StarfieldWaterMaterialRoute.MeshViewerTransmission,
            Channel(material, "Transmission").GetFactor("TransmissionFactor"));
        Assert.True(material.Extras![StarfieldWaterMaterialRoute.MeshViewerMaterialExtrasKey]!.GetValue<bool>());
        Assert.False(NifNeutralSceneAdapter.TryAdapt(Scene(part), resolver, "fixture", out var document,
            out var reason, TestContext.Current.CancellationToken));
        Assert.Null(document);
        Assert.Contains("Water optical", reason);
    }

    /// <summary>Resolver failures remain errors rather than becoming an unsupported-capability result.</summary>
    [Fact]
    public void ResolverFailure_IsNotSwallowedByEitherConsumer()
    {
        using var resolver = new NifTextureResolver(_ => throw new InvalidDataException("malformed texture fixture"));
        var part = Surface("bad.dds");
        Assert.Equal("malformed texture fixture", Assert.Throws<InvalidDataException>(() =>
            Native(Scene(part), resolver)).Message);
        Assert.Equal("malformed texture fixture", Assert.Throws<InvalidDataException>(() =>
            Adapt(Scene(part), resolver)).Message);
    }

    /// <summary>A carried static opacity threshold remains in the floating-point domain through preparation and native encoding.</summary>
    [Fact]
    public void StarfieldOpacity_UsesTheNextFloatWithoutByteQuantization()
    {
        const string material = "materials/test/cutout.mat";
        const float threshold = 1f / 3f;
        var request = MaterialTexturePathResolver.BuildStarfieldOpacityMapRequest(material);
        using var resolver = new NifTextureResolver(path => path == request
            ? Texture([7, 99, 98, 97, 211, 96, 95, 94])
            : Texture([10, 20, 30, 40, 50, 60, 70, 80]));
        var part = Surface(material, alpha: new StarfieldMaterialAlphaRenderState(
            StarfieldMaterialAlphaRenderMode.Layer0OpacityCutout, threshold));
        var prepared = Prepare(part, resolver);
        Assert.Equal(SceneAlphaMode.Mask, prepared.AlphaMode);
        Assert.Equal(MathF.BitIncrement(threshold), prepared.AlphaCutoff);
        Assert.Equal(new byte[] { 10, 20, 30, 7, 50, 60, 70, 211 }, Pixels(prepared.BaseColorImage!.Png));
        var encoded = Assert.Single(Native(Scene(part), resolver).LogicalMaterials);
        Assert.Equal(AlphaMode.MASK, encoded.Alpha);
        Assert.Equal(MathF.BitIncrement(threshold), encoded.AlphaCutoff);
    }

    /// <summary>Creates a drawable material database source with independent channel values.</summary>
    private static NifTextureResolver OrmResolver(bool includeRoughness)
    {
        var textures = new Dictionary<string, DecodedTexture>(StringComparer.OrdinalIgnoreCase)
        {
            [@"textures\test\surface.dds"] = Texture([10, 20, 30, 255, 40, 50, 60, 255]),
            [@"textures\test\surface_ao.dds"] = Texture([55, 0, 0, 0, 66, 0, 0, 0]),
            [@"textures\test\normal.dds"] = Texture([128, 128, 255, 0, 128, 128, 255, 255]),
            [@"textures\test\height.dds"] = Texture([99, 99, 99, 255, 100, 100, 100, 255])
        };
        if (includeRoughness)
            textures[@"textures\test\surface_rough.dds"] = Texture([11, 0, 0, 0, 22, 0, 0, 0]);
        return new NifTextureResolver(new INifTextureSource[]
        {
            new NifMaterialFixtureSource(StarfieldMaterialOrmPolicyTests.BuildDatabase(false,
                baseColorTexturePath: @"Data\Textures\Test\surface.dds"), textures)
        });
    }

    /// <summary>Prepares one material in an export-local cache.</summary>
    private static NifPreparedMaterial Prepare(RenderableSubmesh part, NifTextureResolver resolver) =>
        NifMaterialPreparation.Prepare(part, resolver, [], StarfieldGlbVertexLerpProjection.Resolve(part));

    /// <summary>Creates geometry with a defined normal and UV basis and optional material inputs.</summary>
    private static RenderableSubmesh Surface(string? diffuse = null, string? normal = null,
        NifShaderTextureMetadata? metadata = null, StarfieldMaterialAlphaRenderState alpha = default,
        string name = "surface", float[]? uvs = null, byte[]? colors = null, bool useVertexColors = false) => new()
    {
        ShapeName = name,
        Positions = [0, 0, 0, 1, 0, 0, 0, 1, 0],
        Triangles = [0, 1, 2],
        Normals = [0, 0, 1, 0, 0, 1, 0, 0, 1],
        UVs = uvs ?? [0, 0, 1, 0, 0, 1],
        VertexColors = colors,
        UseVertexColors = useVertexColors,
        DiffuseTexturePath = diffuse,
        NormalMapTexturePath = normal,
        ShaderMetadata = metadata,
        StarfieldMaterialAlpha = alpha
    };

    /// <summary>
    ///     A part whose material differs only by a glow map re-prepares the diffuse texture it shares with another part.
    ///     The two byte-identical prepared images are stored once, as the native writer's glTF library stores them, and
    ///     the glow's different bytes stay a separate image. Before this, the retail neon sign's shared GLB carried
    ///     2.2 MB of second copies.
    /// </summary>
    [Fact]
    public void ByteIdenticalPreparedImages_AreStoredOnceAndDifferentBytesStaySeparate()
    {
        var diffuse = Texture([200, 100, 50, 255, 10, 20, 30, 255]);
        var glow = Texture([1, 2, 3, 255, 250, 240, 230, 255]);
        using var resolver = new NifTextureResolver(path =>
            path.Contains("glow", StringComparison.OrdinalIgnoreCase) ? glow : diffuse);
        var plain = Surface("shared.dds", name: "plain");
        var glowing = Surface("shared.dds", name: "glowing", metadata: new NifShaderTextureMetadata
        {
            TextureSlots = [null, null, "glow.dds"]
        });
        var source = Scene(plain, glowing);

        var document = Adapt(source, resolver);

        Assert.Equal(2, document.Materials.Count);
        var plainBase = Assert.NotNull(document.Materials[0].Texture);
        var glowingBase = Assert.NotNull(document.Materials[1].Texture);
        var emissive = Assert.NotNull(document.Materials[1].EmissiveTexture);
        Assert.Equal(plainBase.ImageIndex, glowingBase.ImageIndex);
        Assert.NotEqual(glowingBase.ImageIndex, emissive.ImageIndex);
        Assert.Equal(2, document.Images.Count);
        Assert.Equal([200, 100, 50, 255, 10, 20, 30, 255], Pixels(document.Images[plainBase.ImageIndex].CopyContent()));
        Assert.Equal([1, 2, 3, 255, 250, 240, 230, 255], Pixels(document.Images[emissive.ImageIndex].CopyContent()));
        Assert.Equal(Native(source, resolver).LogicalImages.Count, Shared(document).LogicalImages.Count);
    }

    /// <summary>
    ///     A NIF vertex color is already 8-bit when read, so the shared writer stores normalized unsigned bytes exactly as
    ///     the native writer does, not floats holding the same values in four times the space.
    /// </summary>
    [Fact]
    public void VertexColors_AreNormalizedBytesWithTheNativeValues()
    {
        using var resolver = new NifTextureResolver(_ => null);
        var source = Scene(Surface(colors: [255, 0, 0, 255, 0, 128, 255, 64, 17, 34, 51, 0], useVertexColors: true));

        var document = Adapt(source, resolver);

        Assert.Equal(SceneColorEncoding.UnsignedByteNormalized, document.Meshes[0].Primitives[0].ColorEncoding);
        var native = Native(source, resolver).LogicalMeshes[0].Primitives[0].GetVertexAccessor("COLOR_0");
        var shared = Shared(document).LogicalMeshes[0].Primitives[0].GetVertexAccessor("COLOR_0");
        Assert.Equal(EncodingType.UNSIGNED_BYTE, shared.Encoding);
        Assert.True(shared.Normalized);
        Vector4[] expected =
        [
            new(1f, 0f, 0f, 1f),
            new(0f, 128 / 255f, 1f, 64 / 255f),
            new(17 / 255f, 34 / 255f, 51 / 255f, 0f)
        ];
        Assert.Equal(expected, shared.AsVector4Array().ToArray());
        Assert.Equal(expected, native.AsVector4Array().ToArray());
    }

    /// <summary>
    ///     On the classic export route each rigid part has its own attachment node, named with a block suffix. The shared
    ///     mesh takes the part's name, as the native writer names its mesh, while a node grouping several parts keeps its
    ///     own name.
    /// </summary>
    [Fact]
    public void RigidMeshes_TakeThePartNameAsTheNativeWriterDoes()
    {
        using var resolver = new NifTextureResolver(_ => null);
        var scene = new GlbScene();
        var single = scene.AddNode("Shape05:0_61", GlbScene.RootNodeIndex, Matrix4x4.Identity, Matrix4x4.Identity,
            GlbNodeKind.Attachment, "Shape05:0_61");
        scene.MeshParts.Add(new GlbMeshPart { Name = "Shape05:0", Submesh = Surface(name: "Shape05:0"), NodeIndex = single });
        var grouped = scene.AddNode("Group_7", GlbScene.RootNodeIndex, Matrix4x4.Identity, Matrix4x4.Identity,
            GlbNodeKind.Attachment, "Group_7");
        scene.MeshParts.Add(new GlbMeshPart { Name = "first", Submesh = Surface(name: "first"), NodeIndex = grouped });
        scene.MeshParts.Add(new GlbMeshPart { Name = "second", Submesh = Surface(name: "second"), NodeIndex = grouped });

        var document = Adapt(scene, resolver);

        Assert.Equal(["Shape05:0", "Group_7"], document.Meshes.Select(static mesh => mesh.Name));
        Assert.Contains("Shape05:0", Native(scene, resolver).LogicalMeshes.Select(static mesh => mesh.Name));
        Assert.Contains("Shape05:0", Shared(document).LogicalMeshes.Select(static mesh => mesh.Name));
    }

    /// <summary>Gives every fixture surface an explicit owning node so the adapter cannot decline on geometry identity.</summary>
    private static GlbScene Scene(params RenderableSubmesh[] parts)
    {
        var scene = new GlbScene();
        foreach (var part in parts)
        {
            var node = scene.AddNode(part.ShapeName!, GlbScene.RootNodeIndex, Matrix4x4.Identity,
                Matrix4x4.Identity, GlbNodeKind.Attachment, part.ShapeName!);
            scene.MeshParts.Add(new GlbMeshPart { Name = part.ShapeName!, Submesh = part, NodeIndex = node });
        }
        return scene;
    }

    /// <summary>Requires successful adaptation; a capability decline fails this fixture.</summary>
    private static ModelDocument Adapt(GlbScene scene, NifTextureResolver resolver)
    {
        Assert.True(NifNeutralSceneAdapter.TryAdapt(scene, resolver, "fixture", out var document,
            out var reason, TestContext.Current.CancellationToken), reason);
        return document!;
    }

    /// <summary>Reads the native writer's encoded artifact to exercise final channel serialization.</summary>
    private static ModelRoot Native(GlbScene scene, NifTextureResolver resolver) =>
        ModelRoot.ParseGLB(GlbWriter.WriteToBytes(scene, resolver));

    /// <summary>Reads the shared writer's encoded artifact to exercise final channel serialization.</summary>
    private static ModelRoot Shared(ModelDocument document) => ModelRoot.ParseGLB(GltfExporter.Encode(
        SceneGltfBuilder.Build(document, GltfExportIntent.Interchange, TestContext.Current.CancellationToken),
        TestContext.Current.CancellationToken));

    /// <summary>Creates a single-row RGBA texture whose expected bytes are written explicitly in each test.</summary>
    private static DecodedTexture Texture(byte[] pixels) => DecodedTexture.FromBaseLevel(pixels, pixels.Length / 4, 1, false);

    /// <summary>Decodes prepared PNG bytes independently of Bethesda's encoder.</summary>
    private static byte[] Pixels(byte[] png)
    {
        using var decoded = new MagickImage(png);
        using var pixelView = decoded.GetPixels();
        return Assert.IsType<byte[]>(pixelView.ToByteArray(PixelMapping.RGBA));
    }

    /// <summary>Checks an independently known rotated tangent while allowing the float rotation's tiny cosine residue.</summary>
    private static void AssertRotatedTangent(Vector4 tangent)
    {
        Assert.Equal(0f, tangent.X, 5);
        Assert.Equal(0f, tangent.Y, 5);
        Assert.Equal(-1f, tangent.Z, 5);
        Assert.Equal(-1f, tangent.W);
    }

    /// <summary>Requires a named output channel to exist.</summary>
    private static MaterialChannel Channel(Material material, string name) => material.FindChannel(name)!.Value;

    /// <summary>Requires the encoded lit emission factor and HDR scalar to equal independent expected values.</summary>
    /// <param name="material">The material parsed from an actual encoded GLB.</param>
    /// <param name="factor">The independently expected bounded linear RGB factor.</param>
    /// <param name="strength">The independently expected HDR multiplier.</param>
    private static void AssertEmission(Material material, Vector3 factor, float strength)
    {
        Assert.False(material.Unlit);
        var emissive = Channel(material, "Emissive");
        Assert.Equal(factor, new Vector3(emissive.Color.X, emissive.Color.Y, emissive.Color.Z));
        Assert.Equal(strength, emissive.GetFactor("EmissiveStrength"));
    }

    /// <summary>Requires the actual image bytes attached to a channel to match independently supplied RGBA.</summary>
    private static void AssertChannel(Material material, string name, byte[] expected) =>
        Assert.Equal(expected, Pixels(Channel(material, name).Texture!.PrimaryImage.Content.Content.ToArray()));
}
