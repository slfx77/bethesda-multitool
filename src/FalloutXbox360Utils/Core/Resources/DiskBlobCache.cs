using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FalloutXbox360Utils.Core.Diagnostics;

namespace FalloutXbox360Utils.Core.Resources;

/// <summary>
///     Base for persistent on-disk blob caches keyed by free-form key text — the shared skeleton of
///     the decoded mesh/texture caches. Handles everything except payload serialization:
///     <list type="bullet">
///         <item>file layout <c>{dir}/{sha256[..2]}/{sha256}{ext}</c>,</item>
///         <item>header (magic + format/decoder versions + key echo with ordinal verification),</item>
///         <item>negative entries (a cached "not found" so misses are not re-searched),</item>
///         <item>atomic writes (GUID temp file + <c>File.Move(overwrite: true)</c>),</item>
///         <item>best-effort background prune to 80% of the byte cap by last-write time,</item>
///         <item>Interlocked hit/miss/store counters and registry stats.</item>
///     </list>
///     Corrupt or version-mismatched files are deleted and reported as misses. The on-disk format
///     produced through this base is byte-identical to the pre-extraction caches — the key echo
///     self-detects any drift.
///     <para>
///         Disk caches are process-lifetime: they register with the
///         <see cref="ResourceRegistry" /> at construction and never unregister.
///         <see cref="ResourceStats.EstimatedBytes" /> reports on-disk bytes as of the last
///         prune/measure pass, and <see cref="ResourceStats.Processed" /> carries the store count.
///     </para>
/// </summary>
internal abstract class DiskBlobCache : ITrackableResource
{
    protected const int MaxKeyBytes = 64 * 1024;

    private readonly long _maxCacheBytes;
    private readonly byte[] _magic;
    private readonly int _formatVersion;
    private readonly int _decoderVersion;
    private readonly string _fileExtension;

    private long _hits;
    private long _misses;
    private long _stores;
    private long _measuredDiskBytes;
    private long _measuredFileCount;

    /// <summary>Creates a disk blob cache with the given identity stamps.</summary>
    /// <param name="resourceName">Stable name for diagnostics and log lines.</param>
    /// <param name="cacheDirectory">Root directory of the cache.</param>
    /// <param name="maxCacheBytes">Soft on-disk byte cap enforced by <see cref="SchedulePrune" />.</param>
    /// <param name="magic">File magic written at offset 0; stable per cache type.</param>
    /// <param name="formatVersion">Container layout version; a mismatch invalidates the file.</param>
    /// <param name="decoderVersion">Decoder output version; bump whenever decoded bytes can change.</param>
    /// <param name="fileExtension">Cache file extension including the dot, e.g. <c>".fdmc"</c>.</param>
    protected DiskBlobCache(
        string resourceName,
        string cacheDirectory,
        long maxCacheBytes,
        byte[] magic,
        int formatVersion,
        int decoderVersion,
        string fileExtension)
    {
        ResourceName = resourceName;
        CacheDirectory = cacheDirectory;
        _maxCacheBytes = Math.Max(1, maxCacheBytes);
        _magic = magic;
        _formatVersion = formatVersion;
        _decoderVersion = decoderVersion;
        _fileExtension = fileExtension;
    }

    /// <summary>
    ///     Registers the cache with <paramref name="registry" />. Disk caches are process-lifetime,
    ///     so the registration is intentionally never disposed.
    /// </summary>
    internal void RegisterWith(ResourceRegistry registry, string? instanceTag = null) =>
        _ = registry.Register(this, instanceTag);

    public string ResourceName { get; }

    public ResourceCategory Category => ResourceCategory.DiskCache;

    internal string CacheDirectory { get; }

    internal long Hits => Interlocked.Read(ref _hits);

    internal long Misses => Interlocked.Read(ref _misses);

    internal long Stores => Interlocked.Read(ref _stores);

    public ResourceStats GetStats() => new()
    {
        EstimatedBytes = Interlocked.Read(ref _measuredDiskBytes),
        EntryCount = Interlocked.Read(ref _measuredFileCount),
        Hits = Hits,
        Misses = Misses,
        Processed = Stores,
    };

    /// <summary>
    ///     Loads the blob stored under <paramref name="keyText" />. Returns true on a cache hit —
    ///     with <paramref name="payload" /> non-null for a real payload or null with
    ///     <paramref name="isNegative" /> set for a cached not-found. Corruption deletes the file and
    ///     reports a miss.
    /// </summary>
    protected bool TryLoadCore<T>(
        string keyText,
        Func<BinaryReader, T> readPayload,
        out T? payload,
        out bool isNegative)
        where T : class
    {
        payload = null;
        isNegative = false;
        var path = GetCachePath(keyText);
        if (!File.Exists(path))
        {
            Interlocked.Increment(ref _misses);
            return false;
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream, Encoding.UTF8);

            var stored = reader.ReadBytes(_magic.Length);
            if (!stored.AsSpan().SequenceEqual(_magic))
            {
                throw new InvalidDataException("Blob cache magic mismatch.");
            }

            if (reader.ReadInt32() != _formatVersion || reader.ReadInt32() != _decoderVersion)
            {
                throw new InvalidDataException("Blob cache version mismatch.");
            }

            var storedKey = ReadString(reader, MaxKeyBytes);
            if (!string.Equals(storedKey, keyText, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Blob cache key mismatch.");
            }

            isNegative = reader.ReadBoolean();
            if (isNegative)
            {
                Interlocked.Increment(ref _hits);
                return true;
            }

            payload = readPayload(reader);
            Interlocked.Increment(ref _hits);
            return true;
        }
        catch
        {
            TryDelete(path);
            payload = null;
            isNegative = false;
            Interlocked.Increment(ref _misses);
            return false;
        }
    }

    /// <summary>Stores <paramref name="payload" /> (or a negative entry when null) under <paramref name="keyText" />.</summary>
    protected void StoreCore<T>(string keyText, T? payload, Action<BinaryWriter, T> writePayload)
        where T : class
    {
        var path = GetCachePath(keyText);
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        var tempPath = path + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp";
        try
        {
            using (var stream = File.Create(tempPath))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(_magic);
                writer.Write(_formatVersion);
                writer.Write(_decoderVersion);
                WriteString(writer, keyText, MaxKeyBytes);
                writer.Write(payload is null);
                if (payload is not null)
                {
                    writePayload(writer, payload);
                }
            }

            File.Move(tempPath, path, overwrite: true);
            Interlocked.Increment(ref _stores);
        }
        catch (Exception ex)
        {
            Logger.Instance.Warn("{0}: cache write failed: {1}", ResourceName, ex.Message);
            TryDelete(tempPath);
        }
    }

    /// <summary>
    ///     Best-effort background size-cap enforcement: measures the cache directory and, when it
    ///     exceeds the cap, deletes the oldest files (by last-write time) until under 80% of the cap.
    ///     Runs off-thread so it never blocks startup; failures are swallowed.
    /// </summary>
    internal void SchedulePrune() => Task.Run(Prune);

    internal void LogStatistics()
    {
        var hits = Hits;
        var misses = Misses;
        var total = hits + misses;
        if (total == 0)
        {
            return;
        }

        Logger.Instance.Info(
            "{0}: {1:N0} hits, {2:N0} misses ({3:P1} hit rate), {4:N0} stores.",
            ResourceName, hits, misses, hits / (double)total, Stores);
    }

    protected static void WriteString(BinaryWriter writer, string value, int maxBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        ValidateRange(bytes.Length, 0, maxBytes, nameof(value));
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    protected static string ReadString(BinaryReader reader, int maxBytes)
    {
        var length = ReadInt32(reader, 0, maxBytes);
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
        {
            throw new EndOfStreamException();
        }

        return Encoding.UTF8.GetString(bytes);
    }

    protected static void WriteNullableString(BinaryWriter writer, string? value, int maxBytes)
    {
        if (value is null)
        {
            writer.Write(-1);
            return;
        }

        WriteString(writer, value, maxBytes);
    }

    protected static string? ReadNullableString(BinaryReader reader, int maxBytes)
    {
        var length = ReadInt32(reader, -1, maxBytes);
        if (length < 0)
        {
            return null;
        }

        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
        {
            throw new EndOfStreamException();
        }

        return Encoding.UTF8.GetString(bytes);
    }

    protected static int ReadInt32(BinaryReader reader, int min, int max)
    {
        var value = reader.ReadInt32();
        ValidateRange(value, min, max, nameof(value));
        return value;
    }

    protected static void ValidateRange(int value, int min, int max, string name)
    {
        if (value < min || value > max)
        {
            throw new InvalidDataException($"{name} is out of range.");
        }
    }

    protected static bool IsDisabled(string? value) =>
        string.Equals(value, "0", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "no", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "off", StringComparison.OrdinalIgnoreCase);

    protected static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Cache cleanup is best-effort; a failed delete must never break the caller.
        }
    }

    /// <summary>Resolves the on-disk path for <paramref name="keyText" /> (<c>{dir}/{sha[..2]}/{sha}{ext}</c>).</summary>
    protected string GetCachePath(string keyText)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keyText))).ToLowerInvariant();
        return Path.Combine(CacheDirectory, hash[..2], hash + _fileExtension);
    }

    private void Prune()
    {
        try
        {
            var dir = new DirectoryInfo(CacheDirectory);
            if (!dir.Exists)
            {
                Interlocked.Exchange(ref _measuredDiskBytes, 0);
                Interlocked.Exchange(ref _measuredFileCount, 0);
                return;
            }

            var files = dir.EnumerateFiles("*" + _fileExtension, SearchOption.AllDirectories).ToList();
            var total = files.Sum(static f => f.Length);
            var count = (long)files.Count;
            if (total > _maxCacheBytes)
            {
                var target = (long)(_maxCacheBytes * 0.8);
                foreach (var f in files.OrderBy(static f => f.LastWriteTimeUtc))
                {
                    if (total <= target)
                    {
                        break;
                    }

                    try
                    {
                        var len = f.Length;
                        f.Delete();
                        total -= len;
                        count--;
                    }
                    catch
                    {
                        // Best-effort; skip files we can't delete (in use, perms).
                    }
                }

                Logger.Instance.Info(
                    "{0}: pruned to {1:N0} MB (cap {2:N0} MB).",
                    ResourceName, total / (1024 * 1024), _maxCacheBytes / (1024 * 1024));
            }

            Interlocked.Exchange(ref _measuredDiskBytes, total);
            Interlocked.Exchange(ref _measuredFileCount, count);
        }
        catch (Exception ex)
        {
            Logger.Instance.Warn("{0}: prune failed: {1}", ResourceName, ex.Message);
        }
    }
}
