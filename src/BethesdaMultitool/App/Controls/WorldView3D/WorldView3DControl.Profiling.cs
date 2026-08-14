using System.Diagnostics;
using System.Globalization;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Profiling;

namespace BethesdaMultitool;

public sealed partial class WorldView3DControl
{
    private void EmitCompletedGpuFrames()
    {
        if (_gpuTimestampProfiler12 is null || !RendererProfilerTrace.IsEnabled)
        {
            return;
        }

        while (_gpuTimestampProfiler12.TryCollectCompleted(out var timings))
        {
            var fields = new Dictionary<string, object?>
            {
                ["frame"] = timings.FrameNumber,
                ["gpuFrameMs"] = timings.FrameMilliseconds,
                ["gpuTerrainMs"] = timings.TerrainMilliseconds,
                ["gpuReferencesMs"] = timings.ReferencesMilliseconds,
                ["gpuWaterMs"] = timings.WaterMilliseconds,
                ["gpuWireframeMs"] = timings.WireframeMilliseconds,
                ["gpuSkyMs"] = timings.SkyMilliseconds,
                ["gpuBlendedMs"] = timings.BlendedMilliseconds,
                ["gpuShadowMs"] = timings.ShadowMilliseconds,
                ["gpuResolveMs"] = timings.ResolveMilliseconds,
                // The residual. If this is not ~0 the frame still contains unmeasured GPU work — the
                // exact condition that hid a 43 ms shadow pass behind an otherwise plausible profile.
                ["gpuOtherMs"] = timings.OtherMilliseconds
            };
            for (var i = 0; i < Core.Formats.Nif.Rendering.D3D12.ShadowMapRenderer12.CascadeCount; i++)
            {
                var cascade = timings.Cascade(i);
                fields[$"gpuShadowC{i}RefMs"] = cascade.ReferencesMilliseconds;
                fields[$"gpuShadowC{i}TerrainMs"] = cascade.TerrainMilliseconds;
            }

            RendererProfilerTrace.Event("gpu-frame", fields);
        }
    }

    private void EmitFrameStall(
        FrameProfileSample sample,
        WorldRenderStats? terrain,
        WorldRenderStats? water,
        WorldRenderStats? wireframe,
        WorldRenderStats? references)
    {
        Log.Warn(
            "3D stall frame={0} total={1:0.00}ms threshold={2:0.00}ms terrain={3:0.00}ms refs={4:0.00}ms present={5:0.00}ms fence={6:0.00}ms",
            sample.FrameNumber,
            sample.TotalMilliseconds,
            _stallThresholdMilliseconds,
            sample.TerrainMilliseconds,
            sample.ReferencesMilliseconds,
            sample.PresentMilliseconds,
            sample.FenceWaitMilliseconds);

        var fields = BuildFrameFields(sample);
        fields["thresholdMs"] = _stallThresholdMilliseconds;
        AddStats(fields, "terrain.", terrain);
        AddStats(fields, "water.", water);
        AddStats(fields, "wire.", wireframe);
        AddStats(fields, "refs.", references);
        RendererProfilerTrace.Event("frame-stall", fields);
    }

    private Dictionary<string, object?> BuildFrameFields(FrameProfileSample sample)
    {
        var fields = RendererProfilerTrace.CameraPoseFields(Profiler_CameraPose);
        fields["frame"] = sample.FrameNumber;
        fields["frameMs"] = sample.TotalMilliseconds;
        fields["controllerMs"] = sample.ControllerMilliseconds;
        fields["beginFrameMs"] = sample.BeginFrameMilliseconds;
        fields["fenceWaitMs"] = sample.FenceWaitMilliseconds;
        fields["acquireMs"] = sample.AcquireMilliseconds;
        fields["clearMs"] = sample.ClearSetupMilliseconds;
        fields["cameraMs"] = sample.CameraMilliseconds;
        fields["terrainMs"] = sample.TerrainMilliseconds;
        fields["referencesMs"] = sample.ReferencesMilliseconds;
        fields["waterMs"] = sample.WaterMilliseconds;
        fields["wireframeMs"] = sample.WireframeMilliseconds;
        fields["endFrameMs"] = sample.EndFrameMilliseconds;
        fields["presentMs"] = sample.PresentMilliseconds;
        fields["hudMs"] = sample.HudMilliseconds;
        fields["visibleTerrain"] = sample.VisibleTerrain;
        fields["visibleReferences"] = sample.VisibleReferences;
        fields["visibleWater"] = sample.VisibleWater;
        fields["visibleWireframe"] = sample.VisibleWireframe;
        fields["totalCells"] = sample.TotalCells;
        fields["renderDistanceCells"] = sample.RenderDistanceCells;
        fields["viewportWidth"] = sample.ViewportWidth;
        fields["viewportHeight"] = sample.ViewportHeight;
        fields["descriptorCount"] = sample.DescriptorCount;
        fields["ringBytes"] = sample.RingBytes;
        fields["gcGen0"] = sample.GcGen0Collections;
        fields["gcGen1"] = sample.GcGen1Collections;
        fields["gcGen2"] = sample.GcGen2Collections;
        fields["managedMemoryBytes"] = sample.ManagedMemoryBytes;
        fields["allocatedBytes"] = sample.AllocatedBytes;
        // The camera's integration timestep. This is the quantity the reported motion "heartbeat" IS
        // — apparent speed is MoveSpeed x dt — yet no sink recorded it, so the symptom could only be
        // inferred from frame time. Captured before the pathological-delta clamp.
        fields["deltaSeconds"] = sample.DeltaSeconds;
        fields["shadowMode"] = _lastShadowMode.ToString();
        fields["shadowCascadeMask"] = _lastShadowCascadeMask;
        fields["shadowCapturedBatchCount"] = _lastShadowDrawCount;
        // Compatibility alias retained for existing trace consumers. This is captured work before
        // cascade filtering, not a count of issued DrawIndexedInstanced calls.
        fields["shadowDrawCount"] = _lastShadowDrawCount;
        fields["shadowTerrainCellDraws"] = _lastShadowTerrainCellDraws;
        fields["shadowReferenceDrawsByCascade"] = _lastShadowReferenceDrawsByCascade;
        fields["shadowReferenceInstancesByCascade"] = _lastShadowReferenceInstancesByCascade;
        fields["shadowTerrainCellDrawsByCascade"] = _lastShadowTerrainCellDrawsByCascade;
        fields["shadowAvailableWithoutSubmissionMask"] = _lastShadowAvailableWithoutSubmissionMask;
        return fields;
    }

    private static void AddStats(
        Dictionary<string, object?> fields,
        string prefix,
        WorldRenderStats? stats)
    {
        foreach (var item in RendererProfilerTrace.StatsFields(prefix, stats))
        {
            fields[item.Key] = item.Value;
        }
    }

    private void MaybeLogProfile(
        FrameProfileSample sample,
        WorldRenderStats? terrain,
        WorldRenderStats? water,
        WorldRenderStats? wireframe,
        WorldRenderStats? references)
    {
        if (!_profileLogging)
        {
            return;
        }

        _profileAccumulator.Add(sample, terrain, water, wireframe, references);
        if (_profileAccumulator.TryFlush(_profileLogIntervalMilliseconds, out var message, out var aggregate))
        {
            var fields = new Dictionary<string, object?>(aggregate, StringComparer.Ordinal);
            foreach (var item in RendererProfilerTrace.CameraPoseFields(Profiler_CameraPose))
            {
                fields[item.Key] = item.Value;
            }

            // Resource gauges — the trajectory of these in the last log line(s) before a crash tells
            // which resource exhausted (the usual "crash with no cause" under sustained streaming):
            //   desc → bindless descriptor heap (texture-slot leak; capacity is the hard ceiling),
            //   vramMb used/budget → GPU memory (device removal once usage exceeds budget),
            //   managedMb → managed heap OOM (retained CPU buffers).
            // Appended to BOTH the JSONL event and the plain text log line so the GUI log alone suffices.
            var resourceGauge = "";
            if (_cbvSrvUavHeap12 is not null)
            {
                fields["persistentDescriptors"] = _cbvSrvUavHeap12.PersistentCount;
                fields["persistentDescriptorCapacity"] = _cbvSrvUavHeap12.PersistentCapacity;
                resourceGauge += string.Create(CultureInfo.InvariantCulture,
                    $" desc={_cbvSrvUavHeap12.PersistentCount}/{_cbvSrvUavHeap12.PersistentCapacity}");
            }

            var managedHeapMb = GC.GetTotalMemory(false) / (1024 * 1024);
            fields["managedHeapMb"] = managedHeapMb;
            resourceGauge += string.Create(CultureInfo.InvariantCulture, $" managedMb={managedHeapMb}");

            if (_gpu12 is not null && _gpu12.TryQueryLocalVideoMemoryMb(out var vramUsedMb, out var vramBudgetMb))
            {
                fields["vramUsedMb"] = vramUsedMb;
                fields["vramBudgetMb"] = vramBudgetMb;
                resourceGauge += string.Create(CultureInfo.InvariantCulture, $" vramMb={vramUsedMb}/{vramBudgetMb}");
            }

            Log.Info(resourceGauge.Length > 0 ? message + " | res:" + resourceGauge : message);
            RendererProfilerTrace.Event("frame-aggregate", fields);
        }
    }

    // Profiler-driving surface --------------------------------------------------------------

    internal long Profiler_FrameIndex => _profileFrameIndex;

    private int BeginSceneSelection()
    {
        _sceneReadyGeneration = -1;
        return unchecked(++_sceneSelectionGeneration);
    }

    private bool IsCurrentSceneSelection(int generation) =>
        generation == _sceneSelectionGeneration;

    private void MarkSceneSelectionReady(int generation)
    {
        if (IsCurrentSceneSelection(generation))
        {
            _sceneReadyGeneration = generation;
        }
    }

    /// <summary>
    ///     Profiler hook: true once the asynchronously-selected worldspace has built its spatial
    ///     index and, for the WastelandNVHeavy stress scene, applied the deterministic bookmark.
    ///     <see cref="LoadData"/> returns before the selection handler resumes after its UI yield,
    ///     so starting a timed profile before this point races the centroid reset against the
    ///     stress bookmark and can benchmark different camera positions across identical runs.
    /// </summary>
    internal bool Profiler_IsSceneReady =>
        _spatialIndex is not null &&
        _sceneReadyGeneration == _sceneSelectionGeneration &&
        (_selectedInterior is not null || !IsWastelandNvHeavyStressScene() || _stressBookmarkApplied);

    internal RendererProfilerCameraPose Profiler_CameraPose =>
        new(_camera.Position, _camera.Yaw, _camera.Pitch, _renderDistance);

    internal WorldRenderStats? Profiler_TerrainStats => _terrain?.LastStats.Snapshot();
    internal WorldRenderStats? Profiler_ReferenceStats => _references?.LastStats.Snapshot();
    internal WorldRenderStats? Profiler_WaterStats => _water?.LastStats.Snapshot();
    internal WorldRenderStats? Profiler_WireframeStats => _cellGrid?.LastStats.Snapshot();

    /// <summary>Profiler hook: the FormId of the currently-selected exterior worldspace (null when an
    /// unlinked-exterior / interior entry is selected). Lets the capture harness round-trip the
    /// worldspace-sync parameter without switching.</summary>
    internal uint? Profiler_SelectedWorldspaceFormId =>
        _data is null ? null : GetSelectedWorldspaceFormId(_data);

    /// <summary>Profiler hook: number of exterior worldspaces (for the capture harness to iterate).</summary>
    internal int Profiler_ExteriorWorldspaceCount => _data?.Worldspaces.Count ?? 0;

    /// <summary>Profiler hook: FormId + world-space centroid (north-Y) + name of exterior worldspace
    /// <paramref name="index" />, so the capture harness can target a specific worldspace and verify
    /// the top-down sync switches to it. Null when the index is out of range or has no placed cells.</summary>
    internal (uint FormId, float CenterX, float CenterY, string Name)? Profiler_GetWorldspaceCenter(int index)
    {
        if (_data is null || index < 0 || index >= _data.Worldspaces.Count) return null;
        var ws = _data.Worldspaces[index];
        long sumX = 0, sumY = 0, n = 0;
        foreach (var c in ws.Cells)
        {
            if (c.GridX is int gx && c.GridY is int gy) { sumX += gx; sumY += gy; n++; }
        }
        if (n == 0) return null;
        var cx = (float)((sumX / (double)n + 0.5) * _data.CellWorldSize);
        var cy = (float)((sumY / (double)n + 0.5) * _data.CellWorldSize);
        return (ws.FormId, cx, cy, ws.EditorId ?? $"0x{ws.FormId:X8}");
    }

    internal void Profiler_SetCameraPose(RendererProfilerCameraPose pose)
    {
        _camera.Position = pose.Position;
        _camera.Yaw = pose.Yaw;
        _camera.Pitch = Math.Clamp(
            pose.Pitch,
            -MathF.PI * 0.5f + 0.01f,
            MathF.PI * 0.5f - 0.01f);
        SetRenderDistance(pose.RenderDistance);
    }

    /// <summary>Profiler hook: the live perspective vertical FOV in degrees, so the P-key pose can copy
    /// it and a headless <c>--capture-frame</c> reproduces the SAME zoom/framing. Without this a capture
    /// always renders at the 60° default and a non-default live FOV looked like a different angle.</summary>
    internal float Profiler_CameraFovDegrees => _camera.FovYRadians * (180f / MathF.PI);

    /// <summary>Profiler hook: apply <c>--capture-fov</c>. Clamped to the same [30,110]° range the live
    /// FOV slider enforces so a replayed pose can never exceed what the viewer itself allows.</summary>
    internal void Profiler_SetCameraFov(float degrees)
    {
        var clamped = Math.Clamp(degrees, MinFovDegrees, MaxFovDegrees);
        _camera.FovYRadians = clamped * (MathF.PI / 180f);
    }

    internal void Profiler_ClearInputState()
    {
        _controller.ClearKeys();
        _toggleKeysDown.Clear();
        _mouseDragActive = false;
    }

    private static int ParsePositiveInt(string? value, int fallback)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
               parsed > 0
            ? parsed
            : fallback;
    }

    private static double ParseNonNegativeDouble(string? value, double fallback)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
               parsed >= 0
            ? parsed
            : fallback;
    }

    private static bool ShouldUpdateLayoutValue(double current, double next)
    {
        if (double.IsNaN(current) || double.IsInfinity(current))
        {
            return true;
        }

        return Math.Abs(current - next) >= 0.5d;
    }

    private long StartProfileTimestamp() => _profileLogging ? Stopwatch.GetTimestamp() : 0;

    private static double ElapsedMilliseconds(long started) =>
        started == 0 ? 0 : Stopwatch.GetElapsedTime(started).TotalMilliseconds;
}
