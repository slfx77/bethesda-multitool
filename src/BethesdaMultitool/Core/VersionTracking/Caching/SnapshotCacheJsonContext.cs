using System.Text.Json.Serialization;

namespace BethesdaMultitool.Core.VersionTracking.Caching;

/// <summary>Source-generated JSON serializer context for the snapshot cache envelope (trim-safe serialization).</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SnapshotCache.CacheEnvelope))]
internal partial class SnapshotCacheJsonContext : JsonSerializerContext;
