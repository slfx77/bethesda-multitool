using System.Numerics;
using System.Reflection;
using BethesdaMultitool.Core.Formats.Nif;
using BethesdaMultitool.Core.Formats.Nif.Materials;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Assembly;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Particles;
using BethesdaMultitool.Core.Formats.SpeedTree;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

public sealed class RenderableSubmeshClonerTests
{
    private static readonly string[] GeometryPropertyNames =
    [
        nameof(RenderableSubmesh.ShapeName),
        nameof(RenderableSubmesh.Positions),
        nameof(RenderableSubmesh.LocalBounds),
        nameof(RenderableSubmesh.Triangles),
        nameof(RenderableSubmesh.Normals),
        nameof(RenderableSubmesh.UVs),
        nameof(RenderableSubmesh.VertexColors),
        nameof(RenderableSubmesh.Tangents),
        nameof(RenderableSubmesh.Bitangents),
        nameof(RenderableSubmesh.BindPosePositions),
        nameof(RenderableSubmesh.SourceBlockIndex)
    ];

    private static readonly string[] GeometryArrayPropertyNames =
    [
        nameof(RenderableSubmesh.Positions),
        nameof(RenderableSubmesh.Triangles),
        nameof(RenderableSubmesh.Normals),
        nameof(RenderableSubmesh.UVs),
        nameof(RenderableSubmesh.VertexColors),
        nameof(RenderableSubmesh.Tangents),
        nameof(RenderableSubmesh.Bitangents),
        nameof(RenderableSubmesh.BindPosePositions)
    ];

    // This list is an intentional maintenance ratchet. Adding a RenderableSubmesh field must force
    // an explicit clone-ownership decision instead of silently resetting it at a viewer/export seam.
    private static readonly string[] ExpectedWritablePropertyNames =
    [
        nameof(RenderableSubmesh.ShapeName),
        nameof(RenderableSubmesh.Positions),
        nameof(RenderableSubmesh.LocalBounds),
        nameof(RenderableSubmesh.Triangles),
        nameof(RenderableSubmesh.Normals),
        nameof(RenderableSubmesh.UVs),
        nameof(RenderableSubmesh.VertexColors),
        nameof(RenderableSubmesh.StarfieldMaterialColor),
        nameof(RenderableSubmesh.StarfieldMaterialAlpha),
        nameof(RenderableSubmesh.Tangents),
        nameof(RenderableSubmesh.Bitangents),
        nameof(RenderableSubmesh.DiffuseTexturePath),
        nameof(RenderableSubmesh.ClampTextureU),
        nameof(RenderableSubmesh.ClampTextureV),
        nameof(RenderableSubmesh.NormalMapTexturePath),
        nameof(RenderableSubmesh.SpecularMapTexturePath),
        nameof(RenderableSubmesh.GradientMapTexturePath),
        nameof(RenderableSubmesh.GradientMapV),
        nameof(RenderableSubmesh.BgsmGlowMapTexturePath),
        nameof(RenderableSubmesh.BgsmEmissionColor),
        nameof(RenderableSubmesh.EnvironmentMapTexturePath),
        nameof(RenderableSubmesh.EnvironmentMapScale),
        nameof(RenderableSubmesh.EnvironmentMapSmoothness),
        nameof(RenderableSubmesh.ClassicEnvironmentMapTexturePath),
        nameof(RenderableSubmesh.ClassicEnvironmentMaskTexturePath),
        nameof(RenderableSubmesh.ClassicEnvironmentMapScale),
        nameof(RenderableSubmesh.ClassicEnvironmentMapUsesWindowReflection),
        nameof(RenderableSubmesh.ClassicEnvironmentMapIsSphereMap),
        nameof(RenderableSubmesh.ClassicParallaxHeightMapTexturePath),
        nameof(RenderableSubmesh.IsDecal),
        nameof(RenderableSubmesh.EffectTint),
        nameof(RenderableSubmesh.EffectFalloff),
        nameof(RenderableSubmesh.SoftParticleFalloffDepth),
        nameof(RenderableSubmesh.ShaderMetadata),
        nameof(RenderableSubmesh.IsEmissive),
        nameof(RenderableSubmesh.UseVertexColors),
        nameof(RenderableSubmesh.UseVertexAlphaForOpacity),
        nameof(RenderableSubmesh.IsDoubleSided),
        nameof(RenderableSubmesh.HasAlphaBlend),
        nameof(RenderableSubmesh.HasAlphaTest),
        nameof(RenderableSubmesh.AlphaTestThreshold),
        nameof(RenderableSubmesh.AlphaTestFunction),
        nameof(RenderableSubmesh.SrcBlendMode),
        nameof(RenderableSubmesh.DstBlendMode),
        nameof(RenderableSubmesh.MaterialAlpha),
        nameof(RenderableSubmesh.MaterialAlphaController),
        nameof(RenderableSubmesh.MaterialGlossiness),
        nameof(RenderableSubmesh.SpecularColor),
        nameof(RenderableSubmesh.MaterialDiffuse),
        nameof(RenderableSubmesh.IsEyeEnvmap),
        nameof(RenderableSubmesh.EnvMapScale),
        nameof(RenderableSubmesh.RenderOrder),
        nameof(RenderableSubmesh.TintColor),
        nameof(RenderableSubmesh.IsFaceGen),
        nameof(RenderableSubmesh.SubsurfaceColor),
        nameof(RenderableSubmesh.AnimatedEmissiveColor),
        nameof(RenderableSubmesh.EmissiveColor),
        nameof(RenderableSubmesh.Lighting30EmissionColor),
        nameof(RenderableSubmesh.IsLighting30),
        nameof(RenderableSubmesh.Lighting30EmissionMultiplier),
        nameof(RenderableSubmesh.Lighting30GlowMapTexturePath),
        nameof(RenderableSubmesh.UvScrollVelocity),
        nameof(RenderableSubmesh.BindPosePositions),
        nameof(RenderableSubmesh.SourceNifPath),
        nameof(RenderableSubmesh.SourceBlockIndex),
        nameof(RenderableSubmesh.SkyType),
        nameof(RenderableSubmesh.IsBillboard),
        nameof(RenderableSubmesh.BillboardMode),
        nameof(RenderableSubmesh.IsLeafBillboard),
        nameof(RenderableSubmesh.IsParticleCloud),
        nameof(RenderableSubmesh.ParticleRuntime),
        nameof(RenderableSubmesh.IsSpeedTreeBranch),
        nameof(RenderableSubmesh.SpeedTreeWindSpeeds),
        nameof(RenderableSubmesh.SpeedTreeLod),
        nameof(RenderableSubmesh.IsFarLodFallback)
    ];

    [Fact]
    public void DeepClonePreservesEveryWritableFieldWithoutAliasingGeometryArrays()
    {
        var source = FilledSubmesh("raw");

        var clone = RenderableSubmeshCloner.DeepClone(source);

        AssertEveryWritablePropertyIsCovered();
        AssertEquivalent(source, clone, deepCloneArrays: true);
        Assert.Same(source.ShaderMetadata, clone.ShaderMetadata);
        Assert.Same(source.MaterialAlphaController, clone.MaterialAlphaController);
        Assert.Same(source.ParticleRuntime, clone.ParticleRuntime);

        clone.Positions[0] = -999f;
        clone.BindPosePositions![0] = -888f;
        Assert.NotEqual(clone.Positions[0], source.Positions[0]);
        Assert.NotEqual(clone.BindPosePositions[0], source.BindPosePositions![0]);
    }

    [Fact]
    public void NpcAssemblyCloneUsesTheExhaustiveClonePolicy()
    {
        var source = FilledSubmesh("npc");

        var clone = NpcExportSceneBuilder.CloneSubmesh(source);

        AssertEquivalent(source, clone, deepCloneArrays: true);
    }

    [Fact]
    public void GeometryOverlayDeepClonesGeometryAndAppliesEveryRenderStateField()
    {
        var geometry = GeometrySubmesh();
        var renderState = FilledSubmesh("resolved");

        var clone = RenderableSubmeshCloner.CloneGeometryWithRenderState(geometry, renderState);

        var geometryProperties = GeometryPropertyNames.ToHashSet(StringComparer.Ordinal);
        foreach (var property in WritableProperties())
        {
            object? expected = property.Name switch
            {
                nameof(RenderableSubmesh.SourceNifPath) => renderState.SourceNifPath,
                nameof(RenderableSubmesh.SkyType) => renderState.SkyType,
                _ when geometryProperties.Contains(property.Name) => property.GetValue(geometry),
                _ => property.GetValue(renderState)
            };
            var actual = property.GetValue(clone);
            AssertPropertyValue(property.Name, expected, actual,
                GeometryArrayPropertyNames.Contains(property.Name, StringComparer.Ordinal));
        }

        Assert.Same(renderState.ShaderMetadata, clone.ShaderMetadata);
        Assert.Same(renderState.MaterialAlphaController, clone.MaterialAlphaController);
        Assert.Same(renderState.ParticleRuntime, clone.ParticleRuntime);
    }

    [Fact]
    public void GeometryOverlayRetainsGeometryProvenanceWhenResolvedStateHasNoOverride()
    {
        var geometry = GeometrySubmesh();
        var renderState = FilledSubmesh("resolved");
        renderState.SourceNifPath = null;
        renderState.SkyType = null;

        var clone = RenderableSubmeshCloner.CloneGeometryWithRenderState(geometry, renderState);

        Assert.Equal(geometry.SourceNifPath, clone.SourceNifPath);
        Assert.Equal(geometry.SkyType, clone.SkyType);
    }

    private static void AssertEveryWritablePropertyIsCovered()
    {
        Assert.Equal(
            ExpectedWritablePropertyNames.OrderBy(static name => name, StringComparer.Ordinal),
            WritableProperties().Select(static property => property.Name)
                .OrderBy(static name => name, StringComparer.Ordinal));
    }

    private static void AssertEquivalent(
        RenderableSubmesh expected,
        RenderableSubmesh actual,
        bool deepCloneArrays)
    {
        foreach (var property in WritableProperties())
        {
            AssertPropertyValue(
                property.Name,
                property.GetValue(expected),
                property.GetValue(actual),
                deepCloneArrays && GeometryArrayPropertyNames.Contains(property.Name, StringComparer.Ordinal));
        }
    }

    private static void AssertPropertyValue(
        string propertyName,
        object? expected,
        object? actual,
        bool requireIndependentArray)
    {
        if (expected is Array expectedArray)
        {
            var actualArray = Assert.IsAssignableFrom<Array>(actual);
            if (requireIndependentArray)
            {
                Assert.NotSame(expectedArray, actualArray);
            }

            Assert.Equal(expectedArray.Cast<object?>().ToArray(), actualArray.Cast<object?>().ToArray());
            return;
        }

        Assert.True(Equals(expected, actual),
            $"{propertyName} changed: expected '{expected ?? "<null>"}', actual '{actual ?? "<null>"}'.");
    }

    private static PropertyInfo[] WritableProperties()
    {
        return typeof(RenderableSubmesh)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(static property => property.SetMethod is not null)
            .ToArray();
    }

    private static RenderableSubmesh GeometrySubmesh()
    {
        return new RenderableSubmesh
        {
            ShapeName = "hierarchy-shape",
            Positions = [101f, 102f, 103f, 104f, 105f, 106f, 107f, 108f, 109f],
            LocalBounds = new NifLocalBounds(new Vector3(110f, 111f, 112f), 113f),
            Triangles = [0, 1, 2],
            Normals = [114f, 115f, 116f],
            UVs = [117f, 118f],
            VertexColors = [119, 120, 121, 122],
            Tangents = [123f, 124f, 125f],
            Bitangents = [126f, 127f, 128f],
            BindPosePositions = [129f, 130f, 131f],
            SourceNifPath = @"meshes\hierarchy.nif",
            SourceBlockIndex = 132,
            SkyType = SkyObjectType.Clouds
        };
    }

    private static RenderableSubmesh FilledSubmesh(string tag)
    {
        var alphaController = new NifMaterialAlphaController(
            40,
            $"{tag}-alpha",
            NifKeyInterpolation.Linear,
            [new NifFloatKey(0.25f, 0.75f)],
            null,
            default,
            default);
        var particleRuntime = new ParticleRuntimeDefinition(
            new ParticleSystemDefinition
            {
                BlockIndex = 41,
                Capacity = 42,
                DiffuseTexturePath = $@"textures\{tag}-particle.dds"
            },
            Matrix4x4.CreateTranslation(43f, 44f, 45f));

        return new RenderableSubmesh
        {
            ShapeName = $"{tag}-shape",
            Positions = [1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f],
            LocalBounds = new NifLocalBounds(new Vector3(10f, 11f, 12f), 13f),
            Triangles = [0, 1, 2],
            Normals = [14f, 15f, 16f],
            UVs = [17f, 18f],
            VertexColors = [19, 20, 21, 22],
            StarfieldMaterialColor = new StarfieldMaterialColorRenderState(
                StarfieldMaterialColorRenderMode.ConstantLerp,
                new Vector4(0.11f, 0.22f, 0.33f, 0.44f)),
            StarfieldMaterialAlpha = new StarfieldMaterialAlphaRenderState(
                StarfieldMaterialAlphaRenderMode.Layer0OpacityCutout,
                0.45f),
            Tangents = [23f, 24f, 25f],
            Bitangents = [26f, 27f, 28f],
            DiffuseTexturePath = $@"textures\{tag}-diffuse.dds",
            ClampTextureU = true,
            ClampTextureV = true,
            NormalMapTexturePath = $@"textures\{tag}-normal.dds",
            SpecularMapTexturePath = $@"textures\{tag}-specular.dds",
            GradientMapTexturePath = $@"textures\{tag}-gradient.dds",
            GradientMapV = 0.29f,
            BgsmGlowMapTexturePath = $@"textures\{tag}-bgsm-glow.dds",
            BgsmEmissionColor = new Vector3(0.31f, 0.32f, 0.33f),
            EnvironmentMapTexturePath = $@"textures\{tag}-environment.dds",
            EnvironmentMapScale = 0.34f,
            EnvironmentMapSmoothness = 0.35f,
            ClassicEnvironmentMapTexturePath = $@"textures\{tag}-classic-environment.dds",
            ClassicEnvironmentMaskTexturePath = $@"textures\{tag}-classic-mask.dds",
            ClassicEnvironmentMapScale = 0.36f,
            ClassicEnvironmentMapUsesWindowReflection = true,
            ClassicEnvironmentMapIsSphereMap = true,
            ClassicParallaxHeightMapTexturePath = $@"textures\{tag}-height.dds",
            IsDecal = true,
            EffectTint = (0.37f, 0.38f, 0.39f),
            EffectFalloff = (0.40f, 0.41f, 0.42f, 0.43f),
            SoftParticleFalloffDepth = 44f,
            ShaderMetadata = new NifShaderTextureMetadata
            {
                PropertyType = $"{tag}-shader",
                ShaderType = 45,
                ShaderFlags = 46,
                TextureSlots = [$@"textures\{tag}-slot.dds"],
                MaterialPath = $@"materials\{tag}.bgsm"
            },
            IsEmissive = true,
            UseVertexColors = true,
            UseVertexAlphaForOpacity = false,
            IsDoubleSided = true,
            HasAlphaBlend = true,
            HasAlphaTest = true,
            AlphaTestThreshold = 47,
            AlphaTestFunction = 3,
            SrcBlendMode = 4,
            DstBlendMode = 5,
            MaterialAlpha = 0.48f,
            MaterialAlphaController = alphaController,
            MaterialGlossiness = 49f,
            SpecularColor = (0.50f, 0.51f, 0.52f),
            MaterialDiffuse = (0.53f, 0.54f, 0.55f),
            IsEyeEnvmap = true,
            EnvMapScale = 0.56f,
            RenderOrder = 57,
            TintColor = (0.58f, 0.59f, 0.60f),
            IsFaceGen = true,
            SubsurfaceColor = (0.61f, 0.62f, 0.63f),
            AnimatedEmissiveColor = (0.64f, 0.65f, 0.66f),
            EmissiveColor = (0.67f, 0.68f, 0.69f),
            Lighting30EmissionColor = (0.70f, 0.71f, 0.72f),
            IsLighting30 = true,
            Lighting30EmissionMultiplier = 0.73f,
            Lighting30GlowMapTexturePath = $@"textures\{tag}-lighting30-glow.dds",
            UvScrollVelocity = new Vector2(0.74f, 0.75f),
            BindPosePositions = [76f, 77f, 78f],
            SourceNifPath = $@"meshes\{tag}.nif",
            SourceBlockIndex = 79,
            SkyType = SkyObjectType.Stars,
            IsBillboard = true,
            BillboardMode = NifBillboardMode.AlwaysFaceCenter,
            IsLeafBillboard = true,
            IsParticleCloud = true,
            ParticleRuntime = particleRuntime,
            IsSpeedTreeBranch = true,
            SpeedTreeWindSpeeds = new Vector2(0.80f, 0.81f),
            SpeedTreeLod = new SpeedTreeLodMetadata(
                1,
                3,
                82f,
                83f,
                SpeedTreeLodComponent.Leaf),
            IsFarLodFallback = true
        };
    }
}
