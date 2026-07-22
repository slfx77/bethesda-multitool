using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     Source contracts for the Windows-only D3D12 opaque-scene snapshot lifecycle. These verify
///     resource identity and exact state round trips without requiring a hardware device in the
///     platform-neutral test target.
/// </summary>
public sealed class WaterOpaqueSceneSnapshotSourceTests
{
    [Fact]
    public void LiveSurface_UsesBorrowedResolveAtMsaaAndLazilyCreatesOneSampleCopy()
    {
        var source = ReadSurface("GpuSwapChainSurface12.cs");

        Assert.Contains("public ID3D12Resource? WaterOpaqueSnapshotResource =>", source,
            StringComparison.Ordinal);
        Assert.Contains("_hdrResolve ?? _waterOpaqueCopy;", source, StringComparison.Ordinal);

        var resolveFactory = Extract(
            source,
            "private static ID3D12Resource? CreateHdrResolve(",
            "private static ID3D12Resource? CreateWaterOpaqueCopy(");
        Assert.Contains("if (sampleCount <= 1) return null;", resolveFactory, StringComparison.Ordinal);
        Assert.Contains(
            "ResourceDescription.Texture2D(SceneColorFormat, width, height,",
            resolveFactory,
            StringComparison.Ordinal);
        Assert.Contains("sampleCount: 1", resolveFactory, StringComparison.Ordinal);
        Assert.Contains("ResourceStates.ResolveDest", resolveFactory, StringComparison.Ordinal);

        var copyFactory = Extract(
            source,
            "private static ID3D12Resource? CreateWaterOpaqueCopy(",
            "#endif");
        Assert.Contains("if (sampleCount > 1) return null;", copyFactory, StringComparison.Ordinal);
        Assert.Contains(
            "ResourceDescription.Texture2D(SceneColorFormat, width, height,",
            copyFactory,
            StringComparison.Ordinal);
        Assert.Contains("sampleCount: 1", copyFactory, StringComparison.Ordinal);
        Assert.Contains("ResourceStates.CopyDest", copyFactory, StringComparison.Ordinal);

        var create = Extract(source, "public static GpuSwapChainSurface12? Create(", "public void Resize(");
        Assert.DoesNotContain("CreateWaterOpaqueCopy(", create, StringComparison.Ordinal);

        var resize = Extract(source, "public void Resize(", "public bool TryPrepareWaterOpaqueSnapshot(");
        Assert.Contains("_waterOpaqueCopy?.Dispose();", resize, StringComparison.Ordinal);
        Assert.Contains("_waterOpaqueCopy = null;", resize, StringComparison.Ordinal);
        Assert.Contains("_waterOpaqueSnapshotPrepared = false;", resize, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateWaterOpaqueCopy(", resize, StringComparison.Ordinal);

        var lazyEnsure = Extract(
            source,
            "public bool TryEnsureWaterOpaqueSnapshotResource()",
            "public bool TryPrepareWaterOpaqueSnapshot(");
        Assert.Contains(
            "_waterOpaqueCopy = CreateWaterOpaqueCopy(_device, _width, _height, _sampleCount);",
            lazyEnsure,
            StringComparison.Ordinal);
        Assert.Contains("catch (Exception ex)", lazyEnsure, StringComparison.Ordinal);
        Assert.Contains("return false;", lazyEnsure, StringComparison.Ordinal);

        var dispose = Extract(source, "public void Dispose()", "public static GpuSwapChainSurface12? Create(");
        Assert.Contains("_waterOpaqueCopy?.Dispose();", dispose, StringComparison.Ordinal);
        Assert.Contains("_waterOpaqueCopy = null;", dispose, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveSurface_PrepareAndRestoreRoundTripEveryOneAndFourSampleState()
    {
        var source = ReadSurface("GpuSwapChainSurface12.cs");
        var prepare = Extract(
            source,
            "public bool TryPrepareWaterOpaqueSnapshot(",
            "public bool RestoreWaterOpaqueSnapshot(");

        Assert.Contains(
            "sceneColor is null || snapshot is null || _waterOpaqueSnapshotPrepared",
            prepare,
            StringComparison.Ordinal);
        Assert.Contains("return false;", prepare, StringComparison.Ordinal);
        SourceContract.AssertOrder(
            prepare,
            "cmd.UnsetRenderTargets();",
            "ResourceStates.RenderTarget, ResourceStates.ResolveSource",
            "cmd.ResolveSubresource(snapshot, 0, sceneColor, 0, SceneColorFormat);",
            "ResourceStates.ResolveDest, ResourceStates.PixelShaderResource",
            "ResourceStates.ResolveSource, ResourceStates.RenderTarget");
        SourceContract.AssertOrder(
            prepare,
            "ResourceStates.RenderTarget, ResourceStates.CopySource",
            "cmd.CopyResource(snapshot, sceneColor);",
            "ResourceStates.CopyDest, ResourceStates.PixelShaderResource",
            "ResourceStates.CopySource, ResourceStates.RenderTarget");
        Assert.Contains("_waterOpaqueSnapshotPrepared = true;", prepare, StringComparison.Ordinal);

        var restore = Extract(
            source,
            "public bool RestoreWaterOpaqueSnapshot(",
            "public void ResolveTo(");
        Assert.Contains("snapshot is null || !_waterOpaqueSnapshotPrepared", restore, StringComparison.Ordinal);
        Assert.Contains("? ResourceStates.ResolveDest", restore, StringComparison.Ordinal);
        Assert.Contains(": ResourceStates.CopyDest;", restore, StringComparison.Ordinal);
        Assert.Contains(
            "ResourceStates.PixelShaderResource, idleState",
            restore,
            StringComparison.Ordinal);
        Assert.Contains("_waterOpaqueSnapshotPrepared = false;", restore, StringComparison.Ordinal);

        var finalResolve = Extract(source, "public void ResolveTo(", "public static void FinishBackBuffer(");
        SourceContract.AssertOrder(
            finalResolve,
            "RestoreWaterOpaqueSnapshot(cmd);",
            "cmd.ResourceBarrierTransition(_msaaColor, ResourceStates.RenderTarget, ResourceStates.ResolveSource);",
            "cmd.ResolveSubresource(_hdrResolve, 0, _msaaColor, 0, SceneColorFormat);");
    }

    [Fact]
    public void OffscreenTarget_MirrorsSnapshotShapeBranchAndReadbackSafety()
    {
        var source = ReadSurface("GpuOffscreenSceneTarget12.cs");

        Assert.Contains(
            "_disposed ? null : _hdrResolveTex ?? _waterOpaqueCopy;",
            source,
            StringComparison.Ordinal);
        var msaaAllocation = Extract(source, "if (msaa)", "// 8-bit tonemap output");
        Assert.Contains("ResourceDescription.Texture2D(ColorFormat, (uint)width, (uint)height,", msaaAllocation,
            StringComparison.Ordinal);
        Assert.Contains("ResourceStates.ResolveDest", msaaAllocation, StringComparison.Ordinal);
        Assert.DoesNotContain("_waterOpaqueCopy", msaaAllocation, StringComparison.Ordinal);

        var lazyEnsure = Extract(
            source,
            "public bool TryEnsureWaterOpaqueSnapshotResource()",
            "public bool TryPrepareWaterOpaqueSnapshot(");
        Assert.Contains("_waterOpaqueCopy = _gpu.Device.CreateCommittedResource", lazyEnsure,
            StringComparison.Ordinal);
        Assert.Contains("sampleCount: 1", lazyEnsure, StringComparison.Ordinal);
        Assert.Contains("ResourceStates.CopyDest", lazyEnsure, StringComparison.Ordinal);
        Assert.Contains("catch (Exception ex)", lazyEnsure, StringComparison.Ordinal);

        var prepare = Extract(
            source,
            "public bool TryPrepareWaterOpaqueSnapshot(",
            "public bool RestoreWaterOpaqueSnapshot(");
        Assert.Contains("_disposed || snapshot is null || _waterOpaqueSnapshotPrepared", prepare,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            prepare,
            "cmd.UnsetRenderTargets();",
            "ResourceStates.RenderTarget, ResourceStates.ResolveSource",
            "cmd.ResolveSubresource(snapshot, 0, _colorTex, 0, ColorFormat);",
            "ResourceStates.ResolveDest, ResourceStates.PixelShaderResource",
            "ResourceStates.ResolveSource, ResourceStates.RenderTarget");
        SourceContract.AssertOrder(
            prepare,
            "ResourceStates.RenderTarget, ResourceStates.CopySource",
            "cmd.CopyResource(snapshot, _colorTex);",
            "ResourceStates.CopyDest, ResourceStates.PixelShaderResource",
            "ResourceStates.CopySource, ResourceStates.RenderTarget");

        var readback = Extract(source, "public void RecordReadback(", "private void EnsureReadback(");
        SourceContract.AssertOrder(
            readback,
            "RestoreWaterOpaqueSnapshot(cmd);",
            "cmd.ResolveSubresource(_hdrResolveTex, 0, _colorTex, 0, ColorFormat);");

        var dispose = Extract(source, "public void Dispose()", "_colorTex.Dispose();");
        Assert.Contains("_waterOpaqueSnapshotPrepared = false;", dispose, StringComparison.Ordinal);
        Assert.Contains("_waterOpaqueCopy?.Dispose();", dispose, StringComparison.Ordinal);
        Assert.Contains("_hdrResolveTex?.Dispose();", dispose, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveHost_PreflightsCapturesBindsAndRestoresAroundWaterOnly()
    {
        var source = ReadAppSource("WorldView3DControl.Frame.cs");
        var waterPass = Extract(
            source,
            "_water?.SetSceneDepth(",
            "_gpuTimestampProfiler12?.Write(cmd, GpuTimestampRegion.WaterEnd);");

        SourceContract.AssertOrder(
            waterPass,
            "_water.GetFnvWater001Preflight(",
            "TryEnsureWaterOpaqueSnapshotSrv()",
            "surface.TryPrepareWaterOpaqueSnapshot(cmd)",
            "_water.SetFnvWater001Snapshot(",
            "cmd.OMSetRenderTargets(sceneRtv)",
            "_water?.Render(",
            "isPerspectiveProjection: !projectionActive",
            "surface.RestoreWaterOpaqueSnapshot(cmd);");
        Assert.Contains("isPerspectiveProjection: !projectionActive", waterPass,
            StringComparison.Ordinal);
        Assert.Contains("_water.SetFnvWater001Snapshot(null, 0, 0);", waterPass,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CaptureHost_WritesPersistentDescriptorsBeforePrimeLoopAndRestoresEachPass()
    {
        var source = ReadAppSource("WorldView3DControl.SceneCapture.cs");
        var capture = source[source.IndexOf("ulong fenceValue;", StringComparison.Ordinal)..];
        var loopStart = capture.IndexOf("for (var pass = captureShadows ? 0 : 1; pass < 2; pass++)",
            StringComparison.Ordinal);
        Assert.True(loopStart > 0);

        var beforeLoop = capture[..loopStart];
        SourceContract.AssertOrder(
            beforeLoop,
            "TryEnsureCaptureDepthSrv(target)",
            "_water?.SetSceneDepth(",
            "_water.GetFnvWater001Preflight(",
            "TryEnsureCaptureWaterOpaqueSnapshotSrv(target)");

        var pass = capture[loopStart..capture.IndexOf(
            "Profiler_LastCaptureScenarioSnapshot =",
            loopStart,
            StringComparison.Ordinal)];
        SourceContract.AssertOrder(
            pass,
            "_water.GetFnvWater001Preflight(",
            "target.TryPrepareWaterOpaqueSnapshot(cmd)",
            "_water.SetFnvWater001Snapshot(",
            "target.BindColorOnly(cmd)",
            "_water.RenderAtTime(viewProj, cylinder, Vector3.Zero, animationTimeSeconds)",
            "target.RestoreWaterOpaqueSnapshot(cmd);");
        Assert.DoesNotContain("TryEnsureCaptureDepthSrv(target)", pass, StringComparison.Ordinal);
        Assert.DoesNotContain("TryEnsureCaptureWaterOpaqueSnapshotSrv(target)", pass,
            StringComparison.Ordinal);
    }

    [Fact]
    public void HostDescriptorsAreSingleSampleSceneColorAndDieWithTheirHeap()
    {
        var lifecycle = ReadAppSource("WorldView3DControl.Lifecycle.cs");
        var liveFactory = Extract(
            lifecycle,
            "private bool TryEnsureWaterOpaqueSnapshotSrv()",
            "private void DisposeRenderResources()");
        Assert.Contains("GpuSwapChainSurface12.SceneColorFormat", liveFactory,
            StringComparison.Ordinal);
        Assert.Contains("ShaderResourceViewDimension.Texture2D", liveFactory,
            StringComparison.Ordinal);
        Assert.Contains("TryEnsureWaterOpaqueSnapshotResource()", liveFactory,
            StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(_waterOpaqueSnapshotSrvResource, snapshot)", liveFactory,
            StringComparison.Ordinal);
        Assert.Contains("_waterOpaqueSnapshotSrvResource = snapshot;", liveFactory,
            StringComparison.Ordinal);

        var capture = ReadAppSource("WorldView3DControl.SceneCapture.cs");
        var captureFactory = Extract(
            capture,
            "private bool TryEnsureCaptureWaterOpaqueSnapshotSrv(",
            "internal bool Profiler_TrySelectWorldspaceByName(");
        Assert.Contains("GpuOffscreenSceneTarget12.ColorFormat", captureFactory,
            StringComparison.Ordinal);
        Assert.Contains("ShaderResourceViewDimension.Texture2D", captureFactory,
            StringComparison.Ordinal);

        var device = ReadAppSource("WorldView3DControl.Device.cs");
        SourceContract.AssertOrder(
            device,
            "_cbvSrvUavHeap12?.Dispose(); _cbvSrvUavHeap12 = null;",
            "_waterOpaqueSnapshotSrv = null;",
            "_waterOpaqueSnapshotSrvResource = null;",
            "_captureWaterOpaqueSnapshotSrv = null;");
    }

    [Fact]
    public void SnapshotDescriptorFailureIsLocalAndReleasesAnUnusableDedicatedCopy()
    {
        var lifecycle = ReadAppSource("WorldView3DControl.Lifecycle.cs");
        var liveFactory = Extract(
            lifecycle,
            "private bool TryEnsureWaterOpaqueSnapshotSrv()",
            "private void DisposeRenderResources()");
        SourceContract.AssertOrder(
            liveFactory,
            "try",
            "_cbvSrvUavHeap12.AllocatePersistent()",
            "catch (Exception ex)",
            "_cbvSrvUavHeap12.FreePersistent(allocation.BindlessIndex);",
            "_surface12.ReleaseDedicatedWaterOpaqueSnapshotResource();",
            "return false;");

        var capture = ReadAppSource("WorldView3DControl.SceneCapture.cs");
        var captureFactory = Extract(
            capture,
            "private bool TryEnsureCaptureWaterOpaqueSnapshotSrv(",
            "internal bool Profiler_TrySelectWorldspaceByName(");
        SourceContract.AssertOrder(
            captureFactory,
            "try",
            "_cbvSrvUavHeap12.AllocatePersistent()",
            "catch (Exception ex)",
            "_cbvSrvUavHeap12.FreePersistent(allocation.BindlessIndex);",
            "target.ReleaseDedicatedWaterOpaqueSnapshotResource();",
            "return false;");

        var liveSurface = ReadSurface("GpuSwapChainSurface12.cs");
        var liveRelease = Extract(
            liveSurface,
            "public bool ReleaseDedicatedWaterOpaqueSnapshotResource()",
            "public CpuDescriptorHandle MsaaColorRtv");
        Assert.Contains("if (_waterOpaqueSnapshotPrepared || _waterOpaqueCopy is null) return false;",
            liveRelease, StringComparison.Ordinal);
        SourceContract.AssertOrder(liveRelease, "_waterOpaqueCopy.Dispose();", "_waterOpaqueCopy = null;", "return true;");

        var offscreen = ReadSurface("GpuOffscreenSceneTarget12.cs");
        var captureRelease = Extract(
            offscreen,
            "public bool ReleaseDedicatedWaterOpaqueSnapshotResource()",
            "public bool TonemapHistoryReset");
        Assert.Contains("_disposed || _waterOpaqueSnapshotPrepared || _waterOpaqueCopy is null",
            captureRelease, StringComparison.Ordinal);
        SourceContract.AssertOrder(captureRelease, "_waterOpaqueCopy.Dispose();", "_waterOpaqueCopy = null;", "return true;");
    }

    [Fact]
    public void LiveWaterFailureRestoresStatesOrAbortsTheUnsubmittedFrame()
    {
        var frame = ReadAppSource("WorldView3DControl.Frame.cs");
        var waterPass = Extract(
            frame,
            "var fnvWater001SnapshotPrepared = false;",
            "_gpuTimestampProfiler12?.Write(cmd, GpuTimestampRegion.WaterEnd);");
        SourceContract.AssertOrder(
            waterPass,
            "surface.TryPrepareWaterOpaqueSnapshot(cmd)",
            "waterDepthSampled = true;",
            "_water?.Render(",
            "catch",
            "surface.RestoreWaterOpaqueSnapshot(cmd);",
            "cmd.ResourceBarrierTransition(depthRes!, sampledDepthState,",
            "recorder.EndFrame();",
            "surface.DiscardWaterOpaqueSnapshotPreparation();",
            "recorder.AbortFrame();");

        var renderFailure = Extract(
            frame,
            "// A recording failure before EndFrame has not changed GPU resource states.",
            "// Tolerate TRANSIENT failures");
        SourceContract.AssertOrder(
            renderFailure,
            "_water?.SetFnvWater001Snapshot(null, 0, 0);",
            "_surface12?.DiscardWaterOpaqueSnapshotPreparation();",
            "_commandRecorder12?.AbortFrame();");

        var recorder = ReadSurface("GpuCommandRecorder12.cs");
        var abort = Extract(recorder, "public bool AbortFrame()", "public void EndFrame()");
        SourceContract.AssertOrder(
            abort,
            "if (!_frameOpen) return false;",
            "_commandList.Close();",
            "resource.Dispose();",
            "_currentFrameRetirements.Clear();",
            "_frameIndex = (_frameIndex + 1) % FramesInFlight;",
            "_frameOpen = false;",
            "return true;");
        Assert.DoesNotContain("ExecuteCommandList", abort, StringComparison.Ordinal);
    }

    [Fact]
    public void CaptureFailureRestoresSnapshotAndDepthBeforeSubmittingThePartialPass()
    {
        var capture = ReadAppSource("WorldView3DControl.SceneCapture.cs");
        var loop = Extract(
            capture,
            "for (var pass = captureShadows ? 0 : 1; pass < 2; pass++)",
            "Profiler_LastCaptureScenarioSnapshot =");
        SourceContract.AssertOrder(
            loop,
            "var captureFnvWater001SnapshotPrepared = false;",
            "var captureDepthSampled = false;",
            "target.TryPrepareWaterOpaqueSnapshot(cmd)",
            "captureDepthSampled = true;",
            "_water.RenderAtTime(viewProj, cylinder, Vector3.Zero, animationTimeSeconds)",
            "// Any exception after snapshot preparation or the depth transition",
            "target.RestoreWaterOpaqueSnapshot(cmd);",
            "target.BindColorOnly(cmd);",
            "ResourceStates.DepthWrite);",
            "target.Rebind(cmd);",
            "recorder.EndFrame();");

        var cleanupStart = loop.IndexOf(
            "// Cleanup could not be recorded, so do not submit a list with uncertain state.",
            StringComparison.Ordinal);
        Assert.True(cleanupStart >= 0);
        var cleanupFailure = loop[cleanupStart..];
        SourceContract.AssertOrder(
            cleanupFailure,
            "target.DiscardWaterOpaqueSnapshotPreparation();",
            "recorder.AbortFrame();");
    }

    private static string Extract(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing start marker `{startMarker}`.");
        Assert.True(end > start, $"Missing end marker `{endMarker}` after `{startMarker}`.");
        return source[start..end];
    }

    private static string ReadSurface(string fileName) =>
        SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Gpu", "D3D12",
            fileName);

    private static string ReadAppSource(string fileName) =>
        SourceContract.ReadSource(
            "src", "BethesdaMultitool", "App", "Controls",
            fileName);
}
