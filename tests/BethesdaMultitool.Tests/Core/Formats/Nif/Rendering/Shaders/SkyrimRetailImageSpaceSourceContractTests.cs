using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Shaders;

/// <summary>
///     Structural guard for the Oldrim pair-13/pair-15..21 route. Runtime shader compilation is
///     tested separately; these assertions preserve the recovered instruction semantics even when the
///     optional compiler gate is unavailable.
/// </summary>
public sealed class SkyrimRetailImageSpaceSourceContractTests
{
    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(SourceContract.RepoRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public void Pair13Route_DispatchesBeforeMode5AndUsesRecoveredConstantsAndPivot()
    {
        var shader = Read(
            "src/BethesdaMultitool/Core/Formats/Nif/Rendering/Gpu/Shaders/Post/tonemap.frag.hlsl");

        var skyrimStart = shader.IndexOf("if (uParams0.z >= 5.5)", StringComparison.Ordinal);
        var classicSdrStart = shader.IndexOf("if (uParams0.z >= 4.5)", StringComparison.Ordinal);
        Assert.True(skyrimStart >= 0, "EngineSkyrim dispatch is missing.");
        Assert.True(classicSdrStart > skyrimStart,
            "Mode 6 must dispatch before ClassicSdrBloom's >=4.5 branch.");

        var functionStart = shader.IndexOf(
            "float3 ApplySkyrimTonemapBlendCinematic", StringComparison.Ordinal);
        var functionEnd = shader.IndexOf("float4 main(", functionStart, StringComparison.Ordinal);
        var function = shader[functionStart..functionEnd];
        Assert.Contains("float3(0.2125, 0.7154, 0.0721)", function, StringComparison.Ordinal);
        Assert.Contains("float q = luminance * (adaptedFast / adaptedSlow);", function,
            StringComparison.Ordinal);
        Assert.Contains("q / (uParams5.x * uParams5.x)", function, StringComparison.Ordinal);
        Assert.Contains("bloom * saturate(uParams5.z - mapped)", function, StringComparison.Ordinal);
        Assert.Contains("graded - adaptedSlow", function, StringComparison.Ordinal);
        Assert.DoesNotContain("AcesFilmic", function, StringComparison.Ordinal);
    }

    [Fact]
    public void Pair21Route_PreservesSlowFastLaneOrderAndMinimumStep()
    {
        var shader = Read(
            "src/BethesdaMultitool/Core/Formats/Nif/Rendering/Gpu/Shaders/Post/tonemap.frag.hlsl");
        var functionStart = shader.IndexOf("float2 ApplySkyrimAdapt", StringComparison.Ordinal);
        var functionEnd = shader.IndexOf("float4 mainAdapt", functionStart, StringComparison.Ordinal);
        var function = shader[functionStart..functionEnd];

        Assert.Contains("float2 factors = float2(uParams3.y, uParams4.x);", function,
            StringComparison.Ordinal);
        Assert.Contains("max(abs(delta * factors), float2(1.0 / 256.0, 1.0 / 256.0))", function,
            StringComparison.Ordinal);
        // The min() wraps across lines: outer min(abs(delta), max(...)) is the no-overshoot clamp.
        Assert.Contains("float2 stepMagnitude = min(", function, StringComparison.Ordinal);
        Assert.Contains("abs(delta),", function, StringComparison.Ordinal);
        Assert.Contains("previous + sign(delta) * stepMagnitude", function, StringComparison.Ordinal);
    }

    [Fact]
    public void CpuPacking_ReusesModernExposureLaneOnlyForExplicitSkyrimMode()
    {
        var pass = Read(
            "src/BethesdaMultitool/Core/Formats/Nif/Rendering/Gpu/D3D12/GpuTonemapPass12.cs");
        Assert.Contains(
            "settings.Mode == GpuTonemapMode.EngineSkyrim ? adaptFactorFast : settings.AutoExposureMin",
            pass,
            StringComparison.Ordinal);
        Assert.Contains("cmd.SetGraphicsRoot32BitConstants(1, 24, p, 0);", pass,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReductionShaders_UseRecoveredFourAndSixteenTapTables()
    {
        var shader = Read(
            "src/BethesdaMultitool/Core/Formats/Nif/Rendering/Gpu/Shaders/Post/bloom.frag.hlsl");

        Assert.Contains("float4 mainSkyrimLuminance4", shader, StringComparison.Ordinal);
        Assert.Contains("asfloat(0x3E800000u)", shader, StringComparison.Ordinal);
        Assert.Contains("float2(-1.0,  1.0)", shader, StringComparison.Ordinal);
        Assert.Contains("float2( 1.0,  1.0)", shader, StringComparison.Ordinal);
        Assert.Contains("float4 mainSkyrimDownsample16", shader, StringComparison.Ordinal);
        Assert.Contains("asfloat(0x3D800000u)", shader, StringComparison.Ordinal);
        Assert.Contains("float2((float)x - 1.5, (float)y - 1.5)", shader,
            StringComparison.Ordinal);
        Assert.Contains("SamplerState uPointSampler : register(s1);", shader,
            StringComparison.Ordinal);
        Assert.Contains("uSource.SampleLevel(uPointSampler, uv + offset * texel, 0).r", shader,
            StringComparison.Ordinal);

        var pass = Read(
            "src/BethesdaMultitool/Core/Formats/Nif/Rendering/Gpu/D3D12/GpuTonemapPass12.cs");
        Assert.Contains("Filter.MinMagMipPoint", pass, StringComparison.Ordinal);
    }

    [Fact]
    public void Runtime_SelectsRgbThenLuminanceThenScalarAndFusesFinalAdaptReduction()
    {
        var pass = Read(
            "src/BethesdaMultitool/Core/Formats/Nif/Rendering/Gpu/D3D12/GpuTonemapPass12.cs");
        var tonemap = Read(
            "src/BethesdaMultitool/Core/Formats/Nif/Rendering/Gpu/Shaders/Post/tonemap.frag.hlsl");

        Assert.Contains("ClassicHdrPassPlan.CreateSkyrim(width, height, historyWasPrimed, bloomActive)", pass,
            StringComparison.Ordinal);
        Assert.Contains("0 => _downsamplePso", pass, StringComparison.Ordinal);
        Assert.Contains("1 => _skyrimLuminancePso", pass, StringComparison.Ordinal);
        Assert.Contains("_ => _skyrimDownsamplePso", pass, StringComparison.Ordinal);
        Assert.Contains("float SkyrimFinalLuminance", tonemap, StringComparison.Ordinal);
        Assert.Contains("asfloat(0x3D800000u)", tonemap, StringComparison.Ordinal);
        Assert.Contains("? uHdr.SampleLevel(uSampler, float2(0.5, 0.5), 0).r", tonemap,
            StringComparison.Ordinal);
        Assert.DoesNotContain("if (uParams0.z >= 5.5)",
            tonemap[tonemap.IndexOf("float4 mainAvg", StringComparison.Ordinal)..],
            StringComparison.Ordinal);
    }

    [Fact]
    public void LiveAndCaptureFramesShareTemporalAdaptationPolicy()
    {
        var live = Read(
            "src/BethesdaMultitool/App/Controls/WorldView3D/WorldView3DControl.Frame.cs");
        var capture = Read(
            "src/BethesdaMultitool/App/Controls/WorldView3D/WorldView3DControl.SceneCapture.cs");

        Assert.Contains("ResolvePerFrameAdaptation(ResolveTonemapSettings(), deltaSeconds)", live,
            StringComparison.Ordinal);
        Assert.Contains("ResolvePerFrameAdaptation(\n            ResolveTonemapSettings(), captureDeltaSeconds)",
            capture.Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeRetainsSeparateReductionFamiliesAndDoesNotAllocateDisabledBloom()
    {
        var pass = Read(
            "src/BethesdaMultitool/Core/Formats/Nif/Rendering/Gpu/D3D12/GpuTonemapPass12.cs");

        Assert.Contains("private const int RtvDescriptorCount = RtvBankSize * 2;", pass,
            StringComparison.Ordinal);
        Assert.Contains("AlternateClassicFamilyMatches(plan)", pass, StringComparison.Ordinal);
        Assert.Contains("SwapClassicTargetSets();", pass, StringComparison.Ordinal);
        Assert.Contains("plan.BloomEnabled && _brightPassBlurTexture is null", pass,
            StringComparison.Ordinal);
        Assert.Contains("plan.BloomEnabled && _bloomTexture is null", pass,
            StringComparison.Ordinal);
    }
}
