using System.Numerics;
using BethesdaMultitool.CLI.Rendering.Npc;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Particles;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Npc;

public sealed class NpcMeshHelpersCloneTests
{
    [Fact]
    public void ClonePreservesAuthoredMaterialAndSourceEligibilityWithoutChangingOtherParts()
    {
        var source = new NifRenderableModel();
        source.Submeshes.Add(new RenderableSubmesh
        {
            Positions = [],
            Triangles = [],
            ShapeName = "LowerBody:0",
            LegacyMaterialName = "skin",
            HasAuthoredOblivionBodySkinInputs = true,
            AuthoredOblivionBodySkinDiffusePath = @"textures\characters\orc\female\leg.dds",
            SourceNifPath = @"meshes\armor\iron\f\greaves.nif",
            SourceBlockIndex = 7,
            LocalBounds = new NifLocalBounds(new Vector3(1f, 2f, 3f), 4f),
            MaterialDiffuse = (0.125f, 0.375f, 0.625f),
            MaterialGlossiness = 37f,
            SpecularColor = (0.25f, 0.5f, 0.75f),
            MaterialAlpha = 0.875f,
            DiffuseTexturePath = @"npc\00085969\body-skin.dds",
            NormalMapTexturePath = @"textures\armor\iron\f\greaves_n.dds",
            IsFaceGen = true,
            SubsurfaceColor = (0.0625f, 0.1875f, 0.3125f)
        });
        source.Submeshes.Add(new RenderableSubmesh { Positions = [], Triangles = [], ShapeName = "Armor:0" });

        var clone = NpcMeshHelpers.DeepCloneModel(source);

        Assert.Equal(2, clone.Submeshes.Count);
        var skin = clone.Submeshes[0];
        Assert.NotSame(source.Submeshes[0], skin);
        Assert.Equal("LowerBody:0", skin.ShapeName);
        Assert.Equal("skin", skin.LegacyMaterialName);
        Assert.True(skin.HasAuthoredOblivionBodySkinInputs);
        Assert.Equal(@"textures\characters\orc\female\leg.dds", skin.AuthoredOblivionBodySkinDiffusePath);
        Assert.Equal(@"meshes\armor\iron\f\greaves.nif", skin.SourceNifPath);
        Assert.Equal(7, skin.SourceBlockIndex);
        Assert.Equal(new NifLocalBounds(new Vector3(1f, 2f, 3f), 4f), skin.LocalBounds);
        Assert.Equal((0.125f, 0.375f, 0.625f), skin.MaterialDiffuse!.Value);
        Assert.Equal(37f, skin.MaterialGlossiness);
        Assert.Equal((0.25f, 0.5f, 0.75f), skin.SpecularColor);
        Assert.Equal(0.875f, skin.MaterialAlpha);
        Assert.Equal(@"npc\00085969\body-skin.dds", skin.DiffuseTexturePath);
        Assert.Equal(@"textures\armor\iron\f\greaves_n.dds", skin.NormalMapTexturePath);
        Assert.True(skin.IsFaceGen);
        Assert.Equal((0.0625f, 0.1875f, 0.3125f), skin.SubsurfaceColor);
        var armor = clone.Submeshes[1];
        Assert.Equal("Armor:0", armor.ShapeName);
        Assert.False(armor.HasAuthoredOblivionBodySkinInputs);
        Assert.Null(armor.AuthoredOblivionBodySkinDiffusePath);
        Assert.Null(armor.MaterialDiffuse);
        Assert.False(armor.IsFaceGen);
    }

    [Fact]
    public void ClonePreservesExtendedRenderStateAndSharesReadOnlySourceDescriptors()
    {
        var metadata = new NifShaderTextureMetadata { PropertyType = "BSShaderPPLightingProperty" };
        var alphaController = new NifMaterialAlphaController(
            17, "alpha", NifKeyInterpolation.Linear,
            [new NifFloatKey(0.25f, 0.75f)], null, default, default);
        var particles = new ParticleRuntimeDefinition(
            new ParticleSystemDefinition { BlockIndex = 19, Capacity = 8 }, Matrix4x4.Identity);
        var source = new NifRenderableModel();
        source.Submeshes.Add(new RenderableSubmesh
        {
            Positions = [],
            Triangles = [],
            ShaderMetadata = metadata,
            MaterialAlphaController = alphaController,
            ParticleRuntime = particles,
            ClampTextureU = true,
            ClampTextureV = true,
            SpecularMapTexturePath = @"textures\material_s.dds",
            ClassicEnvironmentMapTexturePath = @"textures\environment.dds",
            ClassicEnvironmentMaskTexturePath = @"textures\material_m.dds",
            ClassicEnvironmentMapScale = 0.625f,
            ClassicEnvironmentMapIsSphereMap = true,
            EffectTint = (0.125f, 0.25f, 0.5f),
            UvScrollVelocity = new Vector2(0.0625f, 0.1875f),
            IsLighting30 = true,
            Lighting30EmissionColor = (0.25f, 0.375f, 0.5f),
            Lighting30EmissionMultiplier = 2f,
            Lighting30GlowMapTexturePath = @"textures\material_g.dds"
        });

        var clonedSubmesh = Assert.Single(NpcMeshHelpers.DeepCloneModel(source).Submeshes);

        Assert.Same(metadata, clonedSubmesh.ShaderMetadata);
        Assert.Same(alphaController, clonedSubmesh.MaterialAlphaController);
        Assert.Same(particles, clonedSubmesh.ParticleRuntime);
        Assert.True(clonedSubmesh.ClampTextureU);
        Assert.True(clonedSubmesh.ClampTextureV);
        Assert.Equal(@"textures\material_s.dds", clonedSubmesh.SpecularMapTexturePath);
        Assert.Equal(@"textures\environment.dds", clonedSubmesh.ClassicEnvironmentMapTexturePath);
        Assert.Equal(@"textures\material_m.dds", clonedSubmesh.ClassicEnvironmentMaskTexturePath);
        Assert.Equal(0.625f, clonedSubmesh.ClassicEnvironmentMapScale);
        Assert.True(clonedSubmesh.ClassicEnvironmentMapIsSphereMap);
        Assert.Equal((0.125f, 0.25f, 0.5f), clonedSubmesh.EffectTint);
        Assert.Equal(new Vector2(0.0625f, 0.1875f), clonedSubmesh.UvScrollVelocity);
        Assert.True(clonedSubmesh.IsLighting30);
        Assert.Equal((0.25f, 0.375f, 0.5f), clonedSubmesh.Lighting30EmissionColor!.Value);
        Assert.Equal(2f, clonedSubmesh.Lighting30EmissionMultiplier);
        Assert.Equal(@"textures\material_g.dds", clonedSubmesh.Lighting30GlowMapTexturePath);
    }

    [Fact]
    public void CloneOwnsEveryGeometryArrayAndMutationsLeaveSourceUntouched()
    {
        var submesh = new RenderableSubmesh
        {
            Positions = [1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f],
            Triangles = [0, 1, 2],
            Normals = [0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f],
            UVs = [0.125f, 0.25f, 0.375f, 0.5f, 0.625f, 0.75f],
            VertexColors = [17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28],
            Tangents = [1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f],
            Bitangents = [0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f],
            BindPosePositions = [11f, 12f, 13f, 14f, 15f, 16f, 17f, 18f, 19f]
        };
        var source = new NifRenderableModel();
        source.Submeshes.Add(submesh);

        var clone = Assert.Single(NpcMeshHelpers.DeepCloneModel(source).Submeshes);

        AssertIndependentArray(submesh.Positions, clone.Positions);
        AssertIndependentArray(submesh.Triangles, clone.Triangles);
        AssertIndependentArray(submesh.Normals!, clone.Normals!);
        AssertIndependentArray(submesh.UVs!, clone.UVs!);
        AssertIndependentArray(submesh.VertexColors!, clone.VertexColors!);
        AssertIndependentArray(submesh.Tangents!, clone.Tangents!);
        AssertIndependentArray(submesh.Bitangents!, clone.Bitangents!);
        AssertIndependentArray(submesh.BindPosePositions!, clone.BindPosePositions!);
        clone.Positions[0] = -1f;
        clone.Triangles[0] = 2;
        clone.Normals![0] = -2f;
        clone.UVs![0] = -3f;
        clone.VertexColors![0] = 255;
        clone.Tangents![0] = -4f;
        clone.Bitangents![0] = -5f;
        clone.BindPosePositions![0] = -6f;
        Assert.Equal(1f, submesh.Positions[0]);
        Assert.Equal((ushort)0, submesh.Triangles[0]);
        Assert.Equal(0f, submesh.Normals![0]);
        Assert.Equal(0.125f, submesh.UVs![0]);
        Assert.Equal((byte)17, submesh.VertexColors![0]);
        Assert.Equal(1f, submesh.Tangents![0]);
        Assert.Equal(0f, submesh.Bitangents![0]);
        Assert.Equal(11f, submesh.BindPosePositions![0]);
    }

    [Fact]
    public void CloneRetainsExactModelBoundsAndProvenanceInsteadOfRecomputingThem()
    {
        var source = new NifRenderableModel
        {
            MinX = -11f, MinY = -12f, MinZ = -13f,
            MaxX = 21f, MaxY = 22f, MaxZ = 23f,
            WasSkinned = true, ContainsParticleSource = true
        };
        source.Submeshes.Add(new RenderableSubmesh { Positions = [1f, 2f, 3f], Triangles = [] });

        var clone = NpcMeshHelpers.DeepCloneModel(source);

        Assert.NotSame(source, clone);
        Assert.NotSame(source.Submeshes, clone.Submeshes);
        Assert.Equal(-11f, clone.MinX);
        Assert.Equal(-12f, clone.MinY);
        Assert.Equal(-13f, clone.MinZ);
        Assert.Equal(21f, clone.MaxX);
        Assert.Equal(22f, clone.MaxY);
        Assert.Equal(23f, clone.MaxZ);
        Assert.True(clone.WasSkinned);
        Assert.True(clone.ContainsParticleSource);
        clone.MinX = -99f;
        clone.WasSkinned = false;
        clone.ContainsParticleSource = false;
        clone.Submeshes.Clear();
        Assert.Equal(-11f, source.MinX);
        Assert.True(source.WasSkinned);
        Assert.True(source.ContainsParticleSource);
        Assert.Single(source.Submeshes);
    }

    [Fact]
    public void CloneKeepsAbsentOptionalGeometryAndMaterialStateAbsent()
    {
        var source = new NifRenderableModel();
        source.Submeshes.Add(new RenderableSubmesh { Positions = [1f, 2f, 3f], Triangles = [] });

        var clone = Assert.Single(NpcMeshHelpers.DeepCloneModel(source).Submeshes);

        Assert.Null(clone.Normals);
        Assert.Null(clone.UVs);
        Assert.Null(clone.VertexColors);
        Assert.Null(clone.Tangents);
        Assert.Null(clone.Bitangents);
        Assert.Null(clone.BindPosePositions);
        Assert.Null(clone.ShaderMetadata);
        Assert.Null(clone.MaterialAlphaController);
        Assert.Null(clone.ParticleRuntime);
        Assert.Null(clone.MaterialDiffuse);
        Assert.Null(clone.TintColor);
        Assert.Null(clone.AuthoredOblivionBodySkinDiffusePath);
        Assert.False(clone.HasAuthoredOblivionBodySkinInputs);
        Assert.False(clone.IsFaceGen);
        Assert.Equal(10f, clone.MaterialGlossiness);
        Assert.Equal((0f, 0f, 0f), clone.SpecularColor);
        Assert.Empty(clone.Triangles);
    }

    [Fact]
    public void EmptyCloneKeepsSentinelBoundsAndOwnsItsSubmeshCollection()
    {
        var source = new NifRenderableModel();

        var clone = NpcMeshHelpers.DeepCloneModel(source);

        Assert.NotSame(source, clone);
        Assert.NotSame(source.Submeshes, clone.Submeshes);
        Assert.Empty(clone.Submeshes);
        Assert.False(clone.HasGeometry);
        Assert.False(clone.WasSkinned);
        Assert.False(clone.ContainsParticleSource);
        Assert.Equal(float.MaxValue, clone.MinX);
        Assert.Equal(float.MaxValue, clone.MinY);
        Assert.Equal(float.MaxValue, clone.MinZ);
        Assert.Equal(float.MinValue, clone.MaxX);
        Assert.Equal(float.MinValue, clone.MaxY);
        Assert.Equal(float.MinValue, clone.MaxZ);
        clone.Submeshes.Add(new RenderableSubmesh { Positions = [], Triangles = [] });
        Assert.Empty(source.Submeshes);
    }

    private static void AssertIndependentArray<T>(T[] source, T[] clone)
    {
        Assert.NotSame(source, clone);
        Assert.Equal<T>(source, clone);
    }
}
