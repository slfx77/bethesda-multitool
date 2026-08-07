using System.Diagnostics;
using System.Globalization;

namespace BethesdaMultitool;

/// <summary>One live-frame timing/statistics sample built by the WorldView3D frame loop.</summary>
internal readonly record struct FrameProfileSample(
    long FrameNumber,
    double TotalMilliseconds,
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
    private long _intervalStarted = Stopwatch.GetTimestamp();
    private int _frames;
    private long _lastFrameNumber;
    private double _frameTotal;
    private double _frameMax;
    private readonly List<double> _frameSamples = new(256);
    private double _allocatedBytes;
    private double _controller;
    private double _beginFrame;
    private double _fenceWait;
    private double _acquire;
    private double _clearSetup;
    private double _camera;
    private double _terrainFrame;
    private double _referencesFrame;
    private double _waterFrame;
    private double _wireframeFrame;
    private double _endFrame;
    private double _present;
    private double _hud;
    private double _terrainState;
    private double _terrainGather;
    private double _terrainSort;
    private double _terrainDrawLoop;
    private double _terrainQuadrants;
    private double _terrainMeshUpload;
    private double _terrainPreUpload;
    private double _terrainCpu;
    private double _waterState;
    private double _waterGather;
    private double _waterInstanceBuild;
    private double _waterUpload;
    private double _waterDrawCall;
    private double _waterCpu;
    private double _wireGather;
    private double _wireVertexBuild;
    private double _wireUpload;
    private double _wireDrawCall;
    private double _wireCpu;
    private double _visibleTerrain;
    private double _visibleReferences;
    private double _visibleWater;
    private double _visibleWireframe;
    private double _descriptors;
    private double _ringBytes;
    private double _terrainCandidates;
    private double _terrainDraws;
    private double _terrainQuadrantDraws;
    private double _terrainUploads;
    private double _terrainPreUploads;
    private double _textureMisses;
    private double _textureCompressedUploads;
    private double _textureRgbaUploads;
    private double _refCellsVisited;
    private double _refCandidates;
    private double _refCulled;
    private double _refMeshMissing;
    private double _refTexturePending;
    private double _refDrawn;
    private double _refSubmeshDraws;
    private double _refSrvBinds;
    private double _refCullFlips;
    private double _refMeshMisses;
    private double _refDecodeRequests;
    private double _refQueuedDecodes;
    private double _refDecodeStarts;
    private double _refActiveDecodes;
    private double _refCpuDecodedHits;
    private double _refCpuDecodedMisses;
    private double _refCpuDecodedNegativeHits;
    private double _refCompressedTextureUploads;
    private double _refRgbaTextureUploads;
    private double _refBatches;
    private double _refInstances;
    private double _refInstancedDraws;
    private double _refBlendedDraws;
    private double _refLiveParticleOwners;
    private double _refLiveParticleParticles;
    private double _refLiveParticleDraws;
    private double _refLiveParticleFallbacks;
    private double _refLiveParticleUploadBytes;
    private double _refState;
    private double _refCull;
    private double _refMeshUpload;
    private double _refCb;
    private double _refSrvBind;
    private double _refDrawCall;
    private int _lastTotalCells;
    private float _lastRenderDistanceCells;
    private uint _lastViewportWidth;
    private uint _lastViewportHeight;

    internal void Add(
        FrameProfileSample sample,
        WorldRenderStats? terrain,
        WorldRenderStats? water,
        WorldRenderStats? wireframe,
        WorldRenderStats? references)
    {
        _frames++;
        _lastFrameNumber = sample.FrameNumber;
        _frameTotal += sample.TotalMilliseconds;
        _frameMax = Math.Max(_frameMax, sample.TotalMilliseconds);
        _frameSamples.Add(sample.TotalMilliseconds);
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
            _refCellsVisited += references.ReferenceCellsVisited;
            _refCandidates += references.ReferenceCandidates;
            _refCulled += references.ReferenceCulled;
            _refMeshMissing += references.ReferenceMeshMissing;
            _refTexturePending += references.ReferenceTexturePending;
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
            _refBlendedDraws += references.ReferenceBlendedDraws;
            _refLiveParticleOwners += references.ReferenceLiveParticleOwners;
            _refLiveParticleParticles += references.ReferenceLiveParticleParticles;
            _refLiveParticleDraws += references.ReferenceLiveParticleDraws;
            _refLiveParticleFallbacks += references.ReferenceLiveParticleFallbacks;
            _refLiveParticleUploadBytes += references.ReferenceLiveParticleUploadBytes;
            _refState += references.ReferenceStateSetupMilliseconds;
            _refCull += references.ReferenceCullMilliseconds;
            _refMeshUpload += references.ReferenceMeshUploadMilliseconds;
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
        _frameSamples.Sort();
        var frameMedianMs = (_frameSamples.Count & 1) != 0
            ? _frameSamples[_frameSamples.Count / 2]
            : (_frameSamples[_frameSamples.Count / 2 - 1] + _frameSamples[_frameSamples.Count / 2]) * 0.5;
        aggregate = new Dictionary<string, object?>
        {
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
            ["refsCpuAvgMs"] = Avg(_refState + _refCull + _refMeshUpload + _refCb + _refSrvBind + _refDrawCall),
            ["refsCullAvgMs"] = Avg(_refCull),
            ["refsMeshUploadAvgMs"] = Avg(_refMeshUpload),
            ["refsCbAvgMs"] = Avg(_refCb),
            ["refsDrawAvgMs"] = Avg(_refDrawCall),
            ["refsTexturePendingAvg"] = Avg(_refTexturePending),
            ["refsDrawnAvg"] = Avg(_refDrawn),
            ["refsSubmeshAvg"] = Avg(_refSubmeshDraws),
            ["refsBatchesAvg"] = Avg(_refBatches),
            ["refsInstancesAvg"] = Avg(_refInstances),
            ["refsInstancedDrawsAvg"] = Avg(_refInstancedDraws),
            ["refsBlendedDrawsAvg"] = Avg(_refBlendedDraws),
            ["refsLiveParticleOwnersAvg"] = Avg(_refLiveParticleOwners),
            ["refsLiveParticleParticlesAvg"] = Avg(_refLiveParticleParticles),
            ["refsLiveParticleDrawsAvg"] = Avg(_refLiveParticleDraws),
            ["refsLiveParticleFallbacksAvg"] = Avg(_refLiveParticleFallbacks),
            ["refsLiveParticleUploadBytesAvg"] = Avg(_refLiveParticleUploadBytes)
        };
        // Apply invariant formatting per piece because concatenating interpolated strings
        // first would format each segment with the current UI culture.
            message =
            string.Create(CultureInfo.InvariantCulture, $"3D profile {_frames}f/{elapsed / 1000.0:0.0}s {_lastViewportWidth}x{_lastViewportHeight} cells={_lastTotalCells} dist={_lastRenderDistanceCells:0.#}c ") +
            string.Create(CultureInfo.InvariantCulture, $"frame avg/max={Avg(_frameTotal):0.00}/{_frameMax:0.00}ms ") +
            string.Create(CultureInfo.InvariantCulture, $"median={frameMedianMs:0.00}ms alloc={Avg(_allocatedBytes) / 1024.0:0.0}KB/f ") +
            string.Create(CultureInfo.InvariantCulture, $"stages ctrl={Avg(_controller):0.00} begin={Avg(_beginFrame):0.00} fence={Avg(_fenceWait):0.00} acquire={Avg(_acquire):0.00} clear={Avg(_clearSetup):0.00} ") +
            string.Create(CultureInfo.InvariantCulture, $"camera={Avg(_camera):0.00} terrain={Avg(_terrainFrame):0.00} refs={Avg(_referencesFrame):0.00} water={Avg(_waterFrame):0.00} ") +
            string.Create(CultureInfo.InvariantCulture, $"wire={Avg(_wireframeFrame):0.00} end={Avg(_endFrame):0.00} present={Avg(_present):0.00} hud={Avg(_hud):0.00} ") +
            string.Create(CultureInfo.InvariantCulture, $"desc={Avg(_descriptors):0.0} ringKB={Avg(_ringBytes) / 1024.0:0.0} | ") +
            string.Create(CultureInfo.InvariantCulture, $"visible terrain={Avg(_visibleTerrain):0.0} refs={Avg(_visibleReferences):0.0} water={Avg(_visibleWater):0.0} wire={Avg(_visibleWireframe):0.0} | ") +
            string.Create(CultureInfo.InvariantCulture, $"terrain cpu={Avg(_terrainCpu):0.00} state={Avg(_terrainState):0.00} gather={Avg(_terrainGather):0.00} ") +
            string.Create(CultureInfo.InvariantCulture, $"sort={Avg(_terrainSort):0.00} loop={Avg(_terrainDrawLoop):0.00} quadrants={Avg(_terrainQuadrants):0.00} ") +
            string.Create(CultureInfo.InvariantCulture, $"meshUpload={Avg(_terrainMeshUpload):0.00} preUpload={Avg(_terrainPreUpload):0.00} ") +
            string.Create(CultureInfo.InvariantCulture, $"cand={Avg(_terrainCandidates):0.0} visible={Avg(_visibleTerrain):0.0} cells={Avg(_terrainDraws):0.0} ") +
            string.Create(CultureInfo.InvariantCulture, $"qdraw={Avg(_terrainQuadrantDraws):0.0} uploads={Avg(_terrainUploads):0.0}+{Avg(_terrainPreUploads):0.0} ") +
            string.Create(CultureInfo.InvariantCulture, $"texMiss={Avg(_textureMisses):0.0} texBC={Avg(_textureCompressedUploads):0.0} texRGBA={Avg(_textureRgbaUploads):0.0} | ") +
            string.Create(CultureInfo.InvariantCulture, $"water cpu={Avg(_waterCpu):0.00} state={Avg(_waterState):0.00} gather={Avg(_waterGather):0.00} ") +
            string.Create(CultureInfo.InvariantCulture, $"build={Avg(_waterInstanceBuild):0.00} upload={Avg(_waterUpload):0.00} draw={Avg(_waterDrawCall):0.00} ") +
            string.Create(CultureInfo.InvariantCulture, $"cells={Avg(_visibleWater):0.0} | ") +
            string.Create(CultureInfo.InvariantCulture, $"wire cpu={Avg(_wireCpu):0.00} gather={Avg(_wireGather):0.00} vertices={Avg(_wireVertexBuild):0.00} ") +
            string.Create(CultureInfo.InvariantCulture, $"upload={Avg(_wireUpload):0.00} draw={Avg(_wireDrawCall):0.00} cells={Avg(_visibleWireframe):0.0} | ") +
            string.Create(CultureInfo.InvariantCulture, $"refs state={Avg(_refState):0.00} cull={Avg(_refCull):0.00} meshUp={Avg(_refMeshUpload):0.00} ") +
            string.Create(CultureInfo.InvariantCulture, $"cb={Avg(_refCb):0.00} srvBind={Avg(_refSrvBind):0.00} draw={Avg(_refDrawCall):0.00} ") +
            string.Create(CultureInfo.InvariantCulture, $"cells={Avg(_refCellsVisited):0.0} cand={Avg(_refCandidates):0.0} culled={Avg(_refCulled):0.0} ") +
            string.Create(CultureInfo.InvariantCulture, $"miss={Avg(_refMeshMissing):0.0} texPend={Avg(_refTexturePending):0.0} drawn={Avg(_refDrawn):0.0} submesh={Avg(_refSubmeshDraws):0.0} ") +
            string.Create(CultureInfo.InvariantCulture, $"srvBinds={Avg(_refSrvBinds):0.0} cullFlips={Avg(_refCullFlips):0.0} meshMisses={Avg(_refMeshMisses):0.0} ") +
            string.Create(CultureInfo.InvariantCulture, $"decodeReq={Avg(_refDecodeRequests):0.0} qDec={Avg(_refQueuedDecodes):0.0} startDec={Avg(_refDecodeStarts):0.0} activeDec={Avg(_refActiveDecodes):0.0} ") +
            string.Create(CultureInfo.InvariantCulture, $"cpuMeshHit={Avg(_refCpuDecodedHits):0.0} cpuMeshMiss={Avg(_refCpuDecodedMisses):0.0} cpuMeshNeg={Avg(_refCpuDecodedNegativeHits):0.0} ") +
            string.Create(CultureInfo.InvariantCulture, $"refTexBC={Avg(_refCompressedTextureUploads):0.0} refTexRGBA={Avg(_refRgbaTextureUploads):0.0} batches={Avg(_refBatches):0.0} inst={Avg(_refInstances):0.0} ") +
            string.Create(CultureInfo.InvariantCulture, $"instDraw={Avg(_refInstancedDraws):0.0} blendDraw={Avg(_refBlendedDraws):0.0} ") +
            string.Create(CultureInfo.InvariantCulture, $"liveOwners={Avg(_refLiveParticleOwners):0.0} liveParticles={Avg(_refLiveParticleParticles):0.0} ") +
            string.Create(CultureInfo.InvariantCulture, $"liveDraw={Avg(_refLiveParticleDraws):0.0} liveFallback={Avg(_refLiveParticleFallbacks):0.0} ") +
            string.Create(CultureInfo.InvariantCulture, $"liveUploadKB={Avg(_refLiveParticleUploadBytes) / 1024.0:0.0}");

        Reset();
        return true;
    }

    private void Reset()
    {
        _intervalStarted = Stopwatch.GetTimestamp();
        _frames = 0;
        _lastFrameNumber = 0;
        _frameTotal = 0;
        _frameMax = 0;
        _frameSamples.Clear();
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
        _terrainQuadrantDraws = 0;
        _terrainUploads = 0;
        _terrainPreUploads = 0;
        _textureMisses = 0;
        _textureCompressedUploads = 0;
        _textureRgbaUploads = 0;
        _refCellsVisited = 0;
        _refCandidates = 0;
        _refCulled = 0;
        _refMeshMissing = 0;
        _refTexturePending = 0;
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
        _refBlendedDraws = 0;
        _refLiveParticleOwners = 0;
        _refLiveParticleParticles = 0;
        _refLiveParticleDraws = 0;
        _refLiveParticleFallbacks = 0;
        _refLiveParticleUploadBytes = 0;
        _refState = 0;
        _refCull = 0;
        _refMeshUpload = 0;
        _refCb = 0;
        _refSrvBind = 0;
        _refDrawCall = 0;
    }
}
