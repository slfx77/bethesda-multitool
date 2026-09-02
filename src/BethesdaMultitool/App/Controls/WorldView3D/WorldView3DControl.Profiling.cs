using System.Diagnostics;
using System.Globalization;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Profiling;
using BethesdaMultitool.Core.WorldData;

namespace BethesdaMultitool;

public sealed partial class WorldView3DControl
{
    /// <summary>
    ///     Padding added around an interior's placed-object spread, as a fraction of its longer
    ///     side. Object POSITIONS are origins, not extents, so a wall panel anchored on the
    ///     bounding edge would be half outside an unpadded frame.
    /// </summary>
    private const float InteriorFrameMarginFraction = 0.12f;

    /// <summary>
    ///     Floor for the interior frame margin, in world units, so a cell whose objects are nearly
    ///     coincident still frames a non-degenerate rectangle (the ortho projection needs a
    ///     positive span) at roughly one room's worth of context.
    /// </summary>
    private const float MinInteriorFrameMargin = 256f;

    // Profiler-driving surface --------------------------------------------------------------

    internal long Profiler_FrameIndex => _profileFrameIndex;

    /// <summary>
    ///     Profiler hook: true once the asynchronously-selected worldspace has built its spatial
    ///     index and, for the WastelandNVHeavy stress scene, applied the deterministic bookmark.
    ///     <see cref="LoadData" /> returns before the selection handler resumes after its UI yield,
    ///     so starting a timed profile before this point races the centroid reset against the
    ///     stress bookmark and can benchmark different camera positions across identical runs.
    /// </summary>
    internal bool Profiler_IsSceneReady =>
        _spatialIndex is not null &&
        _sceneReadyGeneration == _sceneSelectionGeneration &&
        (_selectedInterior is not null || !IsWastelandNvHeavyStressScene() || _stressBookmarkApplied);

    internal RendererProfilerCameraPose Profiler_CameraPose =>
        new(_camera.Position, _camera.Yaw, _camera.Pitch, _renderDistance);

    /// <summary>
    ///     Exact D3D12 surface size used by scored live frames. Layout dimensions are expressed in
    ///     device-independent pixels and cannot prove that a post-profile capture used the benchmark
    ///     viewport at non-100% DPI, so the profiler reads the swap-chain surface itself.
    /// </summary>
    internal (int Width, int Height)? Profiler_ViewportPixelSize =>
        _surface12 is { Width: > 0, Height: > 0 } surface
            ? (checked((int)surface.Width), checked((int)surface.Height))
            : null;

    /// <summary>
    ///     Profiler hook: the loaded worldspace's cell-edge size in world units. The harness takes its
    ///     span/distance arguments in CELLS and previously converted them with the hard-coded
    ///     <c>WorldGridConstants.CellSize</c>, which is 40.96× too large on Starfield (100-unit cells) —
    ///     so every headless measurement was taken at a different distance than it reported. Convert
    ///     through this instead.
    /// </summary>
    internal float Profiler_CellWorldSize => _cellSize;

    internal WorldRenderStats? Profiler_TerrainStats => _terrain?.LastStats.Snapshot();
    internal WorldRenderStats? Profiler_ReferenceStats => _references?.LastStats.Snapshot();

    /// <summary>
    ///     Model paths the reference renderer could not resolve in its last pass — the identities
    ///     behind the <c>meshMissing</c> stat. Meaningful after streaming quiescence; mid-stream it
    ///     includes paths merely still decoding. Snapshot copy: the renderer clears and rebuilds the
    ///     backing list per resolve pass on its own cadence.
    /// </summary>
    internal IReadOnlyList<string> Profiler_LastMissingMeshPaths =>
        _references is { } refs ? [.. refs.LastFrameMissingMeshPaths] : [];
    internal WorldRenderStats? Profiler_WaterStats => _water?.LastStats.Snapshot();
    internal WorldRenderStats? Profiler_WireframeStats => _cellGrid?.LastStats.Snapshot();

    /// <summary>
    ///     Profiler hook: the FormId of the currently-selected exterior worldspace (null when an
    ///     unlinked-exterior / interior entry is selected). Lets the capture harness round-trip the
    ///     worldspace-sync parameter without switching.
    /// </summary>
    internal uint? Profiler_SelectedWorldspaceFormId =>
        _data is null ? null : GetSelectedWorldspaceFormId(_data);

    /// <summary>Profiler hook: number of exterior worldspaces (for the capture harness to iterate).</summary>
    internal int Profiler_ExteriorWorldspaceCount => _data?.Worldspaces.Count ?? 0;

    /// <summary>
    ///     Profiler hook: the live perspective vertical FOV in degrees, so the P-key pose can copy
    ///     it and a headless <c>--capture-frame</c> reproduces the SAME zoom/framing. Without this a capture
    ///     always renders at the 60° default and a non-default live FOV looked like a different angle.
    /// </summary>
    internal float Profiler_CameraFovDegrees => _camera.FovYRadians * (180f / MathF.PI);

    /// <summary>
    ///     Keeps cold-streaming frames out of a later settled benchmark window without pausing the
    ///     renderer that must do the warming. Called before the control is attached to the live
    ///     profiler window, so no aggregate can race ahead of the gate.
    /// </summary>
    internal void Profiler_PauseProfileWindow()
    {
        _profileWindowEnabled = false;
        _profileWindowStartFrame = long.MaxValue;
        _profileAccumulator.ResetForSceneLoad();
    }

    /// <summary>
    ///     Opens a new scored window at the next rendered frame. The completion clock itself keeps
    ///     running during warm-up, so that first admitted sample remains a true Present-to-Present
    ///     interval rather than a partial interval measured from this method call.
    /// </summary>
    internal long Profiler_BeginProfileWindow()
    {
        _profileAccumulator.ResetForSceneLoad();
        _profileWindowStartFrame = _profileFrameIndex + 1;
        _profileWindowEnabled = true;
        return _profileWindowStartFrame;
    }

    private bool IsProfileWindowFrame(long frameNumber) =>
        _profileWindowEnabled && frameNumber >= _profileWindowStartFrame;

    private void EmitCompletedGpuFrames()
    {
        if (_gpuTimestampProfiler12 is null || !RendererProfilerTrace.IsEnabled)
        {
            return;
        }

        while (_gpuTimestampProfiler12.TryCollectCompleted(out var timings))
        {
            // Always drain completed warm-up queries so the timestamp ring keeps advancing, but do
            // not let an old GPU result surface after the scored window opens a few CPU frames later.
            if (!IsProfileWindowFrame(timings.FrameNumber))
            {
                continue;
            }

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

    private void EmitCompletedReferencePipelineStatistics()
    {
        if (_gpuReferencePipelineStatistics12 is null)
        {
            return;
        }

        while (_gpuReferencePipelineStatistics12.TryCollectCompleted(out var statistics))
        {
            // Readback is delayed by the submission fence, so the producing frame number is the
            // only valid profile-window identity. Always drain warm-up results; never relabel one
            // as the later CPU frame on which it became readable.
            if (!RendererProfilerTrace.IsEnabled || !IsProfileWindowFrame(statistics.FrameNumber))
            {
                continue;
            }

            RendererProfilerTrace.Event(
                "gpu-reference-pipeline-statistics",
                new Dictionary<string, object?>
                {
                    ["frame"] = statistics.FrameNumber,
                    ["iaPrimitives"] = statistics.IAPrimitives,
                    ["vsInvocations"] = statistics.VSInvocations,
                    ["psInvocations"] = statistics.PSInvocations
                });
        }
    }

    private void EmitFrameStall(
        FrameProfileSample sample,
        WorldRenderStats? terrain,
        WorldRenderStats? water,
        WorldRenderStats? wireframe,
        WorldRenderStats? references)
    {
        if (!IsProfileWindowFrame(sample.FrameNumber))
        {
            return;
        }

        Log.Warn(
            "3D stall frame={0} wall={1:0.00}ms threshold={2:0.00}ms terrain={3:0.00}ms refs={4:0.00}ms present={5:0.00}ms fence={6:0.00}ms body={7:0.00}ms",
            sample.FrameNumber,
            sample.TotalMilliseconds,
            _stallThresholdMilliseconds,
            sample.TerrainMilliseconds,
            sample.ReferencesMilliseconds,
            sample.PresentMilliseconds,
            sample.FenceWaitMilliseconds,
            sample.RenderBodyMilliseconds);

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
        fields["renderBodyMs"] = sample.RenderBodyMilliseconds;
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
        fields["shadowCpuMs"] = sample.ShadowMilliseconds;
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
        if (!_profileLogging || !IsProfileWindowFrame(sample.FrameNumber))
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

            // Read the frame loop's already-sampled reading rather than issuing a second DXGI query
            // here — and report the SIGNAL alongside the raw numbers, because the zone is what any
            // future decision is made from and a capture that showed only the instantaneous usage
            // could not explain why a decision was or was not taken.
            var videoMemory = _gpu12?.VideoMemory;
            if (videoMemory is { Supported: true })
            {
                var reading = videoMemory.Local.LastReading;
                var vramUsedMb = reading.CurrentUsageBytes / (1024 * 1024);
                var vramBudgetMb = reading.BudgetBytes / (1024 * 1024);
                fields["vramUsedMb"] = vramUsedMb;
                fields["vramBudgetMb"] = vramBudgetMb;
                fields["vramPeakUsedMb"] = videoMemory.Local.PeakUsageBytes / (1024 * 1024);
                fields["vramZone"] = videoMemory.Local.Zone.ToString();
                fields["vramUsageFraction"] = videoMemory.Local.UsageFraction;
                fields["vramNonLocalZone"] = videoMemory.NonLocal.Zone.ToString();
                fields["vramPollMicroseconds"] = videoMemory.LastPollMicroseconds;
                fields["vramBudgetChanges"] = videoMemory.BudgetChangeCount;
                resourceGauge += string.Create(CultureInfo.InvariantCulture,
                    $" vramMb={vramUsedMb}/{vramBudgetMb} vram={videoMemory.Local.Zone}");
            }

            Log.Info(resourceGauge.Length > 0 ? message + " | res:" + resourceGauge : message);
            RendererProfilerTrace.Event("frame-aggregate", fields);
        }
    }

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
    ///     Profiler hook: FormId + world-space centroid (north-Y) + name of exterior worldspace
    ///     <paramref name="index" />, so the capture harness can target a specific worldspace and verify
    ///     the top-down sync switches to it. Null when the index is out of range or has no placed cells.
    /// </summary>
    internal (uint FormId, float CenterX, float CenterY, string Name)? Profiler_GetWorldspaceCenter(int index)
    {
        if (_data is null || index < 0 || index >= _data.Worldspaces.Count) return null;
        var ws = _data.Worldspaces[index];
        long sumX = 0, sumY = 0, n = 0;
        foreach (var c in ws.Cells)
        {
            if (c.GridX is int gx && c.GridY is int gy)
            {
                sumX += gx;
                sumY += gy;
                n++;
            }
        }

        if (n == 0) return null;
        var cx = (float)((sumX / (double)n + 0.5) * _data.CellWorldSize);
        var cy = (float)((sumY / (double)n + 0.5) * _data.CellWorldSize);
        return (ws.FormId, cx, cy, ws.EditorId ?? $"0x{ws.FormId:X8}");
    }

    /// <summary>
    ///     Profiler hook: index of the exterior worldspace whose EditorID is <paramref name="name" />,
    ///     in the same index space <see cref="Profiler_GetWorldspaceCenter" /> uses. Null when no
    ///     worldspace matches.
    ///     <para>
    ///         The top-down capture path targets a worldspace by INDEX, which is unusable for a game
    ///         that ships hundreds of them (Starfield's main master alone has 433) — an index is
    ///         load-order-dependent and silently captures the wrong world when it shifts. This lets the
    ///         harness resolve the same name the lit-frame path already accepts.
    ///     </para>
    /// </summary>
    internal int? Profiler_TryFindWorldspaceIndexByName(string name)
    {
        if (_data is null || string.IsNullOrWhiteSpace(name)) return null;
        for (var i = 0; i < _data.Worldspaces.Count; i++)
        {
            if (string.Equals(_data.Worldspaces[i].EditorId, name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>
    ///     Profiler hook: every scene in the loaded source that can be captured top-down — each
    ///     exterior worldspace, the unlinked-exterior set, and each interior cell — with the world
    ///     rectangle that frames it. Feeds the batch capture harness so a corpus run enumerates
    ///     subjects from the loaded data instead of being handed a list.
    ///     <para>
    ///         Subjects with nothing to draw are omitted: an exterior needs at least one cell with
    ///         grid coordinates, an interior at least one placed object (its extent is DERIVED from
    ///         those positions — interiors have no grid). Memory dumps routinely hold cell records
    ///         whose placements were never captured, and rendering those produces an empty PNG that
    ///         reads as a rendering failure rather than as absent data.
    ///     </para>
    ///     <para>
    ///         Ordering is exteriors (largest cell count first, so the big worldspaces are captured
    ///         while the process is youngest) then interiors by name, for stable, resumable runs.
    ///     </para>
    /// </summary>
    internal IReadOnlyList<TopDownCaptureSubject> Profiler_EnumerateTopDownSubjects()
    {
        if (_data is null) return [];

        var cellSize = _data.CellWorldSize;
        var exteriors = new List<TopDownCaptureSubject>();
        var interiors = new List<TopDownCaptureSubject>();

        foreach (var ws in _data.Worldspaces)
        {
            if (TryFrameCellGrid(ws.Cells, cellSize, TopDownSubjectKind.Worldspace, ws.FormId,
                    ws.EditorId ?? ws.FullName ?? $"0x{ws.FormId:X8}") is { } subject)
            {
                exteriors.Add(subject);
            }
        }

        if (TryFrameCellGrid(_data.UnlinkedExteriorCells, cellSize, TopDownSubjectKind.UnlinkedExterior,
                0u, "UnlinkedExterior") is { } unlinked)
        {
            exteriors.Add(unlinked);
        }

        foreach (var cell in _data.InteriorCells)
        {
            if (TryFrameInterior(cell) is { } subject)
            {
                interiors.Add(subject);
            }
        }

        exteriors.Sort((a, b) => b.CellCount.CompareTo(a.CellCount));
        interiors.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        exteriors.AddRange(interiors);
        return exteriors;
    }

    /// <summary>
    ///     Frames a set of exterior cells by their grid coordinates. The rectangle spans whole cells
    ///     (max edge is the far side of the last cell, hence the +1) so nothing on a boundary cell is
    ///     clipped. Null when no cell carries grid coordinates.
    /// </summary>
    private static TopDownCaptureSubject? TryFrameCellGrid(
        IEnumerable<CellRecord> cells, float cellSize, TopDownSubjectKind kind, uint formId, string name)
    {
        var gxs = new List<float>();
        var gys = new List<float>();
        var zs = new List<float>();
        var count = 0;
        var placements = 0;
        var nonPersistent = 0;
        var terrainCells = 0;
        foreach (var c in cells)
        {
            if (c.GridX is not int gx || c.GridY is not int gy) continue;
            placements += c.PlacedObjects.Count;

            // Residency test (USER RULING): only cells the capture actually RETAINED shape the
            // subject. Persistent refs (doors, activators) exist for every cell in the file
            // whether or not it was ever resident — WastelandNV's persistent activators alone
            // stretched every frame to most of the worldspace while only small portions were
            // captured. Non-persistent refs and terrain exist only where the cell was resident.
            var cellNonPersistent = 0;
            foreach (var p in c.PlacedObjects)
            {
                if (!p.IsPersistent) cellNonPersistent++;
            }

            var hasTerrain = c.Heightmap is not null || c.RuntimeTerrainMesh is not null;
            if (hasTerrain) terrainCells++;
            nonPersistent += cellNonPersistent;
            if (cellNonPersistent == 0 && !hasTerrain) continue;

            count++;

            // One grid sample per CAPTURED ref (terrain-only cells anchor themselves once):
            // the percentile below then frames where the capture's mass actually is, instead of
            // letting a couple of stray far-flung resident cells stretch the frame across the
            // worldspace the way min/max did.
            var weight = Math.Max(cellNonPersistent, 1);
            for (var w = 0; w < weight; w++)
            {
                gxs.Add(gx);
                gys.Add(gy);
            }

            foreach (var p in c.PlacedObjects)
            {
                if (float.IsFinite(p.Z)) zs.Add(p.Z);
            }
        }

        if (count == 0) return null;

        // Percentile bands on the ref-weighted grid samples AND on Z, for the same reason as the
        // interior framer: dump captures carry stray resident cells and orphan placements far
        // from the capture's mass, and min/max framing hands the whole worldspace to them. The
        // auto-fit pass grows the frame back if genuinely-drawn content ends up clipped.
        var (minGxF, maxGxF) = PercentileRange(gxs);
        var (minGyF, maxGyF) = PercentileRange(gys);
        var minGx = (int)MathF.Floor(minGxF);
        var maxGx = (int)MathF.Ceiling(maxGxF);
        var minGy = (int)MathF.Floor(minGyF);
        var maxGy = (int)MathF.Ceiling(maxGyF);
        var (minZ, maxZ) = zs.Count > 0 ? PercentileRange(zs) : (0f, 0f);

        return new TopDownCaptureSubject(
            kind, formId, name,
            minGx * cellSize, (maxGx + 1) * cellSize,
            minGy * cellSize, (maxGy + 1) * cellSize,
            minZ, maxZ,
            count, placements,
            // Exterior water is worldspace-level (WRLD NAM2 default, per-cell XCLW override), not
            // something an individual cell has to opt into, so it always participates outdoors.
            HasWater: true,
            NonPersistentCount: nonPersistent,
            TerrainCellCount: terrainCells);
    }

    /// <summary>
    ///     Frames an interior from the spread of its placed objects, padded by
    ///     <see cref="InteriorFrameMarginFraction" /> so wall geometry whose ORIGIN sits at the
    ///     bounding edge still has its mesh inside the picture. Falls back to a minimum span for
    ///     interiors whose objects are nearly coincident, which would otherwise frame a
    ///     degenerate (zero-width) rectangle the ortho projection cannot build. Null when the
    ///     cell has no positioned objects.
    /// </summary>
    private TopDownCaptureSubject? TryFrameInterior(CellRecord cell)
    {
        // Percentile box, not min/max. Dump-recovered interiors carry orphan refs attributed to the
        // cell whose coordinates are nowhere near it (HooverDamIntPowerPlant04 holds hundreds of
        // Utl* orphans; see the xex21 attribution work) — one such placement stretches a min/max
        // box to tens of thousands of units, and at the fixed capture scale that demanded a ~5 GB
        // render target and failed the whole subject with E_OUTOFMEMORY. Trimming the coordinate
        // tails frames the room the refs actually describe; the auto-fit pass then recovers any
        // real geometry the trim grazed.
        var xs = new List<float>(cell.PlacedObjects.Count);
        var ys = new List<float>(cell.PlacedObjects.Count);
        var zs = new List<float>(cell.PlacedObjects.Count);
        var nonPersistent = 0;
        foreach (var p in cell.PlacedObjects)
        {
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z)) continue;
            xs.Add(p.X);
            ys.Add(p.Y);
            zs.Add(p.Z);
            if (!p.IsPersistent) nonPersistent++;
        }

        var count = xs.Count;
        if (count == 0) return null;

        var (minX, maxX) = PercentileRange(xs);
        var (minY, maxY) = PercentileRange(ys);
        var (minZ, maxZ) = PercentileRange(zs);

        var span = MathF.Max(maxX - minX, maxY - minY);
        var margin = MathF.Max(span * InteriorFrameMarginFraction, MinInteriorFrameMargin);
        minX -= margin;
        maxX += margin;
        minY -= margin;
        maxY += margin;

        return new TopDownCaptureSubject(
            TopDownSubjectKind.Interior, cell.FormId,
            cell.EditorId ?? cell.FullName ?? $"0x{cell.FormId:X8}",
            minX, maxX, minY, maxY, minZ, maxZ, 1, count,
            // Same credibility test the spatial index applies to the quad itself, so the
            // showWater switch and the quad's existence cannot disagree. On dumps the raw flag
            // byte alone flooded most interiors (stale runtime bytes read as "has water").
            HasWater: WorldSpatialIndex.HasCredibleInteriorWater(cell, _data?.IsMemoryDump == true),
            NonPersistentCount: nonPersistent);
    }

    /// <summary>
    ///     The [2nd, 98th] percentile range of <paramref name="values" /> — the coordinate band the
    ///     bulk of a cell's placements actually occupy. Small sets keep plain min/max: with a handful
    ///     of refs every one of them is load-bearing and nothing can be called an outlier.
    /// </summary>
    private static (float Min, float Max) PercentileRange(List<float> values)
    {
        values.Sort();
        if (values.Count < 20) return (values[0], values[^1]);

        var lo = (int)(values.Count * 0.02f);
        var hi = values.Count - 1 - lo;
        return (values[lo], values[hi]);
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

    /// <summary>
    ///     Profiler hook: apply <c>--capture-fov</c>. Clamped to the same [30,110]° range the live
    ///     FOV slider enforces so a replayed pose can never exceed what the viewer itself allows.
    /// </summary>
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
