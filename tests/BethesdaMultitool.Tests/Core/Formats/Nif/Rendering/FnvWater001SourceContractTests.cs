using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>Source contracts for the Windows-only PSO/split-draw and one-shot snapshot plumbing.</summary>
public sealed class FnvWater001SourceContractTests
{
    [Fact]
    public void RendererCompilesDedicatedDepthSamplePermutationAndDisposesIt()
    {
        var source = ReadRenderer();

        Assert.Contains("new ShaderMacro(\"FNV_WATER001\", \"1\")", source, StringComparison.Ordinal);
        Assert.Contains("_psoFnvWater001DepthSample = gpu.Device.CreateGraphicsPipelineState(psoDesc);",
            source, StringComparison.Ordinal);
        Assert.Contains("_psoFnvWater001DepthSample.Dispose();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_psoFnvWater001 =", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EligibleDrawSplitsGeneratedCellsPerWaterTypeAndKeepsPlacedNifsOnWater003()
    {
        var source = ReadRenderer();
        var route = Extract(source,
            "private unsafe int RenderFnvWaterMaterialBatches(",
            "private unsafe void UploadInstances(");

        SourceContract.AssertOrder(route,
            "foreach (var batch in _fnvWaterCellDrawBatches)",
            "batch.Material",
            "batch.StartInstance",
            "batch.InstanceCount");
        Assert.Contains(
            "pso = _psoFnvWater001DepthSample;",
            route,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(route,
            "var pso = _pso;", "if (water001)", "pso = _psoFnvWater001DepthSample;",
            "else if (depthSample)", "pso = _psoDepthSample;", "cmd.SetPipelineState(pso);");
        Assert.Contains("water001: false", route, StringComparison.Ordinal);
        // The batch slice reaches the VS through the per-draw SRV window, never through
        // StartInstanceLocation (D3D12 SV_InstanceID excludes it — see
        // WaterBatchInstanceWindowSourceContractTests).
        Assert.Contains("FirstElement = (ulong)startInstance", route, StringComparison.Ordinal);
    }

    [Fact]
    public void SnapshotSetterRequiresPositivePreflightAndRenderConsumesDescriptorBeforeEarlyReturns()
    {
        var source = ReadRenderer();
        var setter = Extract(source, "public void SetFnvWater001Snapshot(", "public void SetModernCubeMap(");
        Assert.Contains("if (!_fnvWater001PendingPreflight.Candidate)", setter, StringComparison.Ordinal);
        Assert.Contains("bindlessIndex == NoNormalMap", setter, StringComparison.Ordinal);
        Assert.Contains("width == 0", setter, StringComparison.Ordinal);
        Assert.Contains("height == 0", setter, StringComparison.Ordinal);

        var renderStart = source.IndexOf("private int RenderCore(", StringComparison.Ordinal);
        var emptyReturn = source.IndexOf(
            "if (_waterCells.Count == 0 && _nifWaterPlanes.Count == 0) return 0;",
            renderStart,
            StringComparison.Ordinal);
        Assert.True(renderStart >= 0 && emptyReturn > renderStart);
        var prefix = source[renderStart..emptyReturn];
        SourceContract.AssertOrder(prefix,
            "var fnvWater001Snapshot = _fnvWater001Snapshot;",
            "_fnvWater001Snapshot = default;");
    }

    [Fact]
    public void DrawTimeRecheckUsesTheActualProjectionModeAndMixedNifsAreNamed()
    {
        var source = ReadRenderer();
        var render = Extract(source, "private int RenderCore(", "private static string DescribeTechnique(");

        Assert.Contains("isPerspectiveProjection,", render, StringComparison.Ordinal);
        Assert.DoesNotContain("isPerspectiveProjection: true,", render, StringComparison.Ordinal);
        Assert.Contains(
            "$\"+FnvWater003RtFree-scene-depth-{depthRoute}-placed-nif\"",
            render,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EffectiveWaterTypeUsesCellXcwtThenWorldspaceNam2AndResolvesPerBatchMaterial()
    {
        var source = ReadRenderer();
        var route = Extract(source,
            "private uint EffectiveFnvWaterFormId(",
            "private void ClearFnvWater001TransientState(");

        Assert.Contains("water.Cell.WaterFormId is > 0", route, StringComparison.Ordinal);
        Assert.Contains("_fnvWater001WorldspaceDefaultWaterFormId ?? 0", route,
            StringComparison.Ordinal);
        Assert.Contains("_fnvWaterMaterials.TryGetValue(formId", route, StringComparison.Ordinal);
        Assert.Contains("foreach (var formId in _fnvVisibleWaterTypeScratch)", route,
            StringComparison.Ordinal);
        Assert.Contains("HasMixedWaterTypes: false", route, StringComparison.Ordinal);
        Assert.Contains("if (water.Height != planeHeight)", route, StringComparison.Ordinal);

        var host = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "App", "Controls", "WorldView3DControl.Cells.cs");
        Assert.Contains("_water?.SetFnvWaterMaterialCatalog(ResolveFnvWaterMaterialCatalog());",
            host, StringComparison.Ordinal);
        Assert.Contains("foreach (var (formId, water) in _data.WatersByFormId)", host,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CSharpAndHlslAppendTheSameTwoWater001Registers()
    {
        var renderer = ReadRenderer();
        var shader = ReadShader();

        Assert.Contains("private const uint FnvWater001UniformByteSize = 2 * 16;", renderer,
            StringComparison.Ordinal);
        Assert.Contains("public uint FnvWater001SnapshotIndex;", renderer, StringComparison.Ordinal);
        Assert.Contains("public uint FnvWater001SnapshotWidth;", renderer, StringComparison.Ordinal);
        Assert.Contains("public uint FnvWater001SnapshotHeight;", renderer, StringComparison.Ordinal);
        Assert.Contains("public float FnvWater001PlaneHeight;", renderer, StringComparison.Ordinal);
        Assert.Contains("public Vector4 FnvWater001Surface;", renderer, StringComparison.Ordinal);
        Assert.Contains("uint4 uFnvWater001Snapshot;", shader, StringComparison.Ordinal);
        Assert.Contains("float4 uFnvWater001Surface;", shader, StringComparison.Ordinal);
    }

    [Fact]
    public void TelemetryNamesTheReconstructedApproximationAndEveryFallbackReason()
    {
        var source = ReadRenderer();

        Assert.Contains(
            "$\"FnvWater001Reconstructed-opaque-snapshot-main-scene-depth-approx-{depthRoute}\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"selective-content-mask-approximated-by-main-depth\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "LastStats.WaterTelemetryUnavailableReason = LastFnvWater001Decision.ReasonCode;",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RecoveredNoisePrepassConsumesTheFrameAnimationTimeForEveryWatrBatch()
    {
        var source = ReadRenderer();
        var prepass = Extract(source,
            "private unsafe bool RecordFnvNoisePrepass(",
            "/// <summary>IWorldRenderer entry");
        Assert.Contains(
            "FnvWaterNoiseAnimation.Scroll(surface.Layer1, elapsedSeconds)",
            prepass,
            StringComparison.Ordinal);
        Assert.Contains(
            "FnvWaterNoiseAnimation.Scroll(surface.Layer2, elapsedSeconds)",
            prepass,
            StringComparison.Ordinal);
        Assert.Contains(
            "FnvWaterNoiseAnimation.Scroll(surface.Layer3, elapsedSeconds)",
            prepass,
            StringComparison.Ordinal);

        var batched = Extract(source,
            "private unsafe int RenderFnvWaterMaterialBatches(",
            "private unsafe void UploadInstances(");
        Assert.Contains(
            "cmd, frameIndex, elapsedSeconds, noiseIndex, surface",
            batched,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ShaderKeepsRecoveredDepthProjectionAndLocalFallbackContracts()
    {
        var shader = ReadShader();
        var water001 = Extract(shader, "#if FNV_WATER001", "#else");

        Assert.Contains("sceneDistance / waterDistance", water001, StringComparison.Ordinal);
        Assert.Contains("length(scenePoint - input.vWorldPos) / underwaterFogFar", water001,
            StringComparison.Ordinal);
        Assert.Contains("dot(input.vWorldPos - scenePoint, float3(0.0, 0.0, 1.0)) / underwaterFogFar",
            water001, StringComparison.Ordinal);
        Assert.Contains("saturate(lerp(float2(1.0, 1.0), rawDepth, noiseFade))", water001,
            StringComparison.Ordinal);
        Assert.Contains("rawDepth.y * depthT * distortionScale * N.xy", water001,
            StringComparison.Ordinal);
        Assert.Contains("SampleLevel(gWaterClampSampler, refractionUv, 0)", water001,
            StringComparison.Ordinal);
        Assert.Contains("refractionUv * snapshotDimensions - 0.5", water001,
            StringComparison.Ordinal);
        Assert.Contains("displacedPixelBase + int2(1, 0)", water001, StringComparison.Ordinal);
        Assert.Contains("displacedPixelBase + int2(0, 1)", water001, StringComparison.Ordinal);
        Assert.Contains("displacedPixelBase + int2(1, 1)", water001, StringComparison.Ordinal);
        Assert.Equal(5, SourceContract.CountOccurrences(water001, "FnvWater001DepthTapIsUnderwater("));
        Assert.Contains("scenePoint.z < planeHeight", water001, StringComparison.Ordinal);
        Assert.Contains("if (!displacedFootprintIsUnderwater)", water001,
            StringComparison.Ordinal);
        Assert.Contains("refractionUv = input.Position.xy / snapshotDimensions;", water001,
            StringComparison.Ordinal);
        Assert.Contains("return FnvWater003LocalFallback(", water001, StringComparison.Ordinal);
        Assert.Contains("!isfinite(column) || !isfinite(depthT)", water001,
            StringComparison.Ordinal);
        Assert.Contains("clip(-1.0);", water001, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveAndCaptureRoutesPutWhollyUnderwaterBlendsIntoRefractionSnapshot()
    {
        var live = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "App", "Controls", "WorldView3DControl.Frame.cs");
        SourceContract.AssertOrder(
            live,
            "_references.RenderBlendedDeferredBelowWater(partitionedWaterPlaneHeight);",
            "surface.TryPrepareWaterOpaqueSnapshot(cmd)",
            "waterTransparencyPartitioned = belowWaterTransparencyDrawn;",
            "_water?.Render(",
            "_references?.RenderBlendedDeferredAtOrAboveWater(partitionedWaterPlaneHeight);");
        Assert.DoesNotContain("waterTransparencyPartitioned = true", live,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            live,
            "if (waterTransparencyPartitioned)",
            "_references?.RenderBlendedDeferredAtOrAboveWater(partitionedWaterPlaneHeight);",
            "else",
            "_references?.RenderBlendedDeferred();");

        var capture = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "App", "Controls", "WorldView3DControl.SceneCapture.cs");
        SourceContract.AssertOrder(
            capture,
            "_references.RenderBlendedDeferredBelowWater(captureWaterPlaneHeight);",
            "target.TryPrepareWaterOpaqueSnapshot(cmd)",
            "captureWaterTransparencyPartitioned =",
            "_water.RenderAtTime(viewProj, cylinder, Vector3.Zero, animationTimeSeconds)",
            "_references?.RenderBlendedDeferredAtOrAboveWater(captureWaterPlaneHeight);");
        Assert.DoesNotContain("captureWaterTransparencyPartitioned = true", capture,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            capture,
            "if (captureWaterTransparencyPartitioned)",
            "_references?.RenderBlendedDeferredAtOrAboveWater(captureWaterPlaneHeight);",
            "else",
            "_references?.RenderBlendedDeferred();");

        var references = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Camera", "D3D12",
            "ReferenceRenderer12.cs");
        Assert.Contains("WaterTransparencyPartition.IsWhollyBelow(", references,
            StringComparison.Ordinal);
        Assert.Contains("DeferredWaterPartition.NotWhollyBelow", references,
            StringComparison.Ordinal);
    }

    private static string ReadRenderer()
    {
        return SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Camera", "D3D12",
            "WaterRenderer12.cs");
    }

    private static string ReadShader()
    {
        return SourceContract.ReadShaderSource("water.frag.hlsl");
    }

    private static string Extract(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing start marker `{startMarker}`.");
        Assert.True(end > start, $"Missing end marker `{endMarker}` after `{startMarker}`.");
        return source[start..end];
    }
}