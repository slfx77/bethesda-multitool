using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Water;

/// <summary>
///     Source contracts for water's hardware occlusion: the depth-sample PSOs keep the reversed-Z
///     GreaterEqual depth test against the host's READ-ONLY DSV while the scene depth is
///     simultaneously the shader's SRV, and the WATER_HARDWARE_OCCLUSION shader variants drop the
///     pixel-rate occlusion clip (whose binary keep/kill aliased water edges at MSAA'd mesh
///     silhouettes). Regression here re-introduces bright fringes around meshes in front of water.
/// </summary>
public sealed class WaterHardwareOcclusionSourceContractTests
{
    [Fact]
    public void EveryWaterShaderGuardsItsOcclusionClipBehindTheHardwareOcclusionMacro()
    {
        // Exactly one occlusion clip per per-game water file (the shared depth block in the four
        // variant mains; FnvWater003LocalFallback in the WATER001 program), each compiled out of
        // the hardware-occlusion variants.
        foreach (var file in (string[])
                 [
                     "water_fnv.frag.hlsl",
                     "water_oblivion.frag.hlsl",
                     "water_fo4.frag.hlsl",
                     "water_morrowind.frag.hlsl",
                     "water_fnv001.frag.hlsl",
                 ])
        {
            var shader = SourceContract.ReadShaderSource(file);
            Assert.Equal(1, CountOccurrences(shader, "clip(column + asfloat(uDepthParams.w));"));
            Assert.Equal(1, CountOccurrences(shader, "#if !WATER_HARDWARE_OCCLUSION"));
            SourceContract.AssertOrder(
                shader,
                "#if !WATER_HARDWARE_OCCLUSION",
                "clip(column + asfloat(uDepthParams.w));",
                "#endif");
        }

        // The non-finite fail-closed guard is NOT occlusion and must stay unconditional.
        var fallback = Extract(
            SourceContract.ReadShaderSource("water_fnv001.frag.hlsl"),
            "float4 FnvWater003LocalFallback(", "float noiseFade =");
        SourceContract.AssertOrder(
            fallback,
            "clip(-1.0);",
            "#if !WATER_HARDWARE_OCCLUSION",
            "clip(column + asfloat(uDepthParams.w));",
            "#endif");
    }

    [Fact]
    public void DepthSamplePsosKeepTheHardwareDepthStateAndUseHardwareOcclusionShaders()
    {
        var renderer = ReadRenderer();

        // The old depth-disabled template must not come back: every water PSO shares the
        // reversed-Z GreaterEqual / write-mask-zero state and the D32 DSV format.
        Assert.DoesNotContain("DepthEnable = false", renderer, StringComparison.Ordinal);
        Assert.DoesNotContain("psoDesc.DepthStencilFormat = Format.Unknown;", renderer,
            StringComparison.Ordinal);

        // Every depth-sample pixel shader is a WATER_HARDWARE_OCCLUSION compile: the shared four,
        // FNV WATER001, and the modern (FO4 architectural) clone of the depth-sample template.
        Assert.Equal(6, CountOccurrences(renderer,
            "new ShaderMacro(\"WATER_HARDWARE_OCCLUSION\", \"1\")"));
        SourceContract.AssertOrder(
            renderer,
            "var psDepthSampleBytecode = CompileEmbeddedShader(",
            "psoDesc.PixelShader = psDepthSampleBytecode;",
            "_depthSamplePsoTemplate = psoDesc;",
            "_psoDepthSample = gpu.Device.CreateGraphicsPipelineState(psoDesc);",
            "psoDesc.PixelShader = psFnvWater001Bytecode;",
            "_psoFnvWater001DepthSample = gpu.Device.CreateGraphicsPipelineState(psoDesc);");
        Assert.Contains("pixelDepthDescription.PixelShader = modernPixelDepthSampleBytecode;",
            renderer, StringComparison.Ordinal);
    }

    [Fact]
    public void HostsBindTheReadOnlyDsvWhileWaterSamplesDepth()
    {
        var frame = SourceContract.ReadAppSource("WorldView3DControl.Frame.cs");
        var liveBranch = Extract(frame, "if (waterUsesDepth)", "visibleWater = _showWater");
        Assert.Contains("cmd.OMSetRenderTargets(sceneRtv, surface.ReadOnlyDepthStencilView);",
            liveBranch, StringComparison.Ordinal);

        var capture = SourceContract.ReadAppSource("WorldView3DControl.SceneCapture.cs");
        var captureBranch = Extract(capture, "if (captureWaterUsesDepth)",
            "_water.RenderAtTime(viewProj, cylinder, Vector3.Zero, animationTimeSeconds)");
        Assert.Contains("target.BindColorReadOnlyDepth(cmd);", captureBranch,
            StringComparison.Ordinal);
    }

    private static string ReadRenderer()
    {
        return SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "WaterRenderer12.cs");
    }

    private static int CountOccurrences(string source, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static string Extract(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing start marker `{startMarker}`.");
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing end marker `{endMarker}` after `{startMarker}`.");
        return source[start..end];
    }
}