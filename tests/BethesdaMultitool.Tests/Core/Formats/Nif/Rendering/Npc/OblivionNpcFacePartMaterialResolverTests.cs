using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Assets;
using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;
using BethesdaMultitool.Tests.Helpers;
using Xunit;
using NpcTextureHelpers = BethesdaMultitool.CLI.Rendering.Npc.NpcTextureHelpers;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Npc;

public sealed class OblivionNpcFacePartMaterialResolverTests
{
    private const string EarDiffusePath = @"textures\characters\imperial\earshuman.dds";
    private const string EffectiveEarDiffusePath = @"body_egt\000222A8_ears.dds";

    [Fact]
    public void Apply_ResolvesFamilyNormalAndBuildsNativeBumpInputs()
    {
        var normalMapPath = FaceGenHeadShaderFamilyResolver.BuildSiblingPath(EarDiffusePath, "_n")!;
        using var textureResolver = new NifTextureResolver();
        textureResolver.InjectTexture(normalMapPath, TestTextures.Single(127, 127, 255, 255));
        var submesh = CreateTriangle();

        var bumpReady = OblivionNpcFacePartMaterialResolver.Apply(
            submesh,
            textureResolver,
            EarDiffusePath,
            EffectiveEarDiffusePath);

        Assert.True(bumpReady);
        Assert.Equal(EffectiveEarDiffusePath, submesh.DiffuseTexturePath);
        Assert.Equal(normalMapPath, submesh.NormalMapTexturePath);
        Assert.Equal<float>([1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f], submesh.Tangents!);
        Assert.Equal<float>([0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f], submesh.Bitangents!);

        var decoded = ReferenceSubmeshDecoder12.Decode(
            submesh,
            new ReferenceSubmeshDecodeOptions12(
                submesh.DiffuseTexturePath,
                submesh.NormalMapTexturePath));
        Assert.True(decoded.HasBump);
        Assert.Equal(normalMapPath, decoded.NormalMapTexturePath);
    }

    [Fact]
    public void Apply_MissingNormalSiblingDoesNotClaimBumpMapping()
    {
        using var textureResolver = new NifTextureResolver();
        var submesh = CreateTriangle();

        var bumpReady = OblivionNpcFacePartMaterialResolver.Apply(
            submesh,
            textureResolver,
            EarDiffusePath,
            EffectiveEarDiffusePath);

        Assert.False(bumpReady);
        Assert.Null(submesh.NormalMapTexturePath);
        Assert.Null(submesh.Tangents);
        Assert.Null(submesh.Bitangents);
    }

    [Fact]
    public void ApplyClassicSkin2000_MissingNormalRetainsSkinFamilyWhileBumpFailsClosed()
    {
        using var textureResolver = new NifTextureResolver();
        var submesh = CreateTriangle();

        var bumpReady = OblivionNpcFacePartMaterialResolver.ApplyClassicSkin2000(
            submesh,
            textureResolver,
            EarDiffusePath,
            EffectiveEarDiffusePath);

        Assert.False(bumpReady);
        Assert.True(submesh.IsFaceGen);
        Assert.Equal(EffectiveEarDiffusePath, submesh.DiffuseTexturePath);
        Assert.Null(submesh.NormalMapTexturePath);
        Assert.Null(submesh.Tangents);
        Assert.Null(submesh.Bitangents);
    }

    [Fact]
    public void Apply_ZeroNormalFailsClosedWithoutCreatingNonFiniteTangentSpace()
    {
        using var textureResolver = CreateResolverWithNormalMap();
        var submesh = CreateTriangle();
        Array.Clear(submesh.Normals!);

        var bumpReady = OblivionNpcFacePartMaterialResolver.Apply(
            submesh,
            textureResolver,
            EarDiffusePath,
            EffectiveEarDiffusePath);

        AssertBumpRejected(submesh, bumpReady);
    }

    [Fact]
    public void Apply_NonFiniteNormalFailsClosedWithoutCreatingNonFiniteTangentSpace()
    {
        using var textureResolver = CreateResolverWithNormalMap();
        var submesh = CreateTriangle();
        submesh.Normals![0] = float.NaN;

        var bumpReady = OblivionNpcFacePartMaterialResolver.Apply(
            submesh,
            textureResolver,
            EarDiffusePath,
            EffectiveEarDiffusePath);

        AssertBumpRejected(submesh, bumpReady);
    }

    [Fact]
    public void Apply_TangentParallelToNormalFailsClosedAfterOrthogonalization()
    {
        using var textureResolver = CreateResolverWithNormalMap();
        var submesh = CreateTriangle();
        submesh.Normals![0] = 1f;
        submesh.Normals[2] = 0f;
        submesh.Normals[3] = 1f;
        submesh.Normals[5] = 0f;
        submesh.Normals[6] = 1f;
        submesh.Normals[8] = 0f;

        var bumpReady = OblivionNpcFacePartMaterialResolver.Apply(
            submesh,
            textureResolver,
            EarDiffusePath,
            EffectiveEarDiffusePath);

        AssertBumpRejected(submesh, bumpReady);
    }

    [Fact]
    public void Apply_DiscardsNonFiniteAuthoredTangentsAndBuildsFiniteReplacement()
    {
        using var textureResolver = CreateResolverWithNormalMap();
        var submesh = CreateTriangle();
        submesh.Tangents = Enumerable.Repeat(float.NaN, submesh.Positions.Length).ToArray();
        submesh.Bitangents = new float[submesh.Positions.Length];

        var bumpReady = OblivionNpcFacePartMaterialResolver.Apply(
            submesh,
            textureResolver,
            EarDiffusePath,
            EffectiveEarDiffusePath);

        Assert.True(bumpReady);
        Assert.NotNull(submesh.Tangents);
        Assert.NotNull(submesh.Bitangents);
        Assert.All(submesh.Tangents!, value => Assert.True(float.IsFinite(value)));
        Assert.All(submesh.Bitangents!, value => Assert.True(float.IsFinite(value)));
    }

    [Fact]
    public void BuildNpcEarEgtTextureKey_PreservesComparisonVariantIdentity()
    {
        var npc = new NpcAppearance
        {
            NpcFormId = 0x000222A8,
            RenderVariantLabel = "npc_plus_race"
        };

        Assert.Equal(
            @"body_egt\000222A8_npc_plus_race_ears.dds",
            NpcTextureHelpers.BuildNpcEarEgtTextureKey(npc));
    }

    private static RenderableSubmesh CreateTriangle()
    {
        return new RenderableSubmesh
        {
            Positions = [0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f],
            Triangles = [0, 1, 2],
            Normals = [0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f],
            UVs = [0f, 0f, 1f, 0f, 0f, 1f],
            DiffuseTexturePath = EarDiffusePath
        };
    }

    private static NifTextureResolver CreateResolverWithNormalMap()
    {
        var normalMapPath = FaceGenHeadShaderFamilyResolver.BuildSiblingPath(EarDiffusePath, "_n")!;
        var textureResolver = new NifTextureResolver();
        textureResolver.InjectTexture(normalMapPath, TestTextures.Single(127, 127, 255, 255));
        return textureResolver;
    }

    private static void AssertBumpRejected(RenderableSubmesh submesh, bool bumpReady)
    {
        Assert.False(bumpReady);
        Assert.Null(submesh.NormalMapTexturePath);
        Assert.Null(submesh.Tangents);
        Assert.Null(submesh.Bitangents);
    }
}
