using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     Source contracts for the Windows-only D3D12 routing that the platform-neutral test target
///     cannot instantiate. The shader math itself is covered by executable CPU vectors and fxc
///     compilation tests; these checks keep the host/root-signature wiring from silently reverting
///     MSAA water to the view-angle fallback.
/// </summary>
public sealed class WaterMsaaDepthPipelineSourceTests
{
    [Fact]
    public void SharedRootSignatureAliasesBindlessMsaaDepthInSpaceThree()
    {
        var source = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Gpu", "D3D12",
            "GpuRootSignature12.cs");
        var rangeStart = source.IndexOf("var bindlessDepthMsaa = new DescriptorRange1", StringComparison.Ordinal);
        var tableEnd = source.IndexOf("ShaderVisibility.All);", rangeStart, StringComparison.Ordinal);

        Assert.True(rangeStart >= 0);
        Assert.True(tableEnd > rangeStart);
        var contract = source[rangeStart..tableEnd];
        Assert.Contains("NumDescriptors = uint.MaxValue", contract, StringComparison.Ordinal);
        Assert.Contains("BaseShaderRegister = 0", contract, StringComparison.Ordinal);
        Assert.Contains("RegisterSpace = 3", contract, StringComparison.Ordinal);
        Assert.Contains("OffsetInDescriptorsFromTableStart = 0", contract, StringComparison.Ordinal);
        Assert.Contains(
            "new RootDescriptorTable1(bindlessTextures, bindlessCubemaps, bindlessDepthMsaa)",
            contract,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LiveAndCaptureWaterRoutesPassTheActualDepthSampleCount()
    {
        var frame = SourceContract.ReadAppSource("WorldView3DControl.Frame.cs");
        var capture = SourceContract.ReadAppSource("WorldView3DControl.SceneCapture.cs");

        var liveStart = frame.IndexOf("var waterUsesDepth =", StringComparison.Ordinal);
        var liveEnd = frame.IndexOf(
            "_gpuTimestampProfiler12?.Write(cmd, GpuTimestampRegion.WaterEnd);",
            liveStart,
            StringComparison.Ordinal);
        Assert.True(liveStart >= 0);
        Assert.True(liveEnd > liveStart);
        var liveRoute = frame[liveStart..liveEnd];
        var liveWaterStart = liveRoute.IndexOf("_water?.SetSceneDepth(", StringComparison.Ordinal);
        var liveWaterEnd = liveRoute.IndexOf(");", liveWaterStart, StringComparison.Ordinal);
        Assert.True(liveWaterStart >= 0);
        Assert.True(liveWaterEnd > liveWaterStart);
        var liveWaterCall = liveRoute[liveWaterStart..(liveWaterEnd + 2)];
        Assert.Contains("surface.SampleCount);", liveWaterCall, StringComparison.Ordinal);
        Assert.DoesNotContain("!surface.IsMsaa", liveRoute, StringComparison.Ordinal);

        var captureStart = capture.IndexOf("var captureWaterUsesDepth =", StringComparison.Ordinal);
        var captureEnd = capture.IndexOf("var sampledDepthState =", captureStart, StringComparison.Ordinal);
        Assert.True(captureStart >= 0);
        Assert.True(captureEnd > captureStart);
        var captureRoute = capture[captureStart..captureEnd];
        var captureWaterStart = captureRoute.IndexOf("_water?.SetSceneDepth(", StringComparison.Ordinal);
        Assert.True(captureWaterStart >= 0);
        var captureWaterEnd = captureRoute.IndexOf(");", captureWaterStart, StringComparison.Ordinal);
        Assert.True(captureWaterEnd > captureWaterStart);
        var captureWaterCall = captureRoute[captureWaterStart..(captureWaterEnd + 2)];
        Assert.Contains("target.SampleCount);", captureWaterCall, StringComparison.Ordinal);
        Assert.DoesNotContain("!target.IsMsaa", captureRoute, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveAndCaptureDepthFactoriesAndTransitionsPreserveTheMsaaViewContract()
    {
        var lifecycle = SourceContract.ReadAppSource("WorldView3DControl.Lifecycle.cs");
        var frame = SourceContract.ReadAppSource("WorldView3DControl.Frame.cs");
        var capture = SourceContract.ReadAppSource("WorldView3DControl.SceneCapture.cs");

        var liveFactoryStart = lifecycle.IndexOf("private void EnsureDepthSrv()", StringComparison.Ordinal);
        var liveFactoryEnd = lifecycle.IndexOf("private void DisposeRenderResources()", liveFactoryStart,
            StringComparison.Ordinal);
        Assert.True(liveFactoryStart >= 0);
        Assert.True(liveFactoryEnd > liveFactoryStart);
        var liveFactory = lifecycle[liveFactoryStart..liveFactoryEnd];
        Assert.Contains("ViewDimension = _surface12!.IsMsaa", liveFactory, StringComparison.Ordinal);
        Assert.Contains("ShaderResourceViewDimension.Texture2DMultisampled", liveFactory,
            StringComparison.Ordinal);

        var captureFactoryStart = capture.IndexOf("private bool TryEnsureCaptureDepthSrv(",
            StringComparison.Ordinal);
        var captureFactoryEnd = capture.IndexOf("internal bool Profiler_TrySelectWorldspaceByName(",
            captureFactoryStart, StringComparison.Ordinal);
        Assert.True(captureFactoryStart >= 0);
        Assert.True(captureFactoryEnd > captureFactoryStart);
        var captureFactory = capture[captureFactoryStart..captureFactoryEnd];
        Assert.Contains("ViewDimension = target.IsMsaa", captureFactory, StringComparison.Ordinal);
        Assert.Contains("ShaderResourceViewDimension.Texture2DMultisampled", captureFactory,
            StringComparison.Ordinal);

        Assert.Contains("ResourceStates.DepthRead |", frame, StringComparison.Ordinal);
        Assert.Contains("ResourceStates.PixelShaderResource", frame, StringComparison.Ordinal);
        Assert.Contains("ResourceStates.DepthRead |", capture, StringComparison.Ordinal);
        Assert.Contains("ResourceStates.PixelShaderResource", capture, StringComparison.Ordinal);
    }

    [Fact]
    public void WaterTelemetryNamesRtFreeTechniqueAndDepthRouteTruthfully()
    {
        var source = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Camera", "D3D12",
            "WaterRenderer12.cs");
        var callStart = source.IndexOf("LastStats.WaterTechnique = DescribeTechnique(", StringComparison.Ordinal);
        var helperStart = source.IndexOf("private static string DescribeTechnique(", StringComparison.Ordinal);
        var helperEnd = source.IndexOf("public void Dispose()", helperStart, StringComparison.Ordinal);

        Assert.True(callStart >= 0);
        Assert.True(helperStart > callStart);
        Assert.True(helperEnd > helperStart);
        var call = source[callStart..helperStart];
        Assert.Contains("_game,", call, StringComparison.Ordinal);
        Assert.Contains("depthSample,", call, StringComparison.Ordinal);
        Assert.Contains("_depthSampleCount);", call, StringComparison.Ordinal);

        var helper = source[helperStart..helperEnd];
        Assert.Contains(
            "game is BethesdaGame.Fallout3 or BethesdaGame.FalloutNewVegas",
            helper,
            StringComparison.Ordinal);
        Assert.Contains("? \"FnvWater003RtFree\"", helper, StringComparison.Ordinal);
        Assert.Contains("$\"{game}-ClassicRtFreeWaterStandIn\"", helper, StringComparison.Ordinal);
        Assert.Contains("$\"scene-depth-msaa{sampleCount}x\"", helper, StringComparison.Ordinal);
        Assert.Contains("\"scene-depth-1x\"", helper, StringComparison.Ordinal);
        Assert.Contains(
            "else if (!modernPipeline &&",
            helper,
            StringComparison.Ordinal);
        Assert.Contains(
            "shaderVariant is WaterShaderVariant.FnvWater000 or WaterShaderVariant.Fo4Water",
            helper,
            StringComparison.Ordinal);
        Assert.Contains("\"hardware-depth-view-angle-fallback\"", helper, StringComparison.Ordinal);
        Assert.Contains("\"hardware-depth-no-scene-depth\"", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("FnvWater000-scene-depth", helper, StringComparison.Ordinal);
    }

    [Fact]
    public void WaterSampleCountUsesTheExistingUniformSpareWithoutGrowingTheAbi()
    {
        var source = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Camera", "D3D12",
            "WaterRenderer12.cs");

        Assert.Contains("private const uint UniformsByteSize = ModernWaterPipeline.FrameUniformByteSize;",
            source,
            StringComparison.Ordinal);
        Assert.Contains("RenderOrigin = new Vector4(renderOrigin, _depthSampleCount)",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidEnabledDepthMetadataIsRejectedInsteadOfChangingHostRouting()
    {
        var source = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Camera", "D3D12",
            "WaterRenderer12.cs");
        var methodStart = source.IndexOf("public void SetSceneDepth(", StringComparison.Ordinal);
        var methodEnd = source.IndexOf("public void SetModernCubeMap(", methodStart, StringComparison.Ordinal);

        Assert.True(methodStart >= 0);
        Assert.True(methodEnd > methodStart);
        var method = source[methodStart..methodEnd];
        Assert.Contains("sampleCount < 1", method, StringComparison.Ordinal);
        Assert.Contains("_depthBindlessIndex = NoNormalMap;", method, StringComparison.Ordinal);
        Assert.Contains("throw new ArgumentOutOfRangeException(nameof(sampleCount)", method,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Math.Max(sampleCount, 1)", method, StringComparison.Ordinal);
    }
}