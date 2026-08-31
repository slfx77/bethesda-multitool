using System.Diagnostics;
using System.Globalization;
using BethesdaMultitool.Core.WorldData;

namespace BethesdaMultitool;

/// <summary>One live-frame timing/statistics sample built by the WorldView3D frame loop.</summary>
internal readonly record struct FrameProfileSample(
    long FrameNumber,
    // The true completion-to-completion wall interval. This, rather than the timed render body
    // below, is the denominator for FPS because the GPU-slot wait happens before RenderFrameD3D12.
    double TotalMilliseconds,
    double RenderBodyMilliseconds,
    double ControllerMilliseconds,
    double BeginFrameMilliseconds,
    double FenceWaitMilliseconds,
    double AcquireMilliseconds,
    double ClearSetupMilliseconds,
    double CameraMilliseconds,
    double TerrainMilliseconds,
    double ReferencesMilliseconds,
    double WaterMilliseconds,
    double WireframeMilliseconds,
    // Coarse CPU wall time for the complete frame-end sun-shadow recording block. This is
    // intentionally separate from ReferencesMilliseconds: reference shadow replay happens after
    // the main reference pass and was previously invisible to the CPU stage breakdown.
    double ShadowMilliseconds,
    double EndFrameMilliseconds,
    double PresentMilliseconds,
    double HudMilliseconds,
    int VisibleTerrain,
    int VisibleReferences,
    int VisibleWater,
    int VisibleWireframe,
    int TotalCells,
    float RenderDistanceCells,
    uint ViewportWidth,
    uint ViewportHeight,
    uint DescriptorCount,
    uint RingBytes,
    int GcGen0Collections,
    int GcGen1Collections,
    int GcGen2Collections,
    long ManagedMemoryBytes,
    long AllocatedBytes,
    // The camera's integration timestep for this frame, BEFORE the pathological-delta clamp.
    // Apparent movement speed is MoveSpeed x DeltaSeconds, so a periodic pulse in this series IS the
    // reported motion "heartbeat" — it is not derivable from TotalMilliseconds, because the callback
    // interval and the frame's own cost are different quantities.
    double DeltaSeconds = 0);

/// <summary>
///     Aggregates <see cref="FrameProfileSample" /> rows (plus per-renderer
///     <see cref="WorldRenderStats" /> snapshots) over the profile-log interval and flushes one
///     averaged log line + JSONL aggregate per interval.
/// </summary>
internal sealed class FrameProfileAccumulator
{
    private readonly List<double> _frameSamples = new(256);
    private double _acquire;
    private double _allocatedBytes;
    private double _beginFrame;
    private double _camera;
    private double _clearSetup;
    private double _controller;
    private double _descriptors;
    private double _endFrame;
    private double _fenceWait;
    private double _frameMax;
    private int _frames;
    private double _frameTotal;
    private int _sceneCensusDirtyFrames;
    private int _sceneNonMaintenanceDirtyFrames;
    private double _renderBody;
    private double _hud;
    private long _intervalStarted = Stopwatch.GetTimestamp();
    private long _lastFrameNumber;
    private float _lastRenderDistanceCells;
    private int _lastTotalCells;
    private uint _lastViewportHeight;
    private uint _lastViewportWidth;
    private double _present;
    private double _refActiveDecodes;
    private double _refBatches;
    private double _refBlendedDraws;
    private double _refCandidates;
    private double _refCb;
    private double _refCellsVisited;
    private double _refCompressedTextureUploads;
    private double _refCpuDecodedHits;
    private double _refCpuDecodedMisses;
    private double _refCpuDecodedNegativeHits;
    private double _refCull;
    private double _refCulled;
    private bool _refCullRefreshPending;
    private double _refCullFlips;
    private double _refDecodeRequests;
    private double _refDecodeStarts;
    private double _refDrawCall;
    private double _refDrawn;
    private double _referencesFrame;
    private double _shadowFrame;
    private double _refInstancedDraws;
    private double _refInstances;
    private double _refOpaqueActiveDraws;
    private double _refOpaqueDecalDraws;
    private double _refOpaqueGrassCutoutDraws;
    private double _refOpaqueGrassDepthWriteDraws;
    private double _refOpaqueOrdinaryDraws;
    private double _refOpaquePsoTransitions;
    private double _refOpaqueSurvivingDraws;
    private double _refOpaqueUniquePsos;
    private int _refOpaqueIndirectActiveFrames;
    private double _refOpaqueDirectDraws;
    private double _refOpaqueIndirectDraws;
    private double _refOpaqueIndirectExecuteCalls;
    private double _refOpaqueIndirectArgumentBytes;
    private readonly Dictionary<int, int> _refOpaqueIndirectFallbacks = new();
    private int _refOpaqueFrontToBackActiveFrames;
    private double _refOpaqueFrontToBackBatches;
    private double _refOpaqueFrontToBackInstances;
    private readonly Dictionary<int, int> _refOpaqueFrontToBackFallbacks = new();
    private int _refModernStandardShaderActiveFrames;
    private double _refModernStandardBatches;
    private double _refModernStandardInstances;
    private int _refStaticOpaquePacketActiveFrames;
    private int _refStaticOpaquePacketHitFrames;
    private double _refStaticOpaquePacketBatches;
    private double _refStaticOpaquePacketInstances;
    private double _refStaticOpaquePacketRuns;
    private double _refStaticOpaquePacketBytes;
    private double _refStaticOpaquePacketBuildMilliseconds;
    private double _refStaticOpaquePacketSavedMatrixBytes;
    private double _refStaticOpaquePacketSavedConstantBytes;
    private double _refStaticOpaquePacketSavedArgumentBytes;
    private readonly Dictionary<int, int> _refStaticOpaquePacketFallbacks = new();
    private double _refLiveParticleDraws;
    private double _refLiveParticleFallbacks;
    private double _refLiveParticleOwners;
    private double _refLiveParticleParticles;
    private double _refLiveParticleUploadBytes;
    private double _refPlacedLights;
    private double _refLightTileBuild;
    private double _refLightTileCount;
    private double _refLightTileUploadBytes;
    private double _refLightTileAverageLights;
    private double _refLightTileMaxLights;
    private double _refLightTileEmptyPercent;
    private int _refLightTileFallbackFrames;
    private readonly Dictionary<string, int> _refLightTileFallbacks = new(StringComparer.Ordinal);
    private double _refMeshMisses;
    private double _refMeshMissing;
    private int _refMeshMaterializationPending;
    private double _refShadowMeshMissing;
    private double _refBatchBuild;
    private double _refBatchBuildProcessed;
    private double _refBatchBuildTotal;
    private double _refBatchBuildBudget;
    private double _refMeshResolve;
    private double _refInstanceBucket;
    private double _refBatchFinalize;
    private double _refGpuUpload;
    private double _refBlendedRefresh;
    private double _refBlendedSubmission;
    private double _refMainCpu;
    private double _refOpaqueSubmission;
    private int _refSampleFrames;
    private int _refCullCacheHits;
    private int _refBatchReuseFrames;
    private int _refIncrementalBuildFrames;
    private int _refBuildPublishedFrames;
    private int _refSyncFallbackFrames;
    private readonly Dictionary<int, int> _refReuseBlockers = [];
    private readonly Dictionary<int, int> _refBuildTriggers = [];
    private readonly Dictionary<int, int> _refBuildPhases = [];
    private double _refQueuedDecodes;
    private double _refRgbaTextureUploads;
    private double _refSrvBind;
    private double _refSrvBinds;
    private double _refState;
    private double _refSubmeshDraws;
    private double _refTexturePending;
    private int _refTextureActiveResolvesMax;
    private double _ringBytes;
    private double _terrainCandidates;
    private double _terrainCpu;
    private double _terrainDrawLoop;
    private double _terrainDraws;
    private int _terrainFrustumActiveFrames;
    private double _terrainFrustumRejected;
    private double _terrainFrame;
    private double _terrainGather;
    private double _terrainMeshUpload;
    private double _terrainPreUpload;
    private double _terrainPreUploads;
    private double _terrainQuadrantDraws;
    private double _terrainQuadrants;
    private double _terrainSort;
    private double _terrainState;
    private double _terrainUploads;
    private int _terrainSampleFrames;
    private double _textureCompressedUploads;
    private double _textureMisses;
    private double _textureRgbaUploads;
    private int _terrainTextureActiveResolvesMax;
    private double _visibleReferences;
    private double _visibleTerrain;
    private double _visibleWater;
    private double _visibleWireframe;
    private double _waterCpu;
    private double _waterDrawCall;
    private double _waterFrame;
    private double _waterGather;
    private double _waterInstanceBuild;
    private double _waterState;
    private double _waterUpload;
    private double _wireCpu;
    private double _wireDrawCall;
    private double _wireframeFrame;
    private double _wireGather;
    private double _wireUpload;
    private double _wireVertexBuild;

    internal void Add(
        FrameProfileSample sample,
        WorldRenderStats? terrain,
        WorldRenderStats? water,
        WorldRenderStats? wireframe,
        WorldRenderStats? references)
    {
        _frames++;
        var sceneCensus = CaptureSceneCensus.From(references, terrain);
        if (!sceneCensus.IsClean)
        {
            _sceneCensusDirtyFrames++;
        }
        if (!sceneCensus.IsCleanOrFrameCeilingMaintenance(
                references?.ReferenceBatchBuildTrigger ?? 0))
        {
            _sceneNonMaintenanceDirtyFrames++;
        }

        _lastFrameNumber = sample.FrameNumber;
        _frameTotal += sample.TotalMilliseconds;
        _frameMax = Math.Max(_frameMax, sample.TotalMilliseconds);
        _frameSamples.Add(sample.TotalMilliseconds);
        _renderBody += sample.RenderBodyMilliseconds;
        _allocatedBytes += sample.AllocatedBytes;
        _controller += sample.ControllerMilliseconds;
        _beginFrame += sample.BeginFrameMilliseconds;
        _fenceWait += sample.FenceWaitMilliseconds;
        _acquire += sample.AcquireMilliseconds;
        _clearSetup += sample.ClearSetupMilliseconds;
        _camera += sample.CameraMilliseconds;
        _terrainFrame += sample.TerrainMilliseconds;
        _referencesFrame += sample.ReferencesMilliseconds;
        _waterFrame += sample.WaterMilliseconds;
        _wireframeFrame += sample.WireframeMilliseconds;
        _shadowFrame += sample.ShadowMilliseconds;
        _endFrame += sample.EndFrameMilliseconds;
        _present += sample.PresentMilliseconds;
        _hud += sample.HudMilliseconds;
        _visibleTerrain += sample.VisibleTerrain;
        _visibleReferences += sample.VisibleReferences;
        _visibleWater += sample.VisibleWater;
        _visibleWireframe += sample.VisibleWireframe;
        _descriptors += sample.DescriptorCount;
        _ringBytes += sample.RingBytes;
        _lastTotalCells = sample.TotalCells;
        _lastRenderDistanceCells = sample.RenderDistanceCells;
        _lastViewportWidth = sample.ViewportWidth;
        _lastViewportHeight = sample.ViewportHeight;

        if (terrain is not null)
        {
            _terrainSampleFrames++;
            if (terrain.TerrainFrustumCullingActive) _terrainFrustumActiveFrames++;
            _terrainFrustumRejected += terrain.TerrainFrustumRejected;
            _terrainState += terrain.StateSetupMilliseconds;
            _terrainGather += terrain.VisibleGatherMilliseconds;
            _terrainSort += terrain.VisibleSortMilliseconds;
            _terrainDrawLoop += terrain.DrawLoopMilliseconds;
            _terrainQuadrants += terrain.QuadrantDrawMilliseconds;
            _terrainMeshUpload += terrain.MeshBuildUploadMilliseconds;
            _terrainPreUpload += terrain.NeighborPreUploadMilliseconds;
            _terrainCpu += terrain.CpuFrameMilliseconds;
            _terrainCandidates += terrain.VisibleCandidates;
            _terrainDraws += terrain.TerrainDraws;
            _terrainQuadrantDraws += terrain.TerrainQuadrantDraws;
            _terrainUploads += terrain.NewUploads;
            _terrainPreUploads += terrain.NewPreUploads;
            _textureMisses += terrain.TextureCacheMisses;
            _textureCompressedUploads += terrain.TextureCompressedUploads;
            _textureRgbaUploads += terrain.TextureRgbaFallbackUploads;
            _terrainTextureActiveResolvesMax = Math.Max(
                _terrainTextureActiveResolvesMax,
                terrain.TextureActiveResolves);
        }

        if (water is not null)
        {
            _waterState += water.StateSetupMilliseconds;
            _waterGather += water.VisibleGatherMilliseconds;
            _waterInstanceBuild += water.InstanceBuildMilliseconds;
            _waterUpload += water.GpuUploadMilliseconds;
            _waterDrawCall += water.DrawCallMilliseconds;
            _waterCpu += water.CpuFrameMilliseconds;
        }

        if (wireframe is not null)
        {
            _wireGather += wireframe.VisibleGatherMilliseconds;
            _wireVertexBuild += wireframe.InstanceBuildMilliseconds;
            _wireUpload += wireframe.GpuUploadMilliseconds;
            _wireDrawCall += wireframe.DrawCallMilliseconds;
            _wireCpu += wireframe.CpuFrameMilliseconds;
        }

        if (references is not null)
        {
            _refSampleFrames++;
            if (references.ReferenceCullCacheHit) _refCullCacheHits++;
            if (references.ReferenceBatchesReused) _refBatchReuseFrames++;
            if (references.ReferenceBatchBuildIncrementalAdvanced) _refIncrementalBuildFrames++;
            if (references.ReferenceBatchBuildPublished) _refBuildPublishedFrames++;
            if (references.ReferenceBatchBuildSynchronousFallback) _refSyncFallbackFrames++;
            IncrementHistogram(_refReuseBlockers, references.ReferenceBatchReuseBlocker);
            IncrementHistogram(_refBuildTriggers, references.ReferenceBatchBuildTrigger);
            IncrementHistogram(_refBuildPhases, references.ReferenceBatchBuildPhase);
            _refBatchBuildProcessed += references.ReferenceBatchBuildProcessed;
            _refBatchBuildTotal += references.ReferenceBatchBuildTotal;
            _refBatchBuildBudget += references.ReferenceBatchBuildBudgetMilliseconds;
            _refCellsVisited += references.ReferenceCellsVisited;
            _refCandidates += references.ReferenceCandidates;
            _refCulled += references.ReferenceCulled;
            _refCullRefreshPending |= references.ReferenceCullRefreshPending;
            _refMeshMissing += references.ReferenceMeshMissing;
            _refMeshMaterializationPending = Math.Max(
                _refMeshMaterializationPending,
                references.ReferenceMeshMaterializationRetriesPending);
            _refShadowMeshMissing += references.ReferenceShadowMeshMissing;
            _refTexturePending += references.ReferenceTexturePending;
            _refTextureActiveResolvesMax = Math.Max(
                _refTextureActiveResolvesMax,
                references.ReferenceTextureActiveResolves);
            _refDrawn += references.ReferenceDrawn;
            _refSubmeshDraws += references.ReferenceSubmeshDraws;
            _refSrvBinds += references.ReferenceSrvBinds;
            _refCullFlips += references.ReferenceCullModeFlips;
            _refMeshMisses += references.ReferenceMeshCacheMisses;
            _refDecodeRequests += references.ReferenceDecodeRequests;
            _refQueuedDecodes += references.ReferenceQueuedDecodes;
            _refDecodeStarts += references.ReferenceDecodeStarts;
            _refActiveDecodes += references.ReferenceActiveDecodes;
            _refCpuDecodedHits += references.ReferenceCpuDecodedMeshCacheHits;
            _refCpuDecodedMisses += references.ReferenceCpuDecodedMeshCacheMisses;
            _refCpuDecodedNegativeHits += references.ReferenceCpuDecodedMeshNegativeHits;
            _refCompressedTextureUploads += references.ReferenceCompressedTextureUploads;
            _refRgbaTextureUploads += references.ReferenceRgbaTextureUploads;
            _refBatches += references.ReferenceBatches;
            _refInstances += references.ReferenceInstances;
            _refInstancedDraws += references.ReferenceInstancedDraws;
            _refOpaqueActiveDraws += references.ReferenceOpaqueActiveDraws;
            _refOpaqueSurvivingDraws += references.ReferenceOpaqueSurvivingDraws;
            _refOpaquePsoTransitions += references.ReferenceOpaquePsoTransitions;
            _refOpaqueUniquePsos += references.ReferenceOpaqueUniquePsos;
            _refOpaqueOrdinaryDraws += references.ReferenceOpaqueOrdinaryDraws;
            _refOpaqueDecalDraws += references.ReferenceOpaqueDecalDraws;
            _refOpaqueGrassCutoutDraws += references.ReferenceOpaqueGrassCutoutDraws;
            _refOpaqueGrassDepthWriteDraws += references.ReferenceOpaqueGrassDepthWriteDraws;
            if (references.ReferenceOpaqueIndirectActive)
            {
                _refOpaqueIndirectActiveFrames++;
            }
            _refOpaqueDirectDraws += references.ReferenceOpaqueDirectDraws;
            _refOpaqueIndirectDraws += references.ReferenceOpaqueIndirectDraws;
            _refOpaqueIndirectExecuteCalls += references.ReferenceOpaqueIndirectExecuteCalls;
            _refOpaqueIndirectArgumentBytes += references.ReferenceOpaqueIndirectArgumentBytes;
            if (references.ReferenceOpaqueIndirectFallbackReason != 0)
            {
                IncrementHistogram(
                    _refOpaqueIndirectFallbacks,
                    references.ReferenceOpaqueIndirectFallbackReason);
            }
            if (references.ReferenceOpaqueFrontToBackActive)
            {
                _refOpaqueFrontToBackActiveFrames++;
            }
            _refOpaqueFrontToBackBatches += references.ReferenceOpaqueFrontToBackBatches;
            _refOpaqueFrontToBackInstances += references.ReferenceOpaqueFrontToBackInstances;
            if (references.ReferenceOpaqueFrontToBackFallbackReason != 0)
            {
                IncrementHistogram(
                    _refOpaqueFrontToBackFallbacks,
                    references.ReferenceOpaqueFrontToBackFallbackReason);
            }
            if (references.ReferenceModernStandardShaderActive)
            {
                _refModernStandardShaderActiveFrames++;
            }
            _refModernStandardBatches += references.ReferenceModernStandardBatches;
            _refModernStandardInstances += references.ReferenceModernStandardInstances;
            if (references.ReferenceStaticOpaquePacketActive)
            {
                _refStaticOpaquePacketActiveFrames++;
            }
            if (references.ReferenceStaticOpaquePacketHit)
            {
                _refStaticOpaquePacketHitFrames++;
            }
            _refStaticOpaquePacketBatches += references.ReferenceStaticOpaquePacketBatches;
            _refStaticOpaquePacketInstances += references.ReferenceStaticOpaquePacketInstances;
            _refStaticOpaquePacketRuns += references.ReferenceStaticOpaquePacketRuns;
            _refStaticOpaquePacketBytes += references.ReferenceStaticOpaquePacketBytes;
            _refStaticOpaquePacketBuildMilliseconds +=
                references.ReferenceStaticOpaquePacketBuildMilliseconds;
            _refStaticOpaquePacketSavedMatrixBytes +=
                references.ReferenceStaticOpaquePacketSavedMatrixBytes;
            _refStaticOpaquePacketSavedConstantBytes +=
                references.ReferenceStaticOpaquePacketSavedConstantBytes;
            _refStaticOpaquePacketSavedArgumentBytes +=
                references.ReferenceStaticOpaquePacketSavedArgumentBytes;
            if (references.ReferenceStaticOpaquePacketFallbackReason != 0)
            {
                IncrementHistogram(
                    _refStaticOpaquePacketFallbacks,
                    references.ReferenceStaticOpaquePacketFallbackReason);
            }
            _refBlendedDraws += references.ReferenceBlendedDraws;
            _refPlacedLights += references.ReferencePlacedLightCount;
            _refLightTileBuild += references.ReferencePlacedLightTileBuildMilliseconds;
            _refLightTileCount += references.ReferencePlacedLightTileCount;
            _refLightTileUploadBytes += references.ReferencePlacedLightTileUploadBytes;
            _refLightTileAverageLights += references.ReferencePlacedLightTileAverageLights;
            _refLightTileMaxLights += references.ReferencePlacedLightTileMaxLights;
            _refLightTileEmptyPercent += references.ReferencePlacedLightTileEmptyPercent;
            if (references.ReferencePlacedLightTileFallbackReason is { } lightTileFallback)
            {
                _refLightTileFallbackFrames++;
                _refLightTileFallbacks[lightTileFallback] =
                    _refLightTileFallbacks.TryGetValue(lightTileFallback, out var count) ? count + 1 : 1;
            }
            _refLiveParticleOwners += references.ReferenceLiveParticleOwners;
            _refLiveParticleParticles += references.ReferenceLiveParticleParticles;
            _refLiveParticleDraws += references.ReferenceLiveParticleDraws;
            _refLiveParticleFallbacks += references.ReferenceLiveParticleFallbacks;
            _refLiveParticleUploadBytes += references.ReferenceLiveParticleUploadBytes;
            _refState += references.ReferenceStateSetupMilliseconds;
            _refCull += references.ReferenceCullMilliseconds;
            _refBatchBuild += references.ReferenceBatchBuildMilliseconds;
            _refMeshResolve += references.ReferenceMeshResolveMilliseconds;
            _refInstanceBucket += references.ReferenceInstanceBucketingMilliseconds;
            _refBatchFinalize += references.ReferenceBatchFinalizeMilliseconds;
            _refGpuUpload += references.ReferenceGpuUploadMilliseconds;
            _refBlendedRefresh += references.ReferenceBlendedRefreshMilliseconds;
            _refMainCpu += references.CpuFrameMilliseconds;
            _refOpaqueSubmission += references.ReferenceOpaqueSubmissionMilliseconds;
            _refBlendedSubmission += references.ReferenceBlendedSubmissionMilliseconds;
            _refCb += references.ReferenceCbUpdateMilliseconds;
            _refSrvBind += references.ReferenceSrvBindMilliseconds;
            _refDrawCall += references.ReferenceDrawCallMilliseconds;
        }
    }

    internal bool TryFlush(
        int intervalMilliseconds,
        out string message,
        out IReadOnlyDictionary<string, object?> aggregate)
    {
        var elapsed = Stopwatch.GetElapsedTime(_intervalStarted).TotalMilliseconds;
        if (_frames == 0 || elapsed < intervalMilliseconds)
        {
            message = "";
            aggregate = new Dictionary<string, object?>();
            return false;
        }

        double Avg(double value) => value / _frames;
        double TerrainAvg(double value) =>
            _terrainSampleFrames == 0 ? 0.0 : value / _terrainSampleFrames;

        // One definition of median/p99/1%-low, shared with the live HUD, rather than a second copy
        // of the arithmetic here where the test project cannot reach it.
        var pacingSampler = new Core.Diagnostics.PerformanceSampler(Math.Max(1, _frameSamples.Count));
        foreach (var frameMs in _frameSamples)
        {
            pacingSampler.Add(frameMs);
        }

        var pacing = pacingSampler.Snapshot();
        var frameMedianMs = pacing.MedianMilliseconds;
        aggregate = new Dictionary<string, object?>
        {
            // FPS and pacing first: an A/B on frame time is unreadable without the tail, and every
            // comparison in this program so far has been made by hand from raw millisecond averages.
            ["fpsMedian"] = pacing.MedianFps,
            ["fpsAverage"] = pacing.AverageFps,
            ["fpsOnePercentLow"] = pacing.OnePercentLowFps,
            ["frameP99Ms"] = pacing.Percentile99Milliseconds,
            ["lastFrame"] = _lastFrameNumber,
            ["frames"] = _frames,
            ["elapsedSeconds"] = elapsed / 1000.0,
            ["viewportWidth"] = _lastViewportWidth,
            ["viewportHeight"] = _lastViewportHeight,
            ["totalCells"] = _lastTotalCells,
            ["renderDistanceCells"] = _lastRenderDistanceCells,
            ["frameAvgMs"] = Avg(_frameTotal),
            ["frameMedianMs"] = frameMedianMs,
            ["frameMaxMs"] = _frameMax,
            ["renderBodyAvgMs"] = Avg(_renderBody),
            ["allocatedBytesAvg"] = Avg(_allocatedBytes),
            ["allocatedBytesPerSecond"] = _allocatedBytes / (elapsed / 1000.0),
            ["controllerAvgMs"] = Avg(_controller),
            ["beginFrameAvgMs"] = Avg(_beginFrame),
            ["fenceWaitAvgMs"] = Avg(_fenceWait),
            ["acquireAvgMs"] = Avg(_acquire),
            ["clearAvgMs"] = Avg(_clearSetup),
            ["cameraAvgMs"] = Avg(_camera),
            ["terrainAvgMs"] = Avg(_terrainFrame),
            ["referencesAvgMs"] = Avg(_referencesFrame),
            ["waterAvgMs"] = Avg(_waterFrame),
            ["wireframeAvgMs"] = Avg(_wireframeFrame),
            ["shadowCpuAvgMs"] = Avg(_shadowFrame),
            ["endFrameAvgMs"] = Avg(_endFrame),
            ["presentAvgMs"] = Avg(_present),
            ["hudAvgMs"] = Avg(_hud),
            ["descriptorAvg"] = Avg(_descriptors),
            ["ringBytesAvg"] = Avg(_ringBytes),
            ["visibleTerrainAvg"] = Avg(_visibleTerrain),
            ["visibleReferencesAvg"] = Avg(_visibleReferences),
            ["visibleWaterAvg"] = Avg(_visibleWater),
            ["visibleWireframeAvg"] = Avg(_visibleWireframe),
            ["terrainCpuAvgMs"] = Avg(_terrainCpu),
            ["terrainLoopAvgMs"] = Avg(_terrainDrawLoop),
            ["terrainMeshUploadAvgMs"] = Avg(_terrainMeshUpload),
            ["terrainSampleFrames"] = _terrainSampleFrames,
            ["terrainFrustumActiveRate"] = Rate(_terrainFrustumActiveFrames, _terrainSampleFrames),
            ["terrainFrustumRejectedAvg"] = TerrainAvg(_terrainFrustumRejected),
            // The old value was a hand-selected sum of sub-timers and omitted most of the opaque
            // loop. Report the renderer's actual main-pass stopwatch under the established key;
            // retain the selected sum under an explicitly honest compatibility name.
            ["refsCpuAvgMs"] = Avg(_refMainCpu),
            ["refsInstrumentedAvgMs"] = Avg(_refState + _refCull + _refBatchBuild + _refBlendedRefresh + _refCb + _refSrvBind + _refDrawCall),
            ["refsOpaqueSubmitAvgMs"] = Avg(_refOpaqueSubmission),
            ["refsBlendedSubmitAvgMs"] = Avg(_refBlendedSubmission),
            ["refsCullAvgMs"] = Avg(_refCull),
            ["refsBatchBuildAvgMs"] = Avg(_refBatchBuild),
            // Compatibility alias: this metric was always the whole resolve + batch-build pass,
            // despite its old "mesh upload" name. Keep historical analysis scripts readable.
            ["refsMeshUploadAvgMs"] = Avg(_refBatchBuild),
            ["refsBatchBuildProcessedAvg"] = Avg(_refBatchBuildProcessed),
            ["refsBatchBuildTotalAvg"] = Avg(_refBatchBuildTotal),
            ["refsBatchBuildBudgetAvgMs"] = Avg(_refBatchBuildBudget),
            ["refsMeshResolveAvgMs"] = Avg(_refMeshResolve),
            ["refsInstanceBucketAvgMs"] = Avg(_refInstanceBucket),
            ["refsBatchFinalizeAvgMs"] = Avg(_refBatchFinalize),
            ["refsGpuUploadAvgMs"] = Avg(_refGpuUpload),
            ["refsBlendedRefreshAvgMs"] = Avg(_refBlendedRefresh),
            ["refsSampleFrames"] = _refSampleFrames,
            ["refsCullCacheHitRate"] = Rate(_refCullCacheHits, _refSampleFrames),
            ["refsBatchReuseRate"] = Rate(_refBatchReuseFrames, _refSampleFrames),
            ["refsIncrementalBuildRate"] = Rate(_refIncrementalBuildFrames, _refSampleFrames),
            ["refsBuildPublishRate"] = Rate(_refBuildPublishedFrames, _refSampleFrames),
            ["refsSyncFallbackRate"] = Rate(_refSyncFallbackFrames, _refSampleFrames),
            // Interval maxima/OR, not averages: a zero interval proves each capture blocker stayed
            // drained for every sampled frame instead of hiding one pending frame through dilution.
            ["refCullRefreshPending"] = _refCullRefreshPending,
            ["refMeshMaterializationPending"] = _refMeshMaterializationPending,
            ["refTextureActiveResolvesMax"] = _refTextureActiveResolvesMax,
            ["terrainTextureActiveResolvesMax"] = _terrainTextureActiveResolvesMax,
            ["sceneCensusDirtyFrames"] = _sceneCensusDirtyFrames,
            ["sceneNonMaintenanceDirtyFrames"] = _sceneNonMaintenanceDirtyFrames,
            ["refsReuseBlockerHistogram"] = SnapshotHistogram(_refReuseBlockers),
            ["refsBatchBuildTriggerHistogram"] = SnapshotHistogram(_refBuildTriggers),
            ["refsBatchBuildPhaseHistogram"] = SnapshotHistogram(_refBuildPhases),
            ["refsCbAvgMs"] = Avg(_refCb),
            ["refsDrawAvgMs"] = Avg(_refDrawCall),
            ["refsCbDrawTimingScope"] = "legacy-partial; use refsOpaqueSubmitAvgMs/refsBlendedSubmitAvgMs",
            ["refsTexturePendingAvg"] = Avg(_refTexturePending),
            ["refsShadowMeshMissingAvg"] = Avg(_refShadowMeshMissing),
            ["refsDrawnAvg"] = Avg(_refDrawn),
            ["refsSubmeshAvg"] = Avg(_refSubmeshDraws),
            ["refsBatchesAvg"] = Avg(_refBatches),
            ["refsInstancesAvg"] = Avg(_refInstances),
            ["refsInstancedDrawsAvg"] = Avg(_refInstancedDraws),
            ["refsOpaqueActiveDrawsAvg"] = Avg(_refOpaqueActiveDraws),
            ["refsOpaqueSurvivingDrawsAvg"] = Avg(_refOpaqueSurvivingDraws),
            ["refsOpaquePsoTransitionsAvg"] = Avg(_refOpaquePsoTransitions),
            ["refsOpaqueUniquePsosAvg"] = Avg(_refOpaqueUniquePsos),
            ["refsOpaqueOrdinaryDrawsAvg"] = Avg(_refOpaqueOrdinaryDraws),
            ["refsOpaqueDecalDrawsAvg"] = Avg(_refOpaqueDecalDraws),
            ["refsOpaqueGrassCutoutDrawsAvg"] = Avg(_refOpaqueGrassCutoutDraws),
            ["refsOpaqueGrassDepthWriteDrawsAvg"] = Avg(_refOpaqueGrassDepthWriteDraws),
            ["refsOpaqueIndirectActiveRate"] = Rate(_refOpaqueIndirectActiveFrames, _refSampleFrames),
            ["refsOpaqueDirectDrawsAvg"] = Avg(_refOpaqueDirectDraws),
            ["refsOpaqueIndirectDrawsAvg"] = Avg(_refOpaqueIndirectDraws),
            ["refsOpaqueIndirectExecuteCallsAvg"] = Avg(_refOpaqueIndirectExecuteCalls),
            ["refsOpaqueIndirectArgumentBytesAvg"] = Avg(_refOpaqueIndirectArgumentBytes),
            ["refsOpaqueIndirectFallbackHistogram"] = SnapshotHistogram(_refOpaqueIndirectFallbacks),
            ["refsOpaqueFrontToBackActiveRate"] =
                Rate(_refOpaqueFrontToBackActiveFrames, _refSampleFrames),
            ["refsOpaqueFrontToBackBatchesAvg"] = Avg(_refOpaqueFrontToBackBatches),
            ["refsOpaqueFrontToBackInstancesAvg"] = Avg(_refOpaqueFrontToBackInstances),
            ["refsOpaqueFrontToBackFallbackHistogram"] =
                SnapshotHistogram(_refOpaqueFrontToBackFallbacks),
            ["refsModernStandardShaderActiveRate"] =
                Rate(_refModernStandardShaderActiveFrames, _refSampleFrames),
            ["refsModernStandardBatchesAvg"] = Avg(_refModernStandardBatches),
            ["refsModernStandardInstancesAvg"] = Avg(_refModernStandardInstances),
            ["refsStaticOpaquePacketActiveRate"] =
                Rate(_refStaticOpaquePacketActiveFrames, _refSampleFrames),
            ["refsStaticOpaquePacketHitRate"] =
                Rate(_refStaticOpaquePacketHitFrames, _refSampleFrames),
            ["refsStaticOpaquePacketBatchesAvg"] = Avg(_refStaticOpaquePacketBatches),
            ["refsStaticOpaquePacketInstancesAvg"] = Avg(_refStaticOpaquePacketInstances),
            ["refsStaticOpaquePacketRunsAvg"] = Avg(_refStaticOpaquePacketRuns),
            ["refsStaticOpaquePacketBytesAvg"] = Avg(_refStaticOpaquePacketBytes),
            ["refsStaticOpaquePacketBuildAvgMs"] = Avg(_refStaticOpaquePacketBuildMilliseconds),
            ["refsStaticOpaquePacketSavedMatrixBytesAvg"] =
                Avg(_refStaticOpaquePacketSavedMatrixBytes),
            ["refsStaticOpaquePacketSavedConstantBytesAvg"] =
                Avg(_refStaticOpaquePacketSavedConstantBytes),
            ["refsStaticOpaquePacketSavedArgumentBytesAvg"] =
                Avg(_refStaticOpaquePacketSavedArgumentBytes),
            ["refsStaticOpaquePacketFallbackHistogram"] =
                SnapshotHistogram(_refStaticOpaquePacketFallbacks),
            ["refsBlendedDrawsAvg"] = Avg(_refBlendedDraws),
            ["refsPlacedLightsAvg"] = Avg(_refPlacedLights),
            ["refsLightTileBuildAvgMs"] = Avg(_refLightTileBuild),
            ["refsLightTileCountAvg"] = Avg(_refLightTileCount),
            ["refsLightTileUploadBytesAvg"] = Avg(_refLightTileUploadBytes),
            ["refsLightTileAverageLights"] = Avg(_refLightTileAverageLights),
            ["refsLightTileMaxLightsAvg"] = Avg(_refLightTileMaxLights),
            ["refsLightTileEmptyPercentAvg"] = Avg(_refLightTileEmptyPercent),
            ["refsLightTileFallbackRate"] = Rate(_refLightTileFallbackFrames, _refSampleFrames),
            ["refsLightTileFallbackHistogram"] = SnapshotStringHistogram(_refLightTileFallbacks),
            ["refsLiveParticleOwnersAvg"] = Avg(_refLiveParticleOwners),
            ["refsLiveParticleParticlesAvg"] = Avg(_refLiveParticleParticles),
            ["refsLiveParticleDrawsAvg"] = Avg(_refLiveParticleDraws),
            ["refsLiveParticleFallbacksAvg"] = Avg(_refLiveParticleFallbacks),
            ["refsLiveParticleUploadBytesAvg"] = Avg(_refLiveParticleUploadBytes)
        };
        // Apply invariant formatting per piece because concatenating interpolated strings
        // first would format each segment with the current UI culture.
        message =
            string.Create(CultureInfo.InvariantCulture,
                $"3D profile {_frames}f/{elapsed / 1000.0:0.0}s {_lastViewportWidth}x{_lastViewportHeight} cells={_lastTotalCells} dist={_lastRenderDistanceCells:0.#}c ") +
            string.Create(CultureInfo.InvariantCulture,
                $"FPS {pacing.MedianFps:0.0} (1%low {pacing.OnePercentLowFps:0.0}) ") +
            string.Create(CultureInfo.InvariantCulture,
                $"frame avg/max={Avg(_frameTotal):0.00}/{_frameMax:0.00}ms body={Avg(_renderBody):0.00}ms ") +
            string.Create(CultureInfo.InvariantCulture,
                $"median={frameMedianMs:0.00}ms p99={pacing.Percentile99Milliseconds:0.00}ms " +
                $"alloc={Avg(_allocatedBytes) / 1024.0:0.0}KB/f ") +
            string.Create(CultureInfo.InvariantCulture,
                $"stages ctrl={Avg(_controller):0.00} begin={Avg(_beginFrame):0.00} fence={Avg(_fenceWait):0.00} acquire={Avg(_acquire):0.00} clear={Avg(_clearSetup):0.00} ") +
            string.Create(CultureInfo.InvariantCulture,
                $"camera={Avg(_camera):0.00} terrain={Avg(_terrainFrame):0.00} refs={Avg(_referencesFrame):0.00} water={Avg(_waterFrame):0.00} ") +
            string.Create(CultureInfo.InvariantCulture,
                $"wire={Avg(_wireframeFrame):0.00} shadow={Avg(_shadowFrame):0.00} end={Avg(_endFrame):0.00} present={Avg(_present):0.00} hud={Avg(_hud):0.00} ") +
            string.Create(CultureInfo.InvariantCulture,
                $"desc={Avg(_descriptors):0.0} ringKB={Avg(_ringBytes) / 1024.0:0.0} | ") +
            string.Create(CultureInfo.InvariantCulture,
                $"visible terrain={Avg(_visibleTerrain):0.0} refs={Avg(_visibleReferences):0.0} water={Avg(_visibleWater):0.0} wire={Avg(_visibleWireframe):0.0} | ") +
            string.Create(CultureInfo.InvariantCulture,
                $"terrain cpu={Avg(_terrainCpu):0.00} state={Avg(_terrainState):0.00} gather={Avg(_terrainGather):0.00} ") +
            string.Create(CultureInfo.InvariantCulture,
                $"sort={Avg(_terrainSort):0.00} loop={Avg(_terrainDrawLoop):0.00} quadrants={Avg(_terrainQuadrants):0.00} ") +
            string.Create(CultureInfo.InvariantCulture,
                $"meshUpload={Avg(_terrainMeshUpload):0.00} preUpload={Avg(_terrainPreUpload):0.00} ") +
            string.Create(CultureInfo.InvariantCulture,
                $"cand={Avg(_terrainCandidates):0.0} visible={Avg(_visibleTerrain):0.0} cells={Avg(_terrainDraws):0.0} ") +
            string.Create(CultureInfo.InvariantCulture,
                $"qdraw={Avg(_terrainQuadrantDraws):0.0} frustum={Rate(_terrainFrustumActiveFrames, _terrainSampleFrames):0.00} " +
                $"reject={TerrainAvg(_terrainFrustumRejected):0.0} uploads={Avg(_terrainUploads):0.0}+{Avg(_terrainPreUploads):0.0} ") +
            string.Create(CultureInfo.InvariantCulture,
                $"texMiss={Avg(_textureMisses):0.0} texBC={Avg(_textureCompressedUploads):0.0} texRGBA={Avg(_textureRgbaUploads):0.0} | ") +
            string.Create(CultureInfo.InvariantCulture,
                $"water cpu={Avg(_waterCpu):0.00} state={Avg(_waterState):0.00} gather={Avg(_waterGather):0.00} ") +
            string.Create(CultureInfo.InvariantCulture,
                $"build={Avg(_waterInstanceBuild):0.00} upload={Avg(_waterUpload):0.00} draw={Avg(_waterDrawCall):0.00} ") +
            string.Create(CultureInfo.InvariantCulture, $"cells={Avg(_visibleWater):0.0} | ") +
            string.Create(CultureInfo.InvariantCulture,
                $"wire cpu={Avg(_wireCpu):0.00} gather={Avg(_wireGather):0.00} vertices={Avg(_wireVertexBuild):0.00} ") +
            string.Create(CultureInfo.InvariantCulture,
                $"upload={Avg(_wireUpload):0.00} draw={Avg(_wireDrawCall):0.00} cells={Avg(_visibleWireframe):0.0} | ") +
            string.Create(CultureInfo.InvariantCulture,
                $"refs cpu={Avg(_refMainCpu):0.00} state={Avg(_refState):0.00} cull={Avg(_refCull):0.00} batch={Avg(_refBatchBuild):0.00} ") +
            string.Create(CultureInfo.InvariantCulture,
                $"resolve={Avg(_refMeshResolve):0.00} bucket={Avg(_refInstanceBucket):0.00} finalize={Avg(_refBatchFinalize):0.00} gpuUp={Avg(_refGpuUpload):0.00} ") +
            string.Create(CultureInfo.InvariantCulture,
                $"opaqueSubmit={Avg(_refOpaqueSubmission):0.00} blendSubmit={Avg(_refBlendedSubmission):0.00} " +
                $"cbPartial={Avg(_refCb):0.00} srvBind={Avg(_refSrvBind):0.00} drawPartial={Avg(_refDrawCall):0.00} ") +
            string.Create(CultureInfo.InvariantCulture,
                $"cells={Avg(_refCellsVisited):0.0} cand={Avg(_refCandidates):0.0} culled={Avg(_refCulled):0.0} ") +
            string.Create(CultureInfo.InvariantCulture,
                $"miss={Avg(_refMeshMissing):0.0} texPend={Avg(_refTexturePending):0.0} drawn={Avg(_refDrawn):0.0} submesh={Avg(_refSubmeshDraws):0.0} ") +
            string.Create(CultureInfo.InvariantCulture,
                $"srvBinds={Avg(_refSrvBinds):0.0} cullFlips={Avg(_refCullFlips):0.0} meshMisses={Avg(_refMeshMisses):0.0} ") +
            string.Create(CultureInfo.InvariantCulture,
                $"decodeReq={Avg(_refDecodeRequests):0.0} qDec={Avg(_refQueuedDecodes):0.0} startDec={Avg(_refDecodeStarts):0.0} activeDec={Avg(_refActiveDecodes):0.0} ") +
            string.Create(CultureInfo.InvariantCulture,
                $"cpuMeshHit={Avg(_refCpuDecodedHits):0.0} cpuMeshMiss={Avg(_refCpuDecodedMisses):0.0} cpuMeshNeg={Avg(_refCpuDecodedNegativeHits):0.0} ") +
            string.Create(CultureInfo.InvariantCulture,
                $"refTexBC={Avg(_refCompressedTextureUploads):0.0} refTexRGBA={Avg(_refRgbaTextureUploads):0.0} batches={Avg(_refBatches):0.0} inst={Avg(_refInstances):0.0} ") +
            string.Create(CultureInfo.InvariantCulture,
                $"instDraw={Avg(_refInstancedDraws):0.0} blendDraw={Avg(_refBlendedDraws):0.0} ") +
            string.Create(CultureInfo.InvariantCulture,
                $"opaque={Avg(_refOpaqueActiveDraws):0.0}/{Avg(_refOpaqueSurvivingDraws):0.0} " +
                 $"pso={Avg(_refOpaqueUniquePsos):0.0}/{Avg(_refOpaquePsoTransitions):0.0} " +
                 $"lanes={Avg(_refOpaqueOrdinaryDraws):0.0}/{Avg(_refOpaqueDecalDraws):0.0}/" +
                 $"{Avg(_refOpaqueGrassCutoutDraws):0.0}/{Avg(_refOpaqueGrassDepthWriteDraws):0.0} ") +
            string.Create(CultureInfo.InvariantCulture,
                $"indirect={Avg(_refOpaqueIndirectDraws):0.0}/{Avg(_refOpaqueIndirectExecuteCalls):0.0} " +
                $"direct={Avg(_refOpaqueDirectDraws):0.0} argKB={Avg(_refOpaqueIndirectArgumentBytes) / 1024.0:0.0} " +
                $"frontToBack={Avg(_refOpaqueFrontToBackBatches):0.0}/{Avg(_refOpaqueFrontToBackInstances):0.0} " +
                $"modern={Avg(_refModernStandardBatches):0.0}/{Avg(_refModernStandardInstances):0.0} " +
                $"packet={Avg(_refStaticOpaquePacketBatches):0.0}/{Avg(_refStaticOpaquePacketInstances):0.0}/" +
                $"{Avg(_refStaticOpaquePacketRuns):0.0} ") +
            string.Create(CultureInfo.InvariantCulture,
                $"lightTiles={Avg(_refLightTileCount):0.0} lights={Avg(_refLightTileAverageLights):0.0}/{Avg(_refLightTileMaxLights):0.0} " +
                $"tileBuild={Avg(_refLightTileBuild):0.00}ms tileKB={Avg(_refLightTileUploadBytes) / 1024.0:0.0} ") +
            string.Create(CultureInfo.InvariantCulture,
                $"liveOwners={Avg(_refLiveParticleOwners):0.0} liveParticles={Avg(_refLiveParticleParticles):0.0} ") +
            string.Create(CultureInfo.InvariantCulture,
                $"liveDraw={Avg(_refLiveParticleDraws):0.0} liveFallback={Avg(_refLiveParticleFallbacks):0.0} ") +
            string.Create(CultureInfo.InvariantCulture,
                $"liveUploadKB={Avg(_refLiveParticleUploadBytes) / 1024.0:0.0}");

        Reset();
        return true;
    }

    /// <summary>
    ///     Starts a fresh observation window after a scene load. The control and swapchain exist
    ///     before world parsing finishes, so retaining their old interval would turn load time into
    ///     a fictitious first-frame hitch.
    /// </summary>
    internal void ResetForSceneLoad() => Reset();

    private void Reset()
    {
        _intervalStarted = Stopwatch.GetTimestamp();
        _frames = 0;
        _lastFrameNumber = 0;
        _frameTotal = 0;
        _frameMax = 0;
        _frameSamples.Clear();
        _sceneCensusDirtyFrames = 0;
        _sceneNonMaintenanceDirtyFrames = 0;
        _renderBody = 0;
        _allocatedBytes = 0;
        _controller = 0;
        _beginFrame = 0;
        _fenceWait = 0;
        _acquire = 0;
        _clearSetup = 0;
        _camera = 0;
        _terrainFrame = 0;
        _referencesFrame = 0;
        _waterFrame = 0;
        _wireframeFrame = 0;
        _shadowFrame = 0;
        _endFrame = 0;
        _present = 0;
        _hud = 0;
        _terrainState = 0;
        _terrainGather = 0;
        _terrainSort = 0;
        _terrainDrawLoop = 0;
        _terrainQuadrants = 0;
        _terrainMeshUpload = 0;
        _terrainPreUpload = 0;
        _terrainCpu = 0;
        _waterState = 0;
        _waterGather = 0;
        _waterInstanceBuild = 0;
        _waterUpload = 0;
        _waterDrawCall = 0;
        _waterCpu = 0;
        _wireGather = 0;
        _wireVertexBuild = 0;
        _wireUpload = 0;
        _wireDrawCall = 0;
        _wireCpu = 0;
        _visibleTerrain = 0;
        _visibleReferences = 0;
        _visibleWater = 0;
        _visibleWireframe = 0;
        _descriptors = 0;
        _ringBytes = 0;
        _terrainCandidates = 0;
        _terrainDraws = 0;
        _terrainSampleFrames = 0;
        _terrainFrustumActiveFrames = 0;
        _terrainFrustumRejected = 0;
        _terrainQuadrantDraws = 0;
        _terrainUploads = 0;
        _terrainPreUploads = 0;
        _textureMisses = 0;
        _textureCompressedUploads = 0;
        _textureRgbaUploads = 0;
        _terrainTextureActiveResolvesMax = 0;
        _refCellsVisited = 0;
        _refCandidates = 0;
        _refCulled = 0;
        _refCullRefreshPending = false;
        _refMeshMissing = 0;
        _refMeshMaterializationPending = 0;
        _refShadowMeshMissing = 0;
        _refTexturePending = 0;
        _refTextureActiveResolvesMax = 0;
        _refDrawn = 0;
        _refSubmeshDraws = 0;
        _refSrvBinds = 0;
        _refCullFlips = 0;
        _refMeshMisses = 0;
        _refDecodeRequests = 0;
        _refQueuedDecodes = 0;
        _refDecodeStarts = 0;
        _refActiveDecodes = 0;
        _refCpuDecodedHits = 0;
        _refCpuDecodedMisses = 0;
        _refCpuDecodedNegativeHits = 0;
        _refCompressedTextureUploads = 0;
        _refRgbaTextureUploads = 0;
        _refBatches = 0;
        _refInstances = 0;
        _refInstancedDraws = 0;
        _refOpaqueActiveDraws = 0;
        _refOpaqueSurvivingDraws = 0;
        _refOpaquePsoTransitions = 0;
        _refOpaqueUniquePsos = 0;
        _refOpaqueOrdinaryDraws = 0;
        _refOpaqueDecalDraws = 0;
        _refOpaqueGrassCutoutDraws = 0;
        _refOpaqueGrassDepthWriteDraws = 0;
        _refOpaqueIndirectActiveFrames = 0;
        _refOpaqueDirectDraws = 0;
        _refOpaqueIndirectDraws = 0;
        _refOpaqueIndirectExecuteCalls = 0;
        _refOpaqueIndirectArgumentBytes = 0;
        _refOpaqueIndirectFallbacks.Clear();
        _refOpaqueFrontToBackActiveFrames = 0;
        _refOpaqueFrontToBackBatches = 0;
        _refOpaqueFrontToBackInstances = 0;
        _refOpaqueFrontToBackFallbacks.Clear();
        _refModernStandardShaderActiveFrames = 0;
        _refModernStandardBatches = 0;
        _refModernStandardInstances = 0;
        _refStaticOpaquePacketActiveFrames = 0;
        _refStaticOpaquePacketHitFrames = 0;
        _refStaticOpaquePacketBatches = 0;
        _refStaticOpaquePacketInstances = 0;
        _refStaticOpaquePacketRuns = 0;
        _refStaticOpaquePacketBytes = 0;
        _refStaticOpaquePacketBuildMilliseconds = 0;
        _refStaticOpaquePacketSavedMatrixBytes = 0;
        _refStaticOpaquePacketSavedConstantBytes = 0;
        _refStaticOpaquePacketSavedArgumentBytes = 0;
        _refStaticOpaquePacketFallbacks.Clear();
        _refBlendedDraws = 0;
        _refPlacedLights = 0;
        _refLightTileBuild = 0;
        _refLightTileCount = 0;
        _refLightTileUploadBytes = 0;
        _refLightTileAverageLights = 0;
        _refLightTileMaxLights = 0;
        _refLightTileEmptyPercent = 0;
        _refLightTileFallbackFrames = 0;
        _refLightTileFallbacks.Clear();
        _refLiveParticleOwners = 0;
        _refLiveParticleParticles = 0;
        _refLiveParticleDraws = 0;
        _refLiveParticleFallbacks = 0;
        _refLiveParticleUploadBytes = 0;
        _refState = 0;
        _refCull = 0;
        _refBatchBuild = 0;
        _refBatchBuildProcessed = 0;
        _refBatchBuildTotal = 0;
        _refBatchBuildBudget = 0;
        _refMeshResolve = 0;
        _refInstanceBucket = 0;
        _refBatchFinalize = 0;
        _refGpuUpload = 0;
        _refBlendedRefresh = 0;
        _refMainCpu = 0;
        _refOpaqueSubmission = 0;
        _refBlendedSubmission = 0;
        _refSampleFrames = 0;
        _refCullCacheHits = 0;
        _refBatchReuseFrames = 0;
        _refIncrementalBuildFrames = 0;
        _refBuildPublishedFrames = 0;
        _refSyncFallbackFrames = 0;
        _refReuseBlockers.Clear();
        _refBuildTriggers.Clear();
        _refBuildPhases.Clear();
        _refCb = 0;
        _refSrvBind = 0;
        _refDrawCall = 0;
    }

    private static void IncrementHistogram(Dictionary<int, int> histogram, int key)
    {
        histogram[key] = histogram.TryGetValue(key, out var count) ? count + 1 : 1;
    }

    private static double Rate(int count, int total) => total == 0 ? 0.0 : (double)count / total;

    private static Dictionary<string, int> SnapshotHistogram(Dictionary<int, int> histogram)
    {
        return histogram.ToDictionary(
            pair => pair.Key.ToString(CultureInfo.InvariantCulture),
            pair => pair.Value,
            StringComparer.Ordinal);
    }

    private static Dictionary<string, int> SnapshotStringHistogram(Dictionary<string, int> histogram)
    {
        return histogram.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal);
    }
}
