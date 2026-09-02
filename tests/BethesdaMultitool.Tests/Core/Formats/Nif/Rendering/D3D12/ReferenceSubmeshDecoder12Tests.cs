using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Materials;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Particles;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Skinning;
using BethesdaMultitool.Core.Formats.SpeedTree;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.D3D12;

public sealed class ReferenceSubmeshDecoder12Tests
{
    [Fact]
    public void DecodeAppliesTheExistingReferenceMaterialPolicyAndCallSiteOverrides()
    {
        var alphaController = new NifMaterialAlphaController(
            17,
            "Alpha",
            NifKeyInterpolation.Linear,
            [new NifFloatKey(0f, 0.75f)],
            null,
            default,
            default);
        var particleRuntime = new ParticleRuntimeDefinition(
            new ParticleSystemDefinition { BlockIndex = 18, Capacity = 4 },
            Matrix4x4.CreateTranslation(1f, 2f, 3f));
        var skin = new NifSubmeshSkin(
            [0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f],
            [0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f],
            [Matrix4x4.Identity],
            [0],
            [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
            [1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f]);
        var source = new RenderableSubmesh
        {
            ShapeName = "ResolvedShape",
            Positions = [0f, 0f, 0f, 2f, 0f, 0f, 0f, 2f, 0f],
            LocalBounds = new NifLocalBounds(new Vector3(1f, 1f, 0f), 2f),
            Triangles = [0, 1, 2],
            Normals = [0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f],
            UVs = [0f, 0f, 1f, 0f, 0f, 1f],
            VertexColors = [10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120],
            UseVertexColors = true,
            Tangents = [1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f],
            Bitangents = [0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f],
            DiffuseTexturePath = @"textures\source_d.dds",
            NormalMapTexturePath = @"textures\source_n.dds",
            SpecularMapTexturePath = @"textures\source_s.dds",
            GradientMapTexturePath = @"textures\source_g.dds",
            GradientMapV = 0.25f,
            EnvironmentMapTexturePath = @"textures\source_e.dds",
            EnvironmentMapScale = 1.5f,
            EnvironmentMapSmoothness = 0.6f,
            ClassicEnvironmentMapTexturePath = @"textures\classic_e.dds",
            ClassicEnvironmentMaskTexturePath = @"textures\classic_m.dds",
            ClassicEnvironmentMapScale = 0.7f,
            ClassicEnvironmentMapUsesWindowReflection = true,
            ClassicEnvironmentMapIsSphereMap = true,
            ClassicParallaxHeightMapTexturePath = @"textures\height.dds",
            IsDecal = true,
            EffectTint = (0.1f, 0.2f, 0.3f),
            EffectFalloff = (0.4f, 0.5f, 0.6f, 0.7f),
            SoftParticleFalloffDepth = 32f,
            HasAlphaBlend = true,
            HasAlphaTest = true,
            AlphaTestThreshold = 120,
            AlphaTestFunction = 4,
            SrcBlendMode = 6,
            DstBlendMode = 7,
            MaterialAlpha = 0.8f,
            MaterialAlphaController = alphaController,
            IsDoubleSided = true,
            MaterialGlossiness = 24f,
            SpecularColor = (0.8f, 0.7f, 0.6f),
            MaterialDiffuse = (0.5f, 0.4f, 0.3f),
            IsBillboard = true,
            BillboardMode = NifBillboardMode.AlwaysFaceCenter,
            IsLeafBillboard = true,
            IsParticleCloud = true,
            ParticleRuntime = particleRuntime,
            IsSpeedTreeBranch = true,
            SpeedTreeWindSpeeds = new Vector2(2f, 3f),
            SpeedTreeLod = new SpeedTreeLodMetadata(1, 2, 10f, 20f, SpeedTreeLodComponent.Leaf),
            IsLighting30 = true,
            Lighting30GlowMapTexturePath = @"textures\lighting30.dds",
            Lighting30EmissionColor = (0.2f, 0.3f, 0.4f),
            Lighting30EmissionMultiplier = 1.25f,
            UvScrollVelocity = new Vector2(0.01f, -0.02f),
            SourceBlockIndex = 19,
            ClampTextureU = true,
            ClampTextureV = true,
            StarfieldMaterialColor = new StarfieldMaterialColorRenderState(
                StarfieldMaterialColorRenderMode.ConstantLerp,
                new Vector4(0.2f, 0.3f, 0.4f, 0.5f)),
            StarfieldMaterialAlpha = new StarfieldMaterialAlphaRenderState(
                StarfieldMaterialAlphaRenderMode.Layer0OpacityCutout,
                0.42f),
            BgsmGlowMapTexturePath = @"textures\bgsm_glow.dds",
            BgsmEmissionColor = new Vector3(0.8f, 0.6f, 0.4f)
        };

        var decoded = ReferenceSubmeshDecoder12.Decode(
            source,
            new ReferenceSubmeshDecodeOptions12(
                @"textures\override_d.dds",
                @"textures\override_n.dds",
                GradientMapVOverride: 0.9f,
                Skin: skin,
                IncludeParticleRuntime: true));

        Assert.Equal(@"textures\override_d.dds", decoded.DiffuseTexturePath);
        Assert.Equal(@"textures\override_n.dds", decoded.NormalMapTexturePath);
        Assert.True(decoded.HasBump);
        Assert.Equal(NifAlphaRenderMode.Blend, decoded.AlphaRenderMode);
        Assert.True(decoded.AlphaBlend);
        Assert.True(decoded.AlphaTest);
        Assert.Equal(0.42f, decoded.AlphaTestThreshold);
        Assert.True(decoded.DepthWritingBlend);
        Assert.True(decoded.EngineZWriteOff);
        Assert.True(decoded.DoubleSided);
        Assert.Equal(new Vector3(1f, 1f, 0f), decoded.LocalBoundsCenter);
        Assert.Equal(2f, decoded.LocalBoundsRadius);
        Assert.True(decoded.SpecularEnabled);
        Assert.Equal(source.SpecularMapTexturePath, decoded.SpecularMapTexturePath);
        Assert.Equal(0.9f, decoded.GradientMapV);
        Assert.Equal(source.EnvironmentMapTexturePath, decoded.EnvironmentMapTexturePath);
        Assert.Equal(source.EffectFalloff.Value.StartOpacity, decoded.EffectFalloffParams.Z);
        Assert.Equal(source.UvScrollVelocity, decoded.UvScrollVelocity);
        Assert.Same(skin, decoded.Skin);
        Assert.Same(alphaController, decoded.MaterialAlphaController);
        Assert.Same(particleRuntime, decoded.ParticleRuntime);
        Assert.Equal(source.SpeedTreeLod, decoded.SpeedTreeLod);
        Assert.Equal(source.ClassicEnvironmentMapTexturePath, decoded.ClassicEnvironmentMapTexturePath);
        Assert.True(decoded.ClassicEnvironmentMapIsSphereMap);
        Assert.Equal(source.StarfieldMaterialColor, decoded.StarfieldMaterialColor);
        Assert.Equal(source.StarfieldMaterialAlpha, decoded.StarfieldMaterialAlpha);
        Assert.Equal(source.BgsmGlowMapTexturePath, decoded.BgsmGlowMapTexturePath);
        Assert.Equal(source.BgsmEmissionColor, decoded.BgsmEmissionColor);
        Assert.NotNull(decoded.MaterialDiffuse);
        var sourceDiffuse = source.MaterialDiffuse.GetValueOrDefault();
        Assert.Equal(sourceDiffuse.R, decoded.MaterialDiffuse.Value.X);
        Assert.Equal(sourceDiffuse.G, decoded.MaterialDiffuse.Value.Y);
        Assert.Equal(sourceDiffuse.B, decoded.MaterialDiffuse.Value.Z);
        Assert.Equal(source.Positions[3], decoded.Vertices[1].Position.X);
        Assert.Equal(source.VertexColors[0] / 255f, decoded.Vertices[0].VertexColor.X, 6);
    }

    [Fact]
    public void DecodePreservesWaterSentinelAndKeepsLiveParticleGraphExplicitlyGated()
    {
        var runtime = new ParticleRuntimeDefinition(
            new ParticleSystemDefinition { BlockIndex = 1, Capacity = 1 },
            Matrix4x4.Identity);
        var source = new RenderableSubmesh
        {
            Positions = [0f, 0f, 0f],
            Triangles = [],
            DiffuseTexturePath = RenderableSubmesh.WaterSurfaceTexturePath,
            ParticleRuntime = runtime
        };

        var decoded = ReferenceSubmeshDecoder12.Decode(
            source,
            new ReferenceSubmeshDecodeOptions12(
                source.DiffuseTexturePath,
                source.NormalMapTexturePath));

        Assert.Equal(RenderableSubmesh.WaterSurfaceTexturePath, decoded.DiffuseTexturePath);
        Assert.Null(decoded.ParticleRuntime);
    }
}
