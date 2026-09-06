using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Profiling;

/// <summary>
///     Pins the non-GPU links behind the opt-in Atomic Wrangler capture oracle. The retail test pins
///     the literal ESM values; these source assertions prevent a future refactor from emitting a
///     plausible event after dropping the override before decode or batch admission.
/// </summary>
public sealed class FnvAtomicWranglerOverrideTraceSourceContractTests
{
    [Fact]
    public void PlacedAlternateTexture_RetainsProvenanceAndAuditsMaterializedBatchTextureKeys()
    {
        var builder = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "WorldData", "WorldMapOverlayBuilder.cs");
        var cache = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "WorldData", "WorldRenderCache.cs");
        var reference = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Scene",
            "RenderableReference.cs");
        var decoder = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "ReferenceMeshDecoder12.cs");
        var renderer = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "ReferenceRenderer12.cs");
        var environment = SourceContract.ReadSource("src", "BethesdaMultitool", "Core", "EnvironmentVariables.cs");

        Assert.Contains("entry.TextureSetFormId, entry.Index", builder, StringComparison.Ordinal);
        Assert.Contains("alternateTextureIndex.TryGetValue(p.BaseFormId, out var alt)", cache,
            StringComparison.Ordinal);
        Assert.Contains("BaseFormId: placement.BaseFormId", reference, StringComparison.Ordinal);
        Assert.Contains("placement.ModelPath! + \"#\" + alternateTextures.VariantKey", reference,
            StringComparison.Ordinal);
        Assert.Contains("overrides.TryGetValue(shapeName, out var textureOverride)", decoder,
            StringComparison.Ordinal);
        Assert.Contains("reference.AlternateTextures", renderer, StringComparison.Ordinal);
        Assert.Contains("TraceReferenceTextureOverrides(r, mesh);", renderer, StringComparison.Ordinal);
        Assert.Contains("EnvironmentVariables.Get(EnvironmentVariables.Viewer.ReferenceOverrideTrace)",
            renderer, StringComparison.Ordinal);
        Assert.Contains("ReferenceOverrideTrace = \"FALLOUT_VIEWER_REFERENCE_OVERRIDE_TRACE\"", environment,
            StringComparison.Ordinal);
        Assert.Contains("reference-texture-override", renderer, StringComparison.Ordinal);
        Assert.Contains("textureSetFormIdHex", renderer, StringComparison.Ordinal);
        Assert.Contains("matchingGpuSubmeshes", renderer, StringComparison.Ordinal);
        Assert.Contains("override-applied-batch-admitted", renderer, StringComparison.Ordinal);
    }
}
