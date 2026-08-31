using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

public sealed class BgsmGlbEmissionSourceContractTests
{
    [Fact]
    public void ExportClonesPreserveResolvedBgsmEmissionState()
    {
        var worldExportSource = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Export",
            "NifExportSceneBuilder.cs");
        var npcExportSource = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Npc", "Assembly",
            "NpcExportSceneBuilder.cs");

        Assert.Equal(2, SourceContract.CountOccurrences(
            worldExportSource,
            "BgsmGlowMapTexturePath = "));
        Assert.Equal(2, SourceContract.CountOccurrences(
            worldExportSource,
            "BgsmEmissionColor = "));
        Assert.Contains("BgsmGlowMapTexturePath = submesh.BgsmGlowMapTexturePath", npcExportSource,
            StringComparison.Ordinal);
        Assert.Contains("BgsmEmissionColor = submesh.BgsmEmissionColor", npcExportSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RegularBgsmEmissionUsesItsOwnPathAndFactorInGltf()
    {
        var source = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Export",
            "GlbWriter.cs");

        SourceContract.AssertOrder(
            source,
            "var hasActiveBgsmEmission = TryEncodeGltfEmission(",
            "var bgsmGlowTexture",
            "var emissiveTexturePath",
            "var emissiveFactor",
            "var emissiveStrength",
            "new MaterialCacheKey(",
            "emissiveTexturePath,",
            "hasActiveBgsmEmission,",
            "emissiveFactor,",
            "emissiveStrength,",
            "material.WithEmissive(emissiveImage, emissiveFactor, emissiveStrength);",
            "material.WithEmissive(emissiveFactor, emissiveStrength);");
    }

    [Fact]
    public void HdrEmissionIsLosslesslySplitBetweenBoundedFactorAndStrength()
    {
        var effective = new Vector3(2f, 0.5f, 4f);

        Assert.True(GlbWriter.TryEncodeGltfEmission(effective, out var factor, out var strength));

        Assert.Equal(new Vector3(0.5f, 0.125f, 1f), factor);
        Assert.Equal(4f, strength);
        Assert.Equal(effective, factor * strength);
    }

    [Fact]
    public void LdrEmissionKeepsUnitStrength()
    {
        var effective = new Vector3(0.25f, 0.5f, 0.75f);

        Assert.True(GlbWriter.TryEncodeGltfEmission(effective, out var factor, out var strength));

        Assert.Equal(effective, factor);
        Assert.Equal(1f, strength);
    }

    [Theory]
    [MemberData(nameof(InvalidEmissionValues))]
    public void InvalidOrInactiveEmissionFailsClosed(Vector3 effective)
    {
        Assert.False(GlbWriter.TryEncodeGltfEmission(effective, out var factor, out var strength));
        Assert.Equal(Vector3.Zero, factor);
        Assert.Equal(1f, strength);
    }

    public static TheoryData<Vector3> InvalidEmissionValues => new()
    {
        Vector3.Zero,
        new Vector3(-0.01f, 0.5f, 0.5f),
        new Vector3(float.NaN, 0.5f, 0.5f),
        new Vector3(0.5f, float.PositiveInfinity, 0.5f)
    };
}
