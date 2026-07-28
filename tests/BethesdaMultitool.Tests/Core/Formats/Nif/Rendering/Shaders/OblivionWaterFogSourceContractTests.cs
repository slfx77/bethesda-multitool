using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Shaders;

/// <summary>
///     Pins the Oblivion water surface-fog source: the engine fills c9 FogParam AND the fog color
///     from the SCENE fog property (FUN_007dcbd0 — tools/GhidraProject/
///     oblivion_water_fog_source_decompiled.txt), NOT from the WATR DATA fog range (that pair is the
///     underwater range; DefaultWater authors FogNear = −8192, which fog-washed the whole surface
///     when the shader blended with it — "water looks more like fog than water").
/// </summary>
public sealed class OblivionWaterFogSourceContractTests
{
    [Fact]
    public void OblivionSurfaceFogBlock_UsesSceneFogDistances_NotWatrRange()
    {
        var shader = SourceContract.ReadShaderSource("water_oblivion.frag.hlsl");

        var blockStart = shader.IndexOf(
            "// Oblivion WATER000 surface fog:", StringComparison.Ordinal);
        Assert.True(blockStart >= 0, "Oblivion surface-fog block comment missing.");
        var blockEnd = shader.IndexOf("return float4(color, alpha);", blockStart, StringComparison.Ordinal);
        Assert.True(blockEnd > blockStart);
        var block = shader[blockStart..blockEnd];

        // Scene fog distances from the atmosphere CB drive visibility…
        Assert.Contains("uAtmosphereParams.z > uAtmosphereParams.y", block, StringComparison.Ordinal);
        Assert.Contains("saturate((uAtmosphereParams.z - viewDist)", block, StringComparison.Ordinal);
        // …the WATR fog range (uLegacySurface1.xy) must NOT re-enter the surface blend, and the
        // Skyrim-curve ApplyFog helper must stay out of the recovered TES4 composite.
        Assert.DoesNotContain("uLegacySurface1.x", block, StringComparison.Ordinal);
        Assert.DoesNotContain("uLegacySurface1.y", block, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyFog(", block, StringComparison.Ordinal);
    }
}