using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Water;

/// <summary>Source contracts for the Windows-only PSO/split-draw and one-shot snapshot plumbing.</summary>
public sealed class FnvWater001SourceContractTests
{
    [Fact]
    public void RendererCompilesDedicatedDepthSamplePermutationAndDisposesIt()
    {
        var source = ReadRenderer();

        Assert.Contains("\"water_fnv001.frag.hlsl\", \"main\", \"ps_5_1\"", source, StringComparison.Ordinal);
        Assert.Contains("_psoFnvWater001DepthSample = gpu.Device.CreateGraphicsPipelineState(psoDesc);",
            source, StringComparison.Ordinal);
        Assert.Contains("_psoFnvWater001DepthSample.Dispose();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_psoFnvWater001 =", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EligibleDrawSplitsGeneratedCellsPerWaterTypeAndKeepsPlacedNifsOnWater003()
    {
        var source = ReadRenderer();
        // The per-batch draw body lives in DrawFnvWaterBatch (shared by the legacy inline loop and
        // the unified-transparency drain); the batch iteration lives in RenderFnvWaterMaterialBatches
        // just after it. Extract from the shared body through the legacy loop so the contract covers
        // both halves of the route.
        var route = Extract(source,
            "private unsafe bool DrawFnvWaterBatch(",
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
            "else if (args.DepthSample)", "pso = _psoDepthSample;", "cmd.SetPipelineState(pso);");
        Assert.Contains("water001: false", route, StringComparison.Ordinal);
        // The batch slice reaches the VS through the per-draw SRV window, never through
        // StartInstanceLocation (D3D12 SV_InstanceID excludes it — see
        // WaterBatchInstanceWindowSourceContractTests). The window's base adds the recorder's
        // frame slot because the instance buffer is frame-slotted (FramesInFlight regions).
        Assert.Contains(
            "((ulong)_recorder.FrameIndex * (ulong)_instanceCapacity) + (ulong)startInstance",
            route,
            StringComparison.Ordinal);
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

        var host = SourceContract.ReadAppSource("WorldView3DControl.Cells.cs");
        Assert.Contains("_water?.SetFnvWaterMaterialCatalog(ResolveFnvWaterMaterialCatalog());",
            host, StringComparison.Ordinal);
        Assert.Contains("foreach (var (formId, water) in _data.WatersByFormId)", host,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CSharpAndHlslAppendTheSameTwoWater001Registers()
    {
        var renderer = ReadRenderer();
        // The Uniforms cbuffer (with the appended WATER001 tail) lives in the shared header every
        // per-game water shader includes, not in the WATER001 program file itself.
        var shader = SourceContract.ReadShaderSource("water_common.hlsli");

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

        // The per-batch prepass call lives in DrawFnvWaterBatch (shared by the legacy loop and the
        // unified-transparency drain); the frame context rides FnvBatchDrawArgs.
        var batched = Extract(source,
            "private unsafe bool DrawFnvWaterBatch(",
            "private unsafe void UploadInstances(");
        Assert.Contains(
            "cmd, args.FrameIndex, args.ElapsedSeconds, noiseIndex, surface",
            batched,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ShaderKeepsRecoveredDepthProjectionAndLocalFallbackContracts()
    {
        // water_fnv001.frag.hlsl IS the WATER001 program (the old #if FNV_WATER001 arm, now its
        // own per-game file), so the whole file carries the recovered contracts.
        var water001 = ReadShader();

        Assert.Contains("sceneDistance / waterDistance", water001, StringComparison.Ordinal);
        // Depth-writer channels normalized by the ACTIVE above-water FogFar (DNAM@36): the engine
        // switches to the UnderWater fog trio only when the camera itself is submerged, which this
        // route never renders. Normalizing by UnderwaterFogFar was the "still opaque" bug.
        Assert.Contains("length(scenePoint - input.vWorldPos) / aboveFogFar", water001,
            StringComparison.Ordinal);
        Assert.Contains("dot(input.vWorldPos - scenePoint, float3(0.0, 0.0, 1.0)) / aboveFogFar",
            water001, StringComparison.Ordinal);
        Assert.Contains("float aboveFogFar = max(uLegacySurface1.y, aboveFogNear + 1.0);", water001,
            StringComparison.Ordinal);
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
        // The fail-closed guard now validates the occluder gap: FnvWater003LocalFallback receives
        // depthT/corrD already resolved, so the raw gap it still takes is purely occlusion input
        // and must stay the UNFILTERED nearest sample (see LoadSceneDepth in water_common.hlsli).
        Assert.Contains("!isfinite(occluderGap) || !isfinite(depthT)", water001,
            StringComparison.Ordinal);
        Assert.Contains("clip(-1.0);", water001, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The below-water split must run BEFORE the WATER001 snapshot decision and must not depend on
    ///     it. It was previously nested inside a positive preflight, which declines on
    ///     <c>MixedVisiblePlaneHeights</c> — true in essentially every exterior view — so submerged
    ///     alpha-blend decals were composited over the water surface instead of being refracted by it.
    ///     Water draws with <c>DepthWriteMask.Zero</c>, so draw order is the only thing that submerges
    ///     them. The live and capture routes must partition identically or a capture cannot verify the
    ///     live frame.
    /// </summary>
    [Fact]
    public void LiveAndCaptureRoutesPutWhollyUnderwaterBlendsIntoRefractionSnapshot()
    {
        var live = SourceContract.ReadAppSource("WorldView3DControl.Frame.cs");
        SourceContract.AssertOrder(
            live,
            "_water.HasVisibleWaterToPartition(cylinder)",
            "_references.RenderBlendedDeferredBelowWater(_water, _camera.Position.Z);",
            "waterTransparencyPartitioned = true;",
            "_water.GetFnvWater001Preflight(",
            "surface.TryPrepareWaterOpaqueSnapshot(cmd)",
            "_water?.Render(",
            "_references?.RenderBlendedDeferredAtOrAboveWater(_water, _camera.Position.Z);");
        // The split no longer unwinds when the snapshot fails: redrawing the whole list post-water
        // would double-blend the submerged half and restore the bug.
        Assert.DoesNotContain("waterTransparencyPartitioned = belowWaterTransparencyDrawn", live,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            live,
            "if (waterTransparencyPartitioned && _water is not null)",
            "_references?.RenderBlendedDeferredAtOrAboveWater(_water, _camera.Position.Z);",
            "else",
            "_references?.RenderBlendedDeferred();");
        // The split is game-agnostic: every title draws water with DepthWriteMask.Zero, and the
        // classification is data-driven from authored per-cell water heights.
        Assert.DoesNotContain("partitionGame is Core.Games.BethesdaGame.FalloutNewVegas", live,
            StringComparison.Ordinal);
        // The water pass's protected ring reservation must still be HELD across the split and
        // released only afterwards. Releasing it first (so the split could not truncate a submerged
        // draw) let the below-water leg consume the water budget on dense frames and water dropped
        // out of individual frames — the ring-starvation flicker this reservation exists to prevent.
        SourceContract.AssertOrder(
            live,
            "_references.RenderBlendedDeferredBelowWater(_water, _camera.Position.Z);",
            "_ringBuffer12!.ReleaseTailReservation(WaterPassRingReservationBytes);",
            "_water?.Render(");

        var capture = SourceContract.ReadAppSource("WorldView3DControl.SceneCapture.cs");
        SourceContract.AssertOrder(
            capture,
            "_water!.HasVisibleWaterToPartition(cylinder)",
            "_references!.RenderBlendedDeferredBelowWater(_water, _camera.Position.Z);",
            "captureWaterTransparencyPartitioned = true;",
            "_water!.GetFnvWater001Preflight(",
            "target.TryPrepareWaterOpaqueSnapshot(cmd)",
            "_water!.Render(viewProj, cylinder, captureRenderOrigin)",
            "_references!.RenderBlendedDeferredAtOrAboveWater(_water!, _camera.Position.Z);");
        SourceContract.AssertOrder(
            capture,
            "if (captureWaterTransparencyPartitioned)",
            "_references!.RenderBlendedDeferredAtOrAboveWater(_water!, _camera.Position.Z);",
            "else",
            "_references!.RenderBlendedDeferred();");

        var references = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "ReferenceRenderer12.cs");
        Assert.Contains("WaterTransparencyPartition.IsWhollyBelow(", references,
            StringComparison.Ordinal);
        Assert.Contains("DeferredWaterPartition.NotWhollyBelow", references,
            StringComparison.Ordinal);
        // Classification is by the geometry's world-space TOP, not by a bounding-sphere apex: FNV's
        // flat decal cards carry 70-125-unit NiBound radii and never qualified as submerged.
        Assert.Contains("draw.WorldBoundsMaxZ", references, StringComparison.Ordinal);
        // ...and against the surface LOCAL to the draw's own XY.
        Assert.Contains("draw.WorldBoundsCenterXY.X", references, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The unified transparency stream is the DEFAULT path on FNV water frames, and the pins
    ///     above now cover only the kill-switch-off legacy branch (<c>AssertOrder</c> scans
    ///     forward, and the legacy else-branch sits after the stream branch in both files). The
    ///     stream sequence therefore needs its own pins — in BOTH hosts. The first A/B of the
    ///     stream was invalidated by exactly this gap: the capture path had no stream branch, and
    ///     it inherited the live loop's sticky <c>_transparencyStreamActive</c>, so "stream on"
    ///     captures rendered a hybrid that was neither ordering.
    /// </summary>
    [Fact]
    public void UnifiedStreamSequenceIsPinnedInBothHosts()
    {
        var live = SourceContract.ReadAppSource("WorldView3DControl.Frame.cs");
        // The routing flag is per-frame HOST state decided before the reference render.
        SourceContract.AssertOrder(
            live,
            "var streamTransparency =",
            "_water.CanStreamTransparency(cylinder);",
            "_references?.SetTransparencyStreamActive(streamTransparency);",
            "if (streamTransparency)");
        // Stream order: depth snapshot → WATER001 decision → reservation HANDOFF (never an early
        // release — the blended capacity plan would spend the water budget on far blended draws
        // and dense frames would drop water wholesale, the 07-24 ring-starvation flicker) →
        // queue → one merged pass → drain → finish → snapshot restores.
        SourceContract.AssertOrder(
            live,
            "if (streamTransparency)",
            "surface.TryPrepareOpaqueDepthSnapshot(cmd)",
            "_water!.SetSceneDepth(",
            "_water.GetFnvWater001Preflight(",
            "_water.DeferStreamTailReservationRelease(WaterPassRingReservationBytes);",
            // Submerged translucent geometry draws BEFORE the stream opens. Water is queued per
            // CELL, so its sort key is a 4096-unit quad's centroid and a decal in the near half of
            // its own cell sorts nearer than the surface above it — the global depth merge cannot
            // express "is under the water", so the partition has to stay a partition.
            "_references!.RenderBlendedDeferredBelowWater(",
            "_water.PrepareTransparencyStream(",
            // Null-conditional in the LIVE host (the capture host above asserts with `!`): the
            // reference pipeline can fail to initialize and the live view stays up reporting
            // PLACED OBJECTS UNAVAILABLE, so this call site is reachable with no reference
            // renderer. The drain/finish pins below still hold — water renders either way.
            "_references?.RenderBlendedDeferredUnified(",
            "streamWaterProbe",
            "_water.DrainAllTransparency();",
            // The above-all-water partition draws AFTER the final drain: no queued surface can
            // occlude those draws, and water writes no depth, so a surface issued after them (the
            // camera-cell quad's centroid sorts nearer than almost everything) would composite on
            // top — the "cell water renders over smoke standing above it" defect. It must run
            // before FinishTransparencyStream, which is allowed to reset the frame's queued-plane
            // state.
            "_references?.RenderBlendedDeferredAboveAllWater(",
            "_water.FinishTransparencyStream();",
            "surface.RestoreWaterOpaqueSnapshot(cmd);",
            "surface.RestoreOpaqueDepthSnapshot(cmd);");

        // The capture decides the routing flag from ITS OWN state (never inherits the live
        // frame's), requires the post-opaque depth COPY (the stream keeps the DSV writable, so
        // the raw-depth binding is illegal), and renders the same stream sequence.
        var capture = SourceContract.ReadAppSource("WorldView3DControl.SceneCapture.cs");
        SourceContract.AssertOrder(
            capture,
            "var captureStreamTransparency =",
            "_references?.SetTransparencyStreamActive(captureStreamTransparency);",
            "TryEnsureCaptureDepthSrv(target, requireSnapshotCopy: captureStreamTransparency);",
            "target.TryRecordDepthMaxResolve(cmd);",
            "if (captureStreamTransparency)",
            "_water.DeferStreamTailReservationRelease(WaterPassRingReservationBytes);",
            // Same submerged-first partition as the live route — a capture that ordered
            // translucent geometry around water differently would misreport the very bug it
            // verifies.
            "_references!.RenderBlendedDeferredBelowWater(",
            "_water.PrepareTransparencyStream(",
            "_references!.RenderBlendedDeferredUnified(",
            "captureWaterProbe",
            "_water.DrainAllTransparency();",
            // Same above-all-water tail pass as the live host — a capture that let the camera-cell
            // water quad composite over above-surface smoke would misreport the very bug it
            // verifies.
            "_references!.RenderBlendedDeferredAboveAllWater(",
            "_water.FinishTransparencyStream();",
            "target.RestoreWaterOpaqueSnapshot(cmd);");

        // The 3D export renders the legacy split by design and must pin the shared renderer's
        // routing flag to it.
        var export = SourceContract.ReadAppSource("WorldView3DControl.Export3D.cs");
        Assert.Contains("_references.SetTransparencyStreamActive(false);", export,
            StringComparison.Ordinal);

        // The legacy split entry points must pass the queued-water plane EXPLICITLY as NaN:
        // IsWhollyAboveAllWater(NaN) is pinned false, so the above-all-water class stays EMPTY on
        // the legacy path and NotWhollyBelow keeps its full legacy meaning. Before this pin the
        // inertness depended on a silent parameter default propagating through two call layers —
        // a finite plane added later would make the legacy path DROP its wholly-above draws
        // (they are only issued by the stream hosts' post-drain pass).
        var legacySplitSource = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "ReferenceRenderer12.cs");
        SourceContract.AssertOrder(
            legacySplitSource,
            "public void RenderBlendedDeferredBelowWater(",
            "maxQueuedWaterHeight: float.NaN);",
            "public void RenderBlendedDeferredAtOrAboveWater(",
            "maxQueuedWaterHeight: float.NaN);");

        // Water opens the handed-off reservation lazily: after the blended capacity plan (first
        // drained run) and unconditionally at finish/abandon.
        var water = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "WaterRenderer12.cs");
        SourceContract.AssertOrder(
            water,
            "private bool DrawNextStreamRun(float depth)",
            "ReleaseStreamTailReservation();",
            "DrawFnvWaterBatch(");
        SourceContract.AssertOrder(
            water,
            "public int FinishTransparencyStream()",
            "ReleaseStreamTailReservation();",
            "LastStats.WaterDraws = waterDraws;");
        Assert.Contains("public void AbandonTransparencyStream()", water, StringComparison.Ordinal);

        // Stream sort granularity is PER CELL / per placed-NIF geometry (the engine's per-shape
        // alpha-pass keys) — a per-material key (= the batch's farthest cell) drained whole lakes
        // before nearer blended draws and submerged decals composited ON TOP of the surface. The
        // drain coalesces contiguous same-material entries so draw counts stay at batch scale.
        SourceContract.AssertOrder(
            water,
            "foreach (var batch in _fnvWaterCellDrawBatches)",
            "_fnvWaterSortScratch[instance].Depth",
            "NifViewDepth(geometry), geometry.WaterFormId",
            "_streamPending.Sort(");
        Assert.Contains("next.StartInstance != first.StartInstance + count", water,
            StringComparison.Ordinal);

        // Frozen batch lists route depth-writing blends at BUILD time, so a routing-flag flip
        // must veto reuse — otherwise the hoist replays an empty list (or the stream never sees
        // the depth-writing draws) for up to the reuse ceiling.
        var references = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "ReferenceRenderer12.cs");
        SourceContract.AssertOrder(
            references,
            "if (_lastBuildStreamActive != _transparencyStreamActive)",
            "return BatchReuseBlocker.StreamRouting;");
        // Publication records the routing mode captured when the staged build began. Reading the
        // live flag here would certify a list that may have been built for the opposite route.
        Assert.Contains("_lastBuildStreamActive = state.StreamActive;", references,
            StringComparison.Ordinal);
    }

    /// <summary>
    ///     The split must be classified per draw against the water surface local to its own XY, never
    ///     against one global plane. The previous design took the MAXIMUM height over every gathered
    ///     water cell — a radius spanning the whole render distance with no frustum or Z bound — and
    ///     then refused to split unless the camera was above that maximum. Measured at Lake Mead: the
    ///     local surface is 3000 but a distant body pushed the max to 5600, so a camera at Z≈3200 was
    ///     treated as submerged and the split silently did nothing.
    /// </summary>
    [Fact]
    public void BelowWaterPartitionUsesAPerDrawLocalSurfaceNotAGlobalPlane()
    {
        var water = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "WaterRenderer12.cs");
        Assert.Contains("public bool TryGetWaterHeightAt(", water, StringComparison.Ordinal);
        Assert.Contains("public bool HasVisibleWaterToPartition(", water, StringComparison.Ordinal);
        // The global-maximum plane and its camera-above-plane guard are gone for good.
        Assert.DoesNotContain("TryGetBelowWaterPartitionPlane", water, StringComparison.Ordinal);
        Assert.DoesNotContain("if (cylinder.Position.Z <= maxHeight) return false;", water,
            StringComparison.Ordinal);

        var partition = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Water",
            "WaterTransparencyPartition.cs");
        Assert.Contains("probe.TryGetWaterHeightAt(worldX, worldY, out var surface)", partition,
            StringComparison.Ordinal);
        // The camera test is per water body, so a camera submerged in one does not flip another.
        Assert.Contains("worldMaxZ < surface && cameraZ > surface", partition, StringComparison.Ordinal);

        var live = SourceContract.ReadAppSource("WorldView3DControl.Frame.cs");
        Assert.DoesNotContain("partitionedWaterPlaneHeight", live, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Partition telemetry must be cleared on any frame that does not split, so a zero always
    ///     means "not splitting". Sticky counters previously kept reporting a stale non-zero count
    ///     from an earlier pose, which disguised the split being disabled outdoors for a whole round.
    /// </summary>
    [Fact]
    public void PartitionTelemetryIsResetWhenNoSplitHappens()
    {
        var references = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "ReferenceRenderer12.cs");
        SourceContract.AssertOrder(
            references,
            "public void RenderBlendedDeferred()",
            "ResetBelowWaterPartitionTelemetry();",
            "RenderBlendedDeferredCore(DeferredWaterPartition.All,");
    }

    /// <summary>
    ///     The offscreen export and headless profiler paths must partition too. They previously drew
    ///     every blended submesh after water unconditionally, so an exported render of an underwater
    ///     scene kept the bug even once the live viewer was fixed.
    /// </summary>
    [Fact]
    public void ExportAndHeadlessPathsAlsoPartitionAroundWater()
    {
        var export = SourceContract.ReadAppSource("WorldView3DControl.Export3D.cs");
        SourceContract.AssertOrder(
            export,
            "_water.HasVisibleWaterToPartition(cylinder)",
            "_references.RenderBlendedDeferredBelowWater(_water, _camera.Position.Z);",
            // Pinned clock (static-export ruling 2026-08-10); the ORDER around water is the contract.
            "_water.RenderAtTime(viewProj, cylinder, default, 0f, isPerspectiveProjection: false);",
            "_references.RenderBlendedDeferredAtOrAboveWater(_water, _camera.Position.Z);");

        var headless = SourceContract.ReadSource(
            "src", "BethesdaRendererProfiler", "NifHeadlessRenderer.cs");
        Assert.Contains("references.RenderBlendedDeferredBelowWater(water, cameraPosition.Z);",
            headless, StringComparison.Ordinal);
        Assert.Contains("references.RenderBlendedDeferredAtOrAboveWater(water, cameraPosition.Z);",
            headless, StringComparison.Ordinal);
    }

    private static string ReadRenderer()
    {
        return SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "WaterRenderer12.cs");
    }

    /// <summary>
    ///     The offscreen capture must be the SAME renderer as the live view, not a lookalike. It is
    ///     the instrument users are asked to aim with a camera pose, so any pass the live frame runs
    ///     and the capture skips turns a reproducible report into an unreproducible one. Two such
    ///     divergences were found this way and are pinned here:
    ///     <list type="bullet">
    ///         <item>
    ///             the planar sky reflection, which the capture cleared and replaced with the
    ///             shader's 2-row gradient stand-in;
    ///         </item>
    ///         <item>
    ///             camera-relative scene rendering, which the capture hardcoded to absolute — the
    ///             very path whose float32 precision camera-relative exists to fix, so captures far from
    ///             the world origin exercised a strictly worse renderer.
    ///         </item>
    ///     </list>
    /// </summary>
    [Fact]
    public void CaptureRendersTheSameSceneTransformsAndReflectionAsTheLiveFrame()
    {
        var capture = SourceContract.ReadAppSource("WorldView3DControl.SceneCapture.cs");
        var live = SourceContract.ReadAppSource("WorldView3DControl.Frame.cs");

        // ONE definition of the camera-relative predicate, so the two paths cannot drift again.
        Assert.Contains("private static bool CameraRelativeSceneRendering", live, StringComparison.Ordinal);
        Assert.Contains("CameraRelativeSceneRendering", capture, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "EnvironmentVariables.Viewer.CameraRelative), \"0\"", capture, StringComparison.Ordinal);

        // Capture builds the same trio the live frame does: a camera-relative scene matrix around a
        // SNAPPED origin, an absolute matrix for culling (cull geometry is absolute world space),
        // and a translation-free sky matrix for the camera-centred dome.
        SourceContract.AssertOrder(
            capture,
            "var viewProjAbsolute = _camera.GetViewMatrix() * proj;",
            "SnapToRenderOriginGrid(_camera.Position)",
            "_camera.GetViewMatrixRelativeTo(captureRenderOrigin) * proj",
            "var viewProjSky = _camera.GetViewMatrixCameraRelative() * proj;");
        Assert.Contains("cullViewProj: viewProjAbsolute", capture, StringComparison.Ordinal);
        Assert.Contains("renderOrigin: captureRenderOrigin", capture, StringComparison.Ordinal);
        Assert.Contains("cameraRelative: captureCameraRelative", capture, StringComparison.Ordinal);
        // Every world-space consumer takes the SAME origin — a Vector3.Zero here would silently put
        // one pass back in absolute space while the rest stayed relative.
        SourceContract.AssertOrder(
            capture,
            "_water.PrepareTransparencyStream(",
            "viewProj, cylinder, captureRenderOrigin, isPerspectiveProjection: true");
        Assert.Contains("RecordSunShadowPass(cmd, captureRenderOrigin", capture, StringComparison.Ordinal);
        Assert.Contains(
            "_water!.Render(viewProj, cylinder, captureRenderOrigin)",
            capture, StringComparison.Ordinal);

        // The planar reflection is rendered BY the capture, at the capture's own extent, and the
        // result is reported so a run can never silently fall back to the gradient unnoticed.
        // The sky-only body is skipped only when the Oblivion scene mirror will overwrite the
        // target the same frame (captureSceneMirrorPlanned) — the same rule as the live frame.
        SourceContract.AssertOrder(
            capture,
            "RenderSky(viewProjSky, Vector3.Zero",
            "_captureWaterReflectionBound = !captureSceneMirrorPlanned &&",
            "TryRenderCaptureWaterReflection(cmd, viewProjSky, sceneSkyScale,",
            "_water?.SetWaterReflection(",
            "(uint)target.Width,");
        // The reported field is DERIVED from the bound flag, and the unbound arm must resolve to the
        // gradient stand-in — labelling a fallback run "planar-rt-*" would make a capture that never
        // rendered a reflection indistinguishable from one that did.
        SourceContract.AssertOrder(
            capture,
            "if (!_captureWaterReflectionBound)",
            "waterReflectionSource = \"gradient-standin\";",
            "\"planar-rt-scene\" : \"planar-rt-sky\"",
            "fields[\"waterReflection\"] = waterReflectionSource;");
        // Same mirror construction as the live route (translation-free dome ⇒ a plain Z flip).
        Assert.Contains(
            "RenderSky(Matrix4x4.CreateScale(1f, 1f, -1f) * viewProjSky, Vector3.Zero",
            capture, StringComparison.Ordinal);
        Assert.Contains(
            "RenderSky(Matrix4x4.CreateScale(1f, 1f, -1f) * viewProjSky, Vector3.Zero",
            live, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The 2D map's top-down overlay must UNBIND the planar sky reflection before drawing water.
    ///     <para>
    ///         <c>WaterRenderer12.SetWaterReflection</c> state PERSISTS across frames — both the
    ///         bindless index and the scene width/height the shader divides <c>SV_Position</c> by are
    ///         owned by the live frame path. The reflection lookup itself
    ///         (<c>water_common.hlsli</c>'s <c>SampleSkyReflection</c>) is SCREEN-SPACE, so it is only
    ///         valid for the camera that rendered the target. The overlay renders through the same
    ///         D3D12 stack under an ORTHOGRAPHIC top-down projection, so leaving the live window's
    ///         binding in place stretched its mirrored sky affinely across whatever world rectangle the
    ///         map was showing — the reflection's world-space feature size tracked 1/zoom, and the
    ///         width mismatch pushed most of the map into the target's clamped edge texel.
    ///     </para>
    ///     <para>
    ///         Unlike the capture path this must NOT render its own reflection: under a parallel
    ///         top-down view a planar mirror reflects the zenith for every pixel, so a mirrored-camera
    ///         target is degenerate. Unbinding restores the direction-based gradient, which depends only
    ///         on the world normal and view vector and is therefore zoom-invariant.
    ///     </para>
    /// </summary>
    [Fact]
    public void TopDownOverlayUnbindsTheLivePlanarReflectionBeforeDrawingWater()
    {
        var topDown = SourceContract.ReadAppSource("WorldView3DControl.TopDown.cs");

        SourceContract.AssertOrder(
            topDown,
            "_water.SetNifWaterPlanes(",
            "_water.SetWaterReflection(null, 0, 0)",
            // Pinned clock (static-2D-map ruling 2026-08-10); the unbind-before-draw ORDER is the contract.
            "_water.RenderAtTime(viewProj, cylinder, default, 0f");

        // Nobody may "fix" this later by binding the live SRV with corrected dimensions: the target
        // itself is the wrong image for a parallel projection, not merely the wrong scale.
        Assert.DoesNotContain(
            "TryRenderCaptureWaterReflection", topDown, StringComparison.Ordinal);
    }

    private static string ReadShader()
    {
        return SourceContract.ReadShaderSource("water_fnv001.frag.hlsl");
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
