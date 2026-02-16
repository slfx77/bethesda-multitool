using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FalloutXbox360Utils.Core.VersionTracking.Models;

namespace FalloutXbox360Utils.Core.VersionTracking.Caching;

/// <summary>
///     Caches VersionSnapshot objects to disk as JSON.
///     Invalidates when source files change or schema version is bumped.
/// </summary>
public class SnapshotCache
{
    /// <summary>
    ///     Bump this when extraction or snapshot code changes.
    ///     Forces re-extraction of all cached snapshots.
    /// </summary>
    public const int SchemaVersion = 2;

    private readonly string _cacheDir;

    public SnapshotCache(string cacheDir)
    {
        _cacheDir = cacheDir;
        Directory.CreateDirectory(_cacheDir);
    }

    /// <summary>
    ///     Tries to load a cached snapshot for the given source file.
    ///     Returns null if cache miss, file changed, or schema version mismatch.
    /// </summary>
    public VersionSnapshot? TryLoad(string sourceFilePath)
    {
        var cachePath = GetCachePath(sourceFilePath);
        if (!File.Exists(cachePath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(cachePath);
            var envelope = JsonSerializer.Deserialize(json, SnapshotCacheJsonContext.Default.CacheEnvelope);
            if (envelope == null)
            {
                return null;
            }

            // Check schema version
            if (envelope.CacheSchemaVersion != SchemaVersion)
            {
                Logger.Instance.Debug($"[SnapshotCache] Schema version mismatch for {Path.GetFileName(sourceFilePath)}: " +
                                      $"cached={envelope.CacheSchemaVersion}, current={SchemaVersion}");
                return null;
            }

            // Check source file fingerprint
            var currentFingerprint = ComputeFingerprint(sourceFilePath);
            if (envelope.SourceFingerprint != currentFingerprint)
            {
                Logger.Instance.Debug($"[SnapshotCache] Source file changed: {Path.GetFileName(sourceFilePath)}");
                return null;
            }

            return envelope.Snapshot;
        }
        catch (Exception ex)
        {
            Logger.Instance.Warn($"[SnapshotCache] Failed to load cache for {Path.GetFileName(sourceFilePath)}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    ///     Saves a snapshot to the cache.
    /// </summary>
    public void Save(string sourceFilePath, VersionSnapshot snapshot)
    {
        var cachePath = GetCachePath(sourceFilePath);

        var envelope = new CacheEnvelope
        {
            CacheSchemaVersion = SchemaVersion,
            SourceFingerprint = ComputeFingerprint(sourceFilePath),
            CachedAt = DateTimeOffset.UtcNow,
            Snapshot = snapshot
        };

        try
        {
            var json = JsonSerializer.Serialize(envelope, SnapshotCacheJsonContext.Default.CacheEnvelope);
            File.WriteAllText(cachePath, json);
            Logger.Instance.Debug($"[SnapshotCache] Saved cache: {Path.GetFileName(cachePath)}");
        }
        catch (Exception ex)
        {
            Logger.Instance.Warn($"[SnapshotCache] Failed to save cache: {ex.Message}");
        }
    }

    private string GetCachePath(string sourceFilePath)
    {
        var hash = ComputePathHash(sourceFilePath);
        var fileName = Path.GetFileNameWithoutExtension(sourceFilePath);
        return Path.Combine(_cacheDir, $"{fileName}_{hash}.json");
    }

    /// <summary>
    ///     Fingerprint based on file size and last write time.
    /// </summary>
    private static string ComputeFingerprint(string filePath)
    {
        var info = new FileInfo(filePath);
        if (!info.Exists)
        {
            return "missing";
        }

        return $"{info.Length}_{info.LastWriteTimeUtc.Ticks}";
    }

    /// <summary>
    ///     Short hash of the file path for cache file naming.
    /// </summary>
    private static string ComputePathHash(string filePath)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(filePath)));
        return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
    }

    internal sealed record CacheEnvelope
    {
        public int CacheSchemaVersion { get; init; }
        public string SourceFingerprint { get; init; } = "";
        public DateTimeOffset CachedAt { get; init; }
        public VersionSnapshot? Snapshot { get; init; }
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SnapshotCache.CacheEnvelope))]
internal partial class SnapshotCacheJsonContext : JsonSerializerContext;
