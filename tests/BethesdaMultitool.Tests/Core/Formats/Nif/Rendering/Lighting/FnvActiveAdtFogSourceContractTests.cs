using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Lighting;

public sealed class FnvActiveAdtFogSourceContractTests
{
    [Theory]
    [InlineData("reference.vert.hlsl", "REFERENCE_SPECIALIZED_DIRECT_VERTEX", "REFERENCE_OBLIVION_CLASSIC_SKIN")]
    [InlineData("reference_instanced.vert.hlsl", "REFERENCE_SPECIALIZED_INSTANCED_VERTEX", "SHADOW_CARD_LIGHT_FACING")]
    public void VertexPath_ProducesANamedInterpolatedAmountFromActualClipPosition(
        string shaderName, string specializedGuard, string independentGuard)
    {
        var shader = SourceContract.ReadShaderSource(shaderName);
        Assert.Contains($"#if !defined({specializedGuard}) && !defined({independentGuard})", shader,
            StringComparison.Ordinal);
        Assert.Contains("float vFnvActiveAdtFogAmount : TEXCOORD20;", shader, StringComparison.Ordinal);
        Assert.DoesNotContain("noperspective float vFnvActiveAdtFogAmount", shader, StringComparison.Ordinal);
        Assert.DoesNotContain("nointerpolation float vFnvActiveAdtFogAmount", shader, StringComparison.Ordinal);
        SourceContract.AssertOrder(shader, "o.Position = mul(uViewProj, worldPos);",
            "o.vFnvActiveAdtFogAmount = 0.0;",
            "IsFnvActiveAdtBaseMaterial(uTextureState.z) && uFogColorFogEnabled.w >= 0.5",
            "o.vFnvActiveAdtFogAmount = EvaluateFnvActiveAdtVertexFog(o.Position);");
    }

    [Fact]
    public void FogEquation_RetainsForwardClipXyzRangeAndPowerWithoutGenericRepairs()
    {
        var shader = SourceContract.ReadSource("src", "BethesdaMultitool", "Core", "Formats", "Nif",
            "Rendering", "Gpu", "Shaders", "Include", "fnv_active_adt_fog.hlsli");
        SourceContract.AssertOrder(shader,
            "float3 forwardClip = float3(reversedClipPosition.xy, reversedClipPosition.w - reversedClipPosition.z);",
            "float distance = length(forwardClip);",
            "float inverseRange = rcp(uAtmosphereParams.z - uAtmosphereParams.y);",
            "float linearAmount = 1.0 - saturate((uAtmosphereParams.z - distance) * inverseRange);",
            "return pow(linearAmount, uCameraPosFogPower.w);");
        Assert.DoesNotContain("uFogFarColorMax", shader, StringComparison.Ordinal);
        Assert.DoesNotContain("max(", shader, StringComparison.Ordinal);
        Assert.DoesNotContain("/ reversedClipPosition.w", shader, StringComparison.Ordinal);
    }

    [Fact]
    public void PixelPath_UsesExactlyOneFogCompositeAndLeavesAlphaSeparate()
    {
        var shader = SourceContract.ReadShaderSource("reference.frag.hlsl");
        SourceContract.AssertOrder(shader, "float outAlpha = fnvActiveAdtBase",
            "float3 outputRgb;", "if (fnvActiveAdtBase && uFogColorFogEnabled.w >= 0.5)",
            "outputRgb = lerp(lit, uFogColorFogEnabled.rgb, input.vFnvActiveAdtFogAmount);",
            "else", "outputRgb = ApplyFog(lit, input.vWorldPos, input.vEnvMap.w);",
            "return float4(outputRgb, outAlpha);");
        Assert.Equal(1,
            shader.Split("outputRgb = lerp(lit, uFogColorFogEnabled.rgb, input.vFnvActiveAdtFogAmount);").Length - 1);
    }

    [Fact]
    public void SourceAdmission_PrecedesRepairsAndFrameChecksTheExactUploadedValues()
    {
        var atmosphere = SourceContract.ReadSource("src", "BethesdaMultitool", "Core", "Formats", "Nif",
            "Rendering", "Atmosphere", "AtmosphereState.cs");
        SourceContract.AssertOrder(atmosphere,
            "var hasUnmodifiedFnvAdtFogSource = FnvActiveAdtFog.HasUnmodifiedWeatherSource(",
            "fogFar = fogNear + 1f;", "fogPower = MathF.Max(fogPower, 0.01f);",
            "HasUnmodifiedFnvAdtFogSource = hasUnmodifiedFnvAdtFogSource");
        var frame = SourceContract.ReadSource("src", "BethesdaMultitool", "App", "Controls", "WorldView3D",
            "WorldView3DControl.Frame.cs");
        SourceContract.AssertOrder(frame, "var constants = AtmosphereConstants.From(",
            "var finiteAdtFogSupported = FnvActiveAdtFog.IsSupported(",
            "resolved.HasUnmodifiedFnvAdtFogSource,",
            "constants.Params.Y, constants.Params.Z, constants.CameraPosFogPower.W,",
            "lightingOn, projectedSunShadowActive, fogEnabled, finiteAdtFogSupported);",
            "*(AtmosphereConstants*)alloc.CpuPtr = constants;");
    }

    [Fact]
    public void RuntimeState_ReachesTheSharedDrawEligibilityAndReportsTheObservedRoute()
    {
        var renderer = SourceContract.ReadSource("src", "BethesdaMultitool", "Core", "Formats", "Nif",
            "Rendering", "D3D12", "ReferenceRenderer12.cs");
        Assert.Contains("bool hasSupportedFog = false)", renderer, StringComparison.Ordinal);
        Assert.Contains("_fnvActiveAdtFogSupported = hasSupportedFog;", renderer, StringComparison.Ordinal);
        Assert.Contains("submesh.AlphaTestFunction,\n            _fnvActiveAdtFogSupported);",
            renderer.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("textureState = ResolveTextureState(sub);", renderer, StringComparison.Ordinal);
        Assert.Contains("var textureState = ResolveTextureState(draw.Submesh);", renderer, StringComparison.Ordinal);
        var diagnostics = SourceContract.ReadSource("src", "BethesdaMultitool", "Core", "Formats", "Nif",
            "Rendering", "D3D12", "ReferenceRenderer12.FnvDiagnostics.cs");
        Assert.Contains("[\"finiteAdtFogSupported\"] = _fnvActiveAdtFogSupported", diagnostics,
            StringComparison.Ordinal);
        Assert.Contains("[\"activeAdtVertexFog\"] = _fnvActiveAdtFogEnabled &&", diagnostics, StringComparison.Ordinal);
    }
}