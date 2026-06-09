using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     Persistent on-disk cache of decoded texture payloads (the output of the DDX→DDS transcode:
///     LZX decompression + Xenon untile + DDS parse, the documented texture streaming-hitch cost).
///     Mirrors <see cref="ReferenceDecodedMeshDiskCache12" />: the cold first run writes, every warm
///     run after reads the ready-to-upload <see cref="GpuTexturePayload" /> from disk and skips the
///     entire transcode. Keyed by (texture-source-set identity + normalized path); a changed BSA set
///     invalidates the whole cache. Enabled by default; disable with
///     <c>FALLOUT_VIEWER_PERSISTENT_TEXTURE_CACHE=0</c>.
/// </summary>
internal sealed class ReferenceDecodedTextureDiskCache12
{
    internal const int CacheFormatVersion = 1;
    // Bump whenever the decode output bytes can change (transcode/untile/parse algorithm changes).
    internal const int DecoderVersion = 1;

    private const int MaxMipLevels = 24;
    private const int MaxMipBytes = 128 * 1024 * 1024;
    private const int MaxDimension = 32_768;
    private const int MaxKeyBytes = 64 * 1024;
    private const string FileExtension = ".fdtc";
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("FNVTC12\0");

    // Soft on-disk size ceiling (default 6 GB, env FALLOUT_VIEWER_TEXTURE_CACHE_MAX_MB). Best-effort
    // background prune at construction deletes oldest files until under 80% of the cap. Larger than
    // the mesh cap because cached payloads are roughly DDS-sized (BCn) and a full game has many.
    private static readonly long MaxCacheBytes = ResolveMaxCacheBytes();

    private long _hits;
    private long _misses;
    private long _stores;

    internal ReferenceDecodedTextureDiskCache12(string cacheDirectory)
    {
        CacheDirectory = cacheDirectory;
    }

    internal string CacheDirectory { get; }

    internal long Hits => Interlocked.Read(ref _hits);
    internal long Misses => Interlocked.Read(ref _misses);
    internal long Stores => Interlocked.Read(ref _stores);

    internal static ReferenceDecodedTextureDiskCache12? CreateFromEnvironment()
    {
        if (IsDisabled(Environment.GetEnvironmentVariable("FALLOUT_VIEWER_PERSISTENT_TEXTURE_CACHE")))
        {
            return null;
        }

        var cacheDirectory = Environment.GetEnvironmentVariable("FALLOUT_VIEWER_TEXTURE_CACHE_DIR");
        if (string.IsNullOrWhiteSpace(cacheDirectory))
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(root))
            {
                root = Path.GetTempPath();
            }

            cacheDirectory = Path.Combine(
                root,
                "FalloutXbox360Utils",
                "ReferenceDecodedTextureCache12",
                $"v{DecoderVersion}");
        }

        var cache = new ReferenceDecodedTextureDiskCache12(cacheDirectory);
        cache.SchedulePrune();
        return cache;
    }

    private static long ResolveMaxCacheBytes()
    {
        const long defaultMb = 6144;
        var raw = Environment.GetEnvironmentVariable("FALLOUT_VIEWER_TEXTURE_CACHE_MAX_MB");
        var mb = long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : defaultMb;
        return mb * 1024L * 1024L;
    }

    /// <summary>Looks up a cached payload for <paramref name="keyText" />. Returns true on a cache hit
    /// (<paramref name="payload" /> non-null for a real texture, null for a cached negative/not-found).</summary>
    internal bool TryLoad(string keyText, out GpuTexturePayload? payload, out bool isNegative)
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

            var magic = reader.ReadBytes(Magic.Length);
            if (!magic.AsSpan().SequenceEqual(Magic))
            {
                throw new InvalidDataException("Texture cache magic mismatch.");
            }

            if (reader.ReadInt32() != CacheFormatVersion || reader.ReadInt32() != DecoderVersion)
            {
                throw new InvalidDataException("Texture cache version mismatch.");
            }

            var storedKey = ReadString(reader, MaxKeyBytes);
            if (!string.Equals(storedKey, keyText, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Texture cache key mismatch.");
            }

            isNegative = reader.ReadBoolean();
            if (isNegative)
            {
                Interlocked.Increment(ref _hits);
                return true;
            }

            payload = ReadPayload(reader);
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

    /// <summary>Stores a payload (or a negative for not-found) under <paramref name="keyText" />.</summary>
    internal void Store(string keyText, GpuTexturePayload? payload)
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
                writer.Write(Magic);
                writer.Write(CacheFormatVersion);
                writer.Write(DecoderVersion);
                WriteString(writer, keyText, MaxKeyBytes);
                writer.Write(payload is null);
                if (payload is not null)
                {
                    WritePayload(writer, payload);
                }
            }

            File.Move(tempPath, path, overwrite: true);
            Interlocked.Increment(ref _stores);
        }
        catch (Exception ex)
        {
            Logger.Instance.Warn("ReferenceDecodedTextureDiskCache12: cache write failed: {0}", ex.Message);
            TryDelete(tempPath);
        }
    }

    internal void SchedulePrune() => Task.Run(Prune);

    private void Prune()
    {
        try
        {
            var dir = new DirectoryInfo(CacheDirectory);
            if (!dir.Exists)
            {
                return;
            }

            var files = dir.EnumerateFiles("*" + FileExtension, SearchOption.AllDirectories).ToList();
            var total = files.Sum(static f => f.Length);
            if (total <= MaxCacheBytes)
            {
                return;
            }

            var target = (long)(MaxCacheBytes * 0.8);
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
                }
                catch
                {
                    // best-effort
                }
            }

            Logger.Instance.Info(
                "ReferenceDecodedTextureDiskCache12: pruned to {0:N0} MB (cap {1:N0} MB).",
                total / (1024 * 1024), MaxCacheBytes / (1024 * 1024));
        }
        catch (Exception ex)
        {
            Logger.Instance.Warn("ReferenceDecodedTextureDiskCache12: prune failed: {0}", ex.Message);
        }
    }

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
            "ReferenceDecodedTextureDiskCache12: {0:N0} hits, {1:N0} misses ({2:P1} hit rate), {3:N0} stores.",
            hits, misses, hits / (double)total, Stores);
    }

    private string GetCachePath(string keyText)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keyText))).ToLowerInvariant();
        return Path.Combine(CacheDirectory, hash[..2], hash + FileExtension);
    }

    private static void WritePayload(BinaryWriter writer, GpuTexturePayload payload)
    {
        writer.Write((int)payload.Format);
        writer.Write(payload.Width);
        writer.Write(payload.Height);
        ValidateRange(payload.MipCount, 1, MaxMipLevels, "MipCount");
        writer.Write(payload.MipCount);
        foreach (var level in payload.MipLevels)
        {
            writer.Write(level.Width);
            writer.Write(level.Height);
            ValidateRange(level.Bytes.Length, 0, MaxMipBytes, "MipBytes");
            writer.Write(level.Bytes.Length);
            writer.Write(level.Bytes);
        }
    }

    private static GpuTexturePayload ReadPayload(BinaryReader reader)
    {
        var formatValue = reader.ReadInt32();
        if (!Enum.IsDefined(typeof(GpuTexturePayloadFormat), formatValue))
        {
            throw new InvalidDataException("Invalid texture payload format.");
        }

        var width = ReadRange(reader, 1, MaxDimension);
        var height = ReadRange(reader, 1, MaxDimension);
        var mipCount = ReadRange(reader, 1, MaxMipLevels);
        var levels = new List<GpuTextureMipPayload>(mipCount);
        for (var i = 0; i < mipCount; i++)
        {
            var w = ReadRange(reader, 1, MaxDimension);
            var h = ReadRange(reader, 1, MaxDimension);
            var len = ReadRange(reader, 0, MaxMipBytes);
            var bytes = reader.ReadBytes(len);
            if (bytes.Length != len)
            {
                throw new EndOfStreamException();
            }

            levels.Add(new GpuTextureMipPayload(w, h, bytes));
        }

        return new GpuTexturePayload((GpuTexturePayloadFormat)formatValue, width, height, levels);
    }

    private static void WriteString(BinaryWriter writer, string value, int maxBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        ValidateRange(bytes.Length, 0, maxBytes, nameof(value));
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadString(BinaryReader reader, int maxBytes)
    {
        var length = ReadRange(reader, 0, maxBytes);
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
        {
            throw new EndOfStreamException();
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static int ReadRange(BinaryReader reader, int min, int max)
    {
        var value = reader.ReadInt32();
        ValidateRange(value, min, max, "value");
        return value;
    }

    private static void ValidateRange(int value, int min, int max, string name)
    {
        if (value < min || value > max)
        {
            throw new InvalidDataException($"{name} is out of range.");
        }
    }

    private static bool IsDisabled(string? value) =>
        string.Equals(value, "0", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "no", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "off", StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string path)
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
            // best-effort
        }
    }
}
