using System.Text.Json.Serialization;

namespace BethesdaMultitool.Core.VersionTracking.Caching;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SnapshotCache.CacheEnvelope))]
internal partial class SnapshotCacheJsonContext : JsonSerializerContext;
