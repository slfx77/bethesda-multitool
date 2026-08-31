using System.Globalization;
using System.Text.Json;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.WorldData;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Profiling;

/// <summary>Writes per-frame renderer profiling samples to a JSON trace file (thread-safe).</summary>
internal static class RendererProfilerTrace
{
    private static readonly Lock Sync = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private static TextWriter? _writer;
    private static bool _ownsWriter;
    private static bool _initialized;
    private static string _sessionId = CreateSessionId();

    /// <summary>
    ///     Identifies one configured writer lifetime. Trace files append by design, so timestamps
    ///     and event names alone cannot safely distinguish two profiler processes that reused a path.
    /// </summary>
    internal static string SessionId
    {
        get
        {
            lock (Sync)
            {
                return _sessionId;
            }
        }
    }

    internal static bool IsEnabled
    {
        get
        {
            EnsureInitialized();
            return _writer is not null;
        }
    }

    internal static string? OutputPath { get; private set; }

    internal static void Configure(string? path)
    {
        lock (Sync)
        {
            CloseLocked();
            _initialized = true;
            _sessionId = CreateSessionId();
            OutputPath = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
            if (OutputPath is null)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(OutputPath)!);
            _writer = new StreamWriter(
                    new FileStream(OutputPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                { AutoFlush = true };
            _ownsWriter = true;
        }
    }

    internal static void SetWriterForTesting(TextWriter writer)
    {
        lock (Sync)
        {
            CloseLocked();
            _initialized = true;
            _sessionId = CreateSessionId();
            OutputPath = null;
            _writer = writer;
            _ownsWriter = false;
        }
    }

    internal static void ResetForTesting()
    {
        lock (Sync)
        {
            CloseLocked();
            _initialized = false;
            _sessionId = CreateSessionId();
            OutputPath = null;
        }
    }

    internal static void Event(string eventType, IReadOnlyDictionary<string, object?>? fields = null)
    {
        EnsureInitialized();
        TextWriter? writer;
        lock (Sync)
        {
            writer = _writer;
            if (writer is null)
            {
                return;
            }

            var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["event"] = eventType,
                ["timestamp"] = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
                ["sessionId"] = _sessionId
            };

            if (fields is not null)
            {
                foreach (var field in fields)
                {
                    payload[field.Key] = field.Value;
                }
            }

            writer.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
        }
    }

    internal static Dictionary<string, object?> CameraPoseFields(RendererProfilerCameraPose pose)
    {
        return new Dictionary<string, object?>
        {
            ["cameraX"] = pose.Position.X,
            ["cameraY"] = pose.Position.Y,
            ["cameraZ"] = pose.Position.Z,
            ["cameraYaw"] = pose.Yaw,
            ["cameraPitch"] = pose.Pitch,
            ["renderDistance"] = pose.RenderDistance
        };
    }

    internal static Dictionary<string, object?> StatsFields(string prefix, WorldRenderStats? stats)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (stats is null)
        {
            return result;
        }

        Add("cpuMs", stats.CpuFrameMilliseconds);
        Add("stateMs", stats.StateSetupMilliseconds);
        Add("gatherMs", stats.VisibleGatherMilliseconds);
        Add("sortMs", stats.VisibleSortMilliseconds);
        Add("loopMs", stats.DrawLoopMilliseconds);
        Add("meshUploadMs", stats.MeshBuildUploadMilliseconds);
        Add("preUploadMs", stats.NeighborPreUploadMilliseconds);
        Add("quadrantMs", stats.QuadrantDrawMilliseconds);
        Add("instanceBuildMs", stats.InstanceBuildMilliseconds);
        Add("gpuUploadMs", stats.GpuUploadMilliseconds);
        Add("drawCallMs", stats.DrawCallMilliseconds);
        Add("resizeMs", stats.ResourceResizeMilliseconds);
        Add("visibleCandidates", stats.VisibleCandidates);
        Add("terrainDraws", stats.TerrainDraws);
        Add("terrainQuadrantDraws", stats.TerrainQuadrantDraws);
        Add("terrainFrustumActive", stats.TerrainFrustumCullingActive);
        Add("terrainFrustumRejected", stats.TerrainFrustumRejected);
        Add("cellsEvictedForBudget", stats.CellsEvictedForBudget);
        Add("newUploads", stats.NewUploads);
        Add("newPreUploads", stats.NewPreUploads);
        Add("textureMisses", stats.TextureCacheMisses);
        Add("textureCompressedUploads", stats.TextureCompressedUploads);
        Add("textureRgbaUploads", stats.TextureRgbaFallbackUploads);
        Add("textureQueuedResolves", stats.TextureQueuedResolves);
        Add("textureActiveResolves", stats.TextureActiveResolves);
        Add("texturePendingResolves", stats.TexturePendingResolves);
        Add("texturePendingUploads", stats.TexturePendingUploads);
        Add("waterDraws", stats.WaterDraws);
        if (stats.WaterPipeline is not null)
        {
            Add("waterPipeline", stats.WaterPipeline);
        }

        if (stats.WaterTechnique is not null)
        {
            Add("waterTechnique", stats.WaterTechnique);
        }

        if (stats.WaterTelemetryUnavailableReason is not null)
        {
            Add("waterFallbackReason", stats.WaterTelemetryUnavailableReason);
        }

        Add("wireframeDraws", stats.WireframeDraws);
        Add("refCellsVisited", stats.ReferenceCellsVisited);
        Add("refCandidates", stats.ReferenceCandidates);
        Add("refCulled", stats.ReferenceCulled);
        Add("refCullCacheHit", stats.ReferenceCullCacheHit);
        Add("refCullCacheVeto", stats.ReferenceCullCacheVeto);
        Add("refBatchesReused", stats.ReferenceBatchesReused);
        Add("refReuseBlocker", stats.ReferenceBatchReuseBlocker);
        Add("refBatchBuildInProgress", stats.ReferenceBatchBuildInProgress);
        Add("refCullRefreshPending", stats.ReferenceCullRefreshPending);
        Add("refBatchBuildIncrementalAdvanced", stats.ReferenceBatchBuildIncrementalAdvanced);
        Add("refBatchBuildPublished", stats.ReferenceBatchBuildPublished);
        Add("refBatchBuildSyncFallback", stats.ReferenceBatchBuildSynchronousFallback);
        Add("refBatchBuildTrigger", stats.ReferenceBatchBuildTrigger);
        Add("refBatchBuildPhase", stats.ReferenceBatchBuildPhase);
        Add("refBatchBuildProcessed", stats.ReferenceBatchBuildProcessed);
        Add("refBatchBuildTotal", stats.ReferenceBatchBuildTotal);
        Add("refBatchBuildBudgetMs", stats.ReferenceBatchBuildBudgetMilliseconds);
        Add("refMeshMissing", stats.ReferenceMeshMissing);
        Add("refShadowMeshMissing", stats.ReferenceShadowMeshMissing);
        Add("refTexturePending", stats.ReferenceTexturePending);
        Add("refDrawn", stats.ReferenceDrawn);
        Add("refSubmeshDraws", stats.ReferenceSubmeshDraws);
        Add("refSrvBinds", stats.ReferenceSrvBinds);
        Add("refCullFlips", stats.ReferenceCullModeFlips);
        Add("refMeshCacheMisses", stats.ReferenceMeshCacheMisses);
        Add("refDecodeRequests", stats.ReferenceDecodeRequests);
        Add("refQueuedDecodes", stats.ReferenceQueuedDecodes);
        Add("refDecodeStarts", stats.ReferenceDecodeStarts);
        Add("refActiveDecodes", stats.ReferenceActiveDecodes);
        Add("refCpuDecodedHits", stats.ReferenceCpuDecodedMeshCacheHits);
        Add("refCpuDecodedMisses", stats.ReferenceCpuDecodedMeshCacheMisses);
        Add("refCpuDecodedNegativeHits", stats.ReferenceCpuDecodedMeshNegativeHits);
        Add("refGpuUploads", stats.ReferenceGpuUploads);
        Add("refMeshMaterializationFailures", stats.ReferenceMeshMaterializationFailures);
        Add("refMeshMaterializationPending", stats.ReferenceMeshMaterializationRetriesPending);
        Add("refUploadByteDeferrals", stats.ReferenceUploadByteBudgetDeferrals);
        Add("refCompressedTextureUploads", stats.ReferenceCompressedTextureUploads);
        Add("refRgbaTextureUploads", stats.ReferenceRgbaTextureUploads);
        Add("refTextureQueuedResolves", stats.ReferenceTextureQueuedResolves);
        Add("refTextureActiveResolves", stats.ReferenceTextureActiveResolves);
        Add("refTexturePendingResolves", stats.ReferenceTexturePendingResolves);
        Add("refTexturePendingUploads", stats.ReferenceTexturePendingUploads);
        Add("refBatches", stats.ReferenceBatches);
        Add("refInstances", stats.ReferenceInstances);
        Add("refInstancedDraws", stats.ReferenceInstancedDraws);
        Add("refOpaqueActiveDraws", stats.ReferenceOpaqueActiveDraws);
        Add("refOpaqueSurvivingDraws", stats.ReferenceOpaqueSurvivingDraws);
        Add("refOpaquePsoTransitions", stats.ReferenceOpaquePsoTransitions);
        Add("refOpaqueUniquePsos", stats.ReferenceOpaqueUniquePsos);
        Add("refOpaqueOrdinaryDraws", stats.ReferenceOpaqueOrdinaryDraws);
        Add("refOpaqueDecalDraws", stats.ReferenceOpaqueDecalDraws);
        Add("refOpaqueGrassCutoutDraws", stats.ReferenceOpaqueGrassCutoutDraws);
        Add("refOpaqueGrassDepthWriteDraws", stats.ReferenceOpaqueGrassDepthWriteDraws);
        Add("refOpaqueIndirectActive", stats.ReferenceOpaqueIndirectActive);
        Add("refOpaqueDirectDraws", stats.ReferenceOpaqueDirectDraws);
        Add("refOpaqueIndirectDraws", stats.ReferenceOpaqueIndirectDraws);
        Add("refOpaqueIndirectExecuteCalls", stats.ReferenceOpaqueIndirectExecuteCalls);
        Add("refOpaqueIndirectArgumentBytes", stats.ReferenceOpaqueIndirectArgumentBytes);
        Add("refOpaqueIndirectFallbackReason", stats.ReferenceOpaqueIndirectFallbackReason);
        Add("refOpaqueFrontToBackActive", stats.ReferenceOpaqueFrontToBackActive);
        Add("refOpaqueFrontToBackBatches", stats.ReferenceOpaqueFrontToBackBatches);
        Add("refOpaqueFrontToBackInstances", stats.ReferenceOpaqueFrontToBackInstances);
        Add("refOpaqueFrontToBackFallbackReason", stats.ReferenceOpaqueFrontToBackFallbackReason);
        Add("refModernStandardShaderActive", stats.ReferenceModernStandardShaderActive);
        Add("refModernStandardBatches", stats.ReferenceModernStandardBatches);
        Add("refModernStandardInstances", stats.ReferenceModernStandardInstances);
        Add("refStaticOpaquePacketActive", stats.ReferenceStaticOpaquePacketActive);
        Add("refStaticOpaquePacketHit", stats.ReferenceStaticOpaquePacketHit);
        Add("refStaticOpaquePacketBatches", stats.ReferenceStaticOpaquePacketBatches);
        Add("refStaticOpaquePacketInstances", stats.ReferenceStaticOpaquePacketInstances);
        Add("refStaticOpaquePacketRuns", stats.ReferenceStaticOpaquePacketRuns);
        Add("refStaticOpaquePacketBytes", stats.ReferenceStaticOpaquePacketBytes);
        Add("refStaticOpaquePacketBuildMs", stats.ReferenceStaticOpaquePacketBuildMilliseconds);
        Add("refStaticOpaquePacketSavedMatrixBytes", stats.ReferenceStaticOpaquePacketSavedMatrixBytes);
        Add("refStaticOpaquePacketSavedConstantBytes", stats.ReferenceStaticOpaquePacketSavedConstantBytes);
        Add("refStaticOpaquePacketSavedArgumentBytes", stats.ReferenceStaticOpaquePacketSavedArgumentBytes);
        Add("refStaticOpaquePacketFallbackReason", stats.ReferenceStaticOpaquePacketFallbackReason);
        Add("refBlendedDraws", stats.ReferenceBlendedDraws);
        Add("refFnvSls1009Draws", stats.ReferenceFnvSls1009Draws);
        Add("refFnvSls1009Instances", stats.ReferenceFnvSls1009Instances);
        Add("refFnvSls1013Draws", stats.ReferenceFnvSls1013Draws);
        Add("refFnvSls1013Instances", stats.ReferenceFnvSls1013Instances);
        Add("refPlacedLightCount", stats.ReferencePlacedLightCount);
        Add("refPlacedLightTileBuildMs", stats.ReferencePlacedLightTileBuildMilliseconds);
        Add("refPlacedLightTileCount", stats.ReferencePlacedLightTileCount);
        Add("refPlacedLightTileUploadBytes", stats.ReferencePlacedLightTileUploadBytes);
        Add("refPlacedLightTileAverageLights", stats.ReferencePlacedLightTileAverageLights);
        Add("refPlacedLightTileMaxLights", stats.ReferencePlacedLightTileMaxLights);
        Add("refPlacedLightTileEmptyPercent", stats.ReferencePlacedLightTileEmptyPercent);
        if (stats.ReferencePlacedLightTileFallbackReason is not null)
        {
            Add("refPlacedLightTileFallbackReason", stats.ReferencePlacedLightTileFallbackReason);
        }
        Add("refFnvClassicBasicLightingEnabled", stats.ReferenceFnvClassicBasicLightingEnabled);
        Add("refFnvClassicBasicFallbackDraws", stats.ReferenceFnvClassicBasicFallbackDraws);
        Add("refFnvClassicBasicFallbackInstances", stats.ReferenceFnvClassicBasicFallbackInstances);
        if (stats.ReferenceFnvClassicBasicFallbackReason is not null)
        {
            Add("refFnvClassicBasicFallbackReason", stats.ReferenceFnvClassicBasicFallbackReason);
        }

        Add("refFnvActiveAdtBaseDraws", stats.ReferenceFnvActiveAdtBaseDraws);
        Add("refFnvActiveAdtBaseInstances", stats.ReferenceFnvActiveAdtBaseInstances);
        Add("refFnvActiveAdtBaseVertexColorDraws", stats.ReferenceFnvActiveAdtBaseVertexColorDraws);
        Add("refFnvActiveAdtBaseVertexColorInstances", stats.ReferenceFnvActiveAdtBaseVertexColorInstances);
        Add("refFnvActiveAdtBaseEnabled", stats.ReferenceFnvActiveAdtBaseEnabled);
        Add("refFnvActiveAdtBaseFallbackDraws", stats.ReferenceFnvActiveAdtBaseFallbackDraws);
        Add("refFnvActiveAdtBaseFallbackInstances", stats.ReferenceFnvActiveAdtBaseFallbackInstances);
        if (stats.ReferenceFnvActiveAdtBaseFallbackReason is not null)
        {
            Add("refFnvActiveAdtBaseFallbackReason", stats.ReferenceFnvActiveAdtBaseFallbackReason);
        }

        Add("refSceneDepthBlendedDraws", stats.ReferenceSceneDepthBlendedDraws);
        Add("refSoftParticleDraws", stats.ReferenceSoftParticleDraws);
        Add("refSoftParticleAuthoredDraws", stats.ReferenceSoftParticleAuthoredDraws);
        Add("refSoftParticleFallbackDraws", stats.ReferenceSoftParticleFallbackDraws);
        Add("refSoftParticleDepthSamples", stats.ReferenceSoftParticleDepthSampleCount);
        Add("refLiveParticleOwners", stats.ReferenceLiveParticleOwners);
        Add("refLiveParticleParticles", stats.ReferenceLiveParticleParticles);
        Add("refLiveParticleDraws", stats.ReferenceLiveParticleDraws);
        Add("refLiveParticleFallbacks", stats.ReferenceLiveParticleFallbacks);
        Add("refLiveParticleUploadBytes", stats.ReferenceLiveParticleUploadBytes);
        Add("refLiveParticleUvFrame", stats.ReferenceLiveParticleUvFrame);
        Add("refLiveParticleAtlasFrames", stats.ReferenceLiveParticleAtlasFrameCount);
        Add("refLiveParticleAuthoredCapacity", stats.ReferenceLiveParticleAuthoredCapacity);
        Add("refStateMs", stats.ReferenceStateSetupMilliseconds);
        Add("refCullMs", stats.ReferenceCullMilliseconds);
        Add("refBatchBuildMs", stats.ReferenceBatchBuildMilliseconds);
        // Compatibility alias for existing traces, handoff gates and analysis scripts. This field
        // historically measured the entire resolve + batch-build pass, not just GPU mesh upload.
        Add("refMeshUploadMs", stats.ReferenceBatchBuildMilliseconds);
        Add("refMeshResolveMs", stats.ReferenceMeshResolveMilliseconds);
        Add("refInstanceBucketMs", stats.ReferenceInstanceBucketingMilliseconds);
        Add("refBatchFinalizeMs", stats.ReferenceBatchFinalizeMilliseconds);
        Add("refGpuUploadMs", stats.ReferenceGpuUploadMilliseconds);
        Add("refBlendedRefreshMs", stats.ReferenceBlendedRefreshMilliseconds);
        Add("refOpaqueSubmissionMs", stats.ReferenceOpaqueSubmissionMilliseconds);
        Add("refBlendedSubmissionMs", stats.ReferenceBlendedSubmissionMilliseconds);
        Add("refCbUpdateMs", stats.ReferenceCbUpdateMilliseconds);
        Add("refSrvBindMs", stats.ReferenceSrvBindMilliseconds);
        Add("refDrawCallMs", stats.ReferenceDrawCallMilliseconds);
        return result;

        void Add(string name, object value)
        {
            result[prefix + name] = value;
        }
    }

    internal static void Close()
    {
        lock (Sync)
        {
            CloseLocked();
            _initialized = false;
            OutputPath = null;
        }
    }

    private static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        lock (Sync)
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            _sessionId = CreateSessionId();
            var path = EnvironmentVariables.Get(EnvironmentVariables.Viewer.ProfileJsonl);
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            OutputPath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(OutputPath)!);
            _writer = new StreamWriter(
                    new FileStream(OutputPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                { AutoFlush = true };
            _ownsWriter = true;
        }
    }

    private static void CloseLocked()
    {
        if (_ownsWriter)
        {
            _writer?.Dispose();
        }
        else
        {
            _writer?.Flush();
        }

        _writer = null;
        _ownsWriter = false;
    }

    private static string CreateSessionId() => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
}
