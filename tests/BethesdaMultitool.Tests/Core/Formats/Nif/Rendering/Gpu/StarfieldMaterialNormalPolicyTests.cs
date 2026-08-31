using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Gpu;

public sealed class StarfieldMaterialNormalPolicyTests
{
    [Fact]
    public void Resolve_DerivesRoleQualifiedNormalForMatWithUsableBasis()
    {
        GpuMeshUploader.GpuVertex[] vertices =
        [
            Vertex(Vector3.UnitX, Vector3.UnitY)
        ];

        var result = StarfieldMaterialNormalPolicy.Resolve(
            @"Data/Materials/Test/Thing.MAT",
            null,
            false,
            vertices);

        Assert.Equal(
            @"materials\test\thing.mat" + MaterialTexturePathResolver.StarfieldNormalMapSuffix,
            result.TexturePath);
        Assert.True(result.HasBump);
        Assert.True(result.IsDerived);
    }

    [Fact]
    public void Resolve_PreservesExistingNormalAndBumpState()
    {
        GpuMeshUploader.GpuVertex[] vertices =
        [
            Vertex(Vector3.UnitX, Vector3.UnitY)
        ];

        var result = StarfieldMaterialNormalPolicy.Resolve(
            @"materials\test\thing.mat",
            @"textures\test\authored_n.dds",
            false,
            vertices);

        Assert.Equal(@"textures\test\authored_n.dds", result.TexturePath);
        Assert.False(result.HasBump);
        Assert.False(result.IsDerived);
    }

    [Theory]
    [InlineData(@"textures\test\thing_d.dds")]
    [InlineData(null)]
    public void Resolve_DoesNotDeriveForNonMaterialDiffuse(string? diffusePath)
    {
        GpuMeshUploader.GpuVertex[] vertices =
        [
            Vertex(Vector3.UnitX, Vector3.UnitY)
        ];

        var result = StarfieldMaterialNormalPolicy.Resolve(diffusePath, null, false, vertices);

        Assert.Null(result.TexturePath);
        Assert.False(result.HasBump);
        Assert.False(result.IsDerived);
    }

    [Fact]
    public void Resolve_RejectsZeroOrOneSidedTangentBases()
    {
        GpuMeshUploader.GpuVertex[] zero = [Vertex(Vector3.Zero, Vector3.Zero)];
        GpuMeshUploader.GpuVertex[] tangentOnly = [Vertex(Vector3.UnitX, Vector3.Zero)];

        var zeroResult = StarfieldMaterialNormalPolicy.Resolve(
            @"materials\test\thing.mat", null, false, zero);
        var tangentOnlyResult = StarfieldMaterialNormalPolicy.Resolve(
            @"materials\test\thing.mat", null, false, tangentOnly);

        Assert.False(zeroResult.IsDerived);
        Assert.False(tangentOnlyResult.IsDerived);
    }

    [Fact]
    public void Resolve_RejectsNonFiniteTangentBases()
    {
        GpuMeshUploader.GpuVertex[] vertices =
        [
            Vertex(new Vector3(float.NaN, 0f, 0f), Vector3.UnitY),
            Vertex(Vector3.UnitX, new Vector3(float.PositiveInfinity, 0f, 0f))
        ];

        var result = StarfieldMaterialNormalPolicy.Resolve(
            @"materials\test\thing.mat", null, false, vertices);

        Assert.Null(result.TexturePath);
        Assert.False(result.HasBump);
        Assert.False(result.IsDerived);
    }

    [Fact]
    public void Resolve_PreservesPreexistingBumpStateWhenNoPathCanBeDerived()
    {
        var result = StarfieldMaterialNormalPolicy.Resolve(
            @"materials\test\thing.mat",
            null,
            true,
            ReadOnlySpan<GpuMeshUploader.GpuVertex>.Empty);

        Assert.Null(result.TexturePath);
        Assert.True(result.HasBump);
        Assert.False(result.IsDerived);
    }

    private static GpuMeshUploader.GpuVertex Vertex(Vector3 tangent, Vector3 bitangent)
    {
        return new GpuMeshUploader.GpuVertex
        {
            Tangent = tangent,
            Bitangent = bitangent
        };
    }
}
