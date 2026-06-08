using System.Text.Json;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera;

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
                ["timestamp"] = DateTimeOffset.Now.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
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

    internal static Dictionary<string, object?> CameraPoseFields(RendererProfilerCameraPose pose) => new()
    {
        ["cameraX"] = pose.Position.X,
        ["cameraY"] = pose.Position.Y,
        ["cameraZ"] = pose.Position.Z,
        ["cameraYaw"] = pose.Yaw,
        ["cameraPitch"] = pose.Pitch,
        ["renderDistance"] = pose.RenderDistance
    };

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
        Add("wireframeDraws", stats.WireframeDraws);
        Add("refCellsVisited", stats.ReferenceCellsVisited);
        Add("refCandidates", stats.ReferenceCandidates);
        Add("refCulled", stats.ReferenceCulled);
        Add("refMeshMissing", stats.ReferenceMeshMissing);
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
        Add("refBlendedDraws", stats.ReferenceBlendedDraws);
        Add("refStateMs", stats.ReferenceStateSetupMilliseconds);
        Add("refCullMs", stats.ReferenceCullMilliseconds);
        Add("refMeshUploadMs", stats.ReferenceMeshUploadMilliseconds);
        Add("refCbUpdateMs", stats.ReferenceCbUpdateMilliseconds);
        Add("refSrvBindMs", stats.ReferenceSrvBindMilliseconds);
        Add("refDrawCallMs", stats.ReferenceDrawCallMilliseconds);
        return result;

        void Add(string name, object value) => result[prefix + name] = value;
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
            var path = Environment.GetEnvironmentVariable("FALLOUT_VIEWER_PROFILE_JSONL");
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
}
