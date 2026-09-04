using BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Materials;

public sealed class NifNormalMapStrengthPolicyTests
{
    [Fact]
    public void GenericDefaultUsesUnattenuatedRetailNormal()
    {
        Assert.Equal(1f, NifNormalMapStrengthPolicy.GenericDefault);
    }

    [Fact]
    public void SpriteNativeReferenceAndGltfRenderersConsumeSharedDefault()
    {
        var spriteRenderer = SourceContract.ReadSource(
            ["src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering",
                "Rasterization", "NifSpriteRenderer.cs"]);
        var referenceCache = SourceContract.ReadSource(
            ["src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering",
                "D3D12", "ReferenceMeshCache12.cs"]);
        var gltfWriter = SourceContract.ReadSource(
            ["src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering",
                "Export", "GlbWriter.cs"]);

        Assert.Contains(
            "BumpStrength { get; set; } = NifNormalMapStrengthPolicy.GenericDefault;",
            spriteRenderer,
            StringComparison.Ordinal);
        Assert.Contains(
            "NifNormalMapStrengthPolicy.GenericDefault,",
            referenceCache,
            StringComparison.Ordinal);
        Assert.Contains(
            "material.WithNormal(image, NifNormalMapStrengthPolicy.GenericDefault);",
            gltfWriter,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ReferenceBumpStrength", referenceCache, StringComparison.Ordinal);
    }
}
