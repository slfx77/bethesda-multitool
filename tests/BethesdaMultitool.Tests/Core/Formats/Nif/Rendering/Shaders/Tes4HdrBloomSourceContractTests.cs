using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Shaders;

/// <summary>
///     Compiler-free structural and numeric pins for Oblivion.exe's separate bright-pass followed
///     by cumulative two-axis blur pairs. Runtime shader compilation is covered by the authoritative
///     permutation inventory.
/// </summary>
public sealed class Tes4HdrBloomSourceContractTests
{
    [Fact]
    public void Shaders_KeepBrightExtractionSeparateAndWriteOpaqueBlurTargets()
    {
        var bloom = SourceContract.ReadShaderSource("bloom.frag.hlsl");
        var brightPass = SourceContract.Extract(
            bloom,
            "float4 mainTes4BrightPass",
            "float3 ClassicBlurRgb");
        var tes4Blur = bloom[bloom.IndexOf("float4 mainTes4Blur", StringComparison.Ordinal)..];

        Assert.Contains("uSource.SampleLevel(uSampler, input.vUv, 0).rgb", brightPass,
            StringComparison.Ordinal);
        Assert.Contains("max(source - uBloom0.x, 0.0) * uBloom0.y", brightPass,
            StringComparison.Ordinal);
        Assert.Contains("return float4(max(source - uBloom0.x, 0.0) * uBloom0.y, 1.0);",
            brightPass, StringComparison.Ordinal);
        Assert.Contains("float3 sum = ClassicBlurRgb(input, ignoredAlpha);", tes4Blur,
            StringComparison.Ordinal);
        Assert.Contains("return float4(sum, 1.0);", tes4Blur, StringComparison.Ordinal);
    }

    [Fact]
    public void Runtime_PingPongsEachTes4PairCumulativelyAndReturnsTheFirstTarget()
    {
        var pass = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Gpu", "D3D12",
            "GpuTonemapPass12.cs");
        var tes4Predicate = SourceContract.Extract(
            pass,
            "var tes4HdrBloom = engineMode",
            "var historyWasPrimed");
        var tes4Arm = SourceContract.Extract(
            pass,
            "// HDR005 is a one-sample bright filter",
            "else\n            {");

        Assert.Contains("settings.Mode == GpuTonemapMode.EngineFo3Fnv", tes4Predicate,
            StringComparison.Ordinal);
        Assert.Contains("ClassicHdrBloomTopology.Tes4SeparateBrightPassCumulative", tes4Predicate,
            StringComparison.Ordinal);
        Assert.Contains(
            "CreateShaderResourceView(_brightPassBlurTexture!, avgSrvDesc, cpuBlur)",
            pass,
            StringComparison.Ordinal);
        Assert.Contains(
            "CreateShaderResourceView(_bloomTexture!, avgSrvDesc, cpuReverseBlur)",
            pass,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            tes4Arm,
            "BrightPassBlurGroup,\n                    _brightPassBlurTexture!",
            "for (var blurPair = 0; blurPair < classicPlan.BlurPairCount; blurPair++)",
            "b[5] = 1f / _bloomHeight",
            "BlurGroup,\n                        _bloomTexture!",
            "b[4] = 1f / _bloomWidth",
            "b[5] = 0f",
            "ReverseBlurGroup,\n                        _brightPassBlurTexture!");
        Assert.Contains("_tes4BrightPassPso", tes4Arm, StringComparison.Ordinal);
        Assert.Equal(2, tes4Arm.Split("_tes4BlurPso", StringSplitOptions.None).Length - 1);
        Assert.Contains(
            "finalBloom = tes4HdrBloom ? _brightPassBlurTexture! : _bloomTexture!;",
            pass,
            StringComparison.Ordinal);
        Assert.Contains("ClassicHdrPassPlan.CreateTes4(", pass, StringComparison.Ordinal);

        var capture = SourceContract.ReadAppSource("WorldView3DControl.SceneCapture.cs");
        Assert.Contains("tonemapClassicBloomTopology", capture, StringComparison.Ordinal);
        Assert.Contains("tonemapEffectiveBlurPairs", capture, StringComparison.Ordinal);
        Assert.Contains("tonemapBrightPassDraws", capture, StringComparison.Ordinal);
        Assert.Contains("tonemapBlurAxisDraws", capture, StringComparison.Ordinal);
    }

    [Fact]
    public void Composite_Tes4ReadsAdaptedRgbAndDoesNotUseBloomAlphaAsLuminance()
    {
        var tonemap = SourceContract.ReadShaderSource("tonemap.frag.hlsl");
        var classic = SourceContract.Extract(
            tonemap,
            "// EngineFo3Fnv",
            "// ADAPT temporally blends");

        Assert.Contains("bool tes4SeparateBloom = uParams5.w < -0.5;", classic,
            StringComparison.Ordinal);
        Assert.Contains("uParams3.z > 0.5 && !tes4SeparateBloom", classic,
            StringComparison.Ordinal);
        Assert.Contains("adapted.r + adapted.g + adapted.b", classic, StringComparison.Ordinal);
        Assert.Contains("bloomTerm = max(bloomTerm, 0.0);", classic, StringComparison.Ordinal);

        var pass = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Gpu", "D3D12",
            "GpuTonemapPass12.cs");
        SourceContract.AssertOrder(
            pass,
            "if (tes4HdrBloom)",
            "modernFamily = -1f;",
            "settings.White, settings.EyeAdaptStrength, settings.ReceiveBloomThreshold, modernFamily");
    }

    [Fact]
    public void Radius4Impulse_TwoPairsEqualASecondCumulativeConvolution()
    {
        var bloom = SourceContract.ReadShaderSource("bloom.frag.hlsl");
        var payloads = new[]
        {
            0x3E511048,
            0x3E387F7D,
            0x3DFD9B6B,
            0x3D87BEEE,
            0x3CE25956
        };
        foreach (var payload in payloads)
        {
            Assert.Contains($"asfloat(0x{payload:X8}u)", bloom, StringComparison.Ordinal);
        }

        var weights = new[]
        {
            BitConverter.Int32BitsToSingle(0x3CE25956),
            BitConverter.Int32BitsToSingle(0x3D87BEEE),
            BitConverter.Int32BitsToSingle(0x3DFD9B6B),
            BitConverter.Int32BitsToSingle(0x3E387F7D),
            BitConverter.Int32BitsToSingle(0x3E511048),
            BitConverter.Int32BitsToSingle(0x3E387F7D),
            BitConverter.Int32BitsToSingle(0x3DFD9B6B),
            BitConverter.Int32BitsToSingle(0x3D87BEEE),
            BitConverter.Int32BitsToSingle(0x3CE25956)
        };

        var impulse = new float[33];
        impulse[impulse.Length / 2] = 1f;
        var afterOneAxis = Convolve(impulse, weights);
        var afterSecondSameAxis = Convolve(afterOneAxis, weights);
        var onePairCenter = afterOneAxis[impulse.Length / 2] * afterOneAxis[impulse.Length / 2];
        var twoPairCenter =
            afterSecondSameAxis[impulse.Length / 2] * afterSecondSameAxis[impulse.Length / 2];

        Assert.Equal(0x3E511048, BitConverter.SingleToInt32Bits(weights[4]));
        Assert.Equal(0x3CE25956, BitConverter.SingleToInt32Bits(weights[0]));
        Assert.NotEqual(onePairCenter, twoPairCenter);
        Assert.True(twoPairCenter < onePairCenter);
    }

    private static float[] Convolve(float[] source, float[] weights)
    {
        var radius = weights.Length / 2;
        var result = new float[source.Length];
        for (var destination = radius; destination < source.Length - radius; destination++)
        {
            for (var tap = -radius; tap <= radius; tap++)
            {
                result[destination] += source[destination + tap] * weights[tap + radius];
            }
        }

        return result;
    }
}
