using System.Globalization;
using System.Text;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Resources;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     Persistent on-disk cache of decoded texture payloads (the output of the DDX→DDS transcode:
///     LZX decompression + Xenon untile + DDS parse, the documented texture streaming-hitch cost).
///     Mirrors <see cref="ReferenceDecodedMeshDiskCache12" />: the cold first run writes, every warm
///     run after reads the ready-to-upload <see cref="GpuTexturePayload" /> from disk and skips the
///     entire transcode. Keyed by (texture-source-set identity + normalized path); a changed BSA set
///     invalidates the whole cache. Enabled by default under the OS temp directory; disable with
///     <c>FALLOUT_VIEWER_PERSISTENT_TEXTURE_CACHE=0</c>.
///     <para>
///         Container handling (header, key echo, negatives, atomic writes, prune, stats) lives in
///         <see cref="DiskBlobCache" />; this type owns only the payload serialization and the
///         env-driven construction. The on-disk format is byte-identical to the pre-extraction
///         implementation — existing warm caches stay valid.
///     </para>
/// </summary>
internal sealed class ReferenceDecodedTextureDiskCache12 : DiskBlobCache
{
    internal const int CacheFormatVersion = 1;
    // Bump whenever the decode output bytes can change (transcode/untile/parse algorithm changes), OR
    // when the set of paths that resolve changes — a cached negative ("not found") would otherwise mask
    // a newly-resolvable path. v2: FO4/FO76 .bgsm/.bgem materials now resolve in the GPU path and
    // absolute "…\Data\…" build paths are peeled, so pre-fix negative entries must be discarded.
    // v3: BC4U/BC5U/DX10(BC1-BC7) DDS headers now parse into native BCn payloads; cached v2 entries
    // hold the uncompressed-RGBA fallback, which for BC5 normal maps lacks the ReconstructZ mode.
    // v4: cubemap payloads (FO4 environment maps) — the serialized format gains ArraySize and
    // writes MipCount × ArraySize levels (v3 wrote MipCount then iterated ALL levels, which would
    // desync on a 6-face payload).
    internal const int DecoderVersion = 4;

    private const int MaxMipLevels = 24;
    private const int MaxMipBytes = 128 * 1024 * 1024;
    private const int MaxDimension = 32_768;
    private const string FileExtension = ".fdtc";
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("FNVTC12\0");

    // Soft on-disk size ceiling (default 6 GB, env FALLOUT_VIEWER_TEXTURE_CACHE_MAX_MB). Best-effort
    // background prune at construction deletes oldest files until under 80% of the cap. Larger than
    // the mesh cap because cached payloads are roughly DDS-sized (BCn) and a full game has many.
    private static readonly long MaxCacheBytes = ResolveMaxCacheBytes();

    internal ReferenceDecodedTextureDiskCache12(string cacheDirectory)
        : base(
            nameof(ReferenceDecodedTextureDiskCache12), cacheDirectory, MaxCacheBytes,
            Magic, CacheFormatVersion, DecoderVersion, FileExtension)
    {
    }

    internal static ReferenceDecodedTextureDiskCache12? CreateFromEnvironment()
    {
        if (IsDisabled(EnvironmentVariables.Get(EnvironmentVariables.Viewer.PersistentTextureCache)))
        {
            return null;
        }

        var cacheDirectory = EnvironmentVariables.Get(EnvironmentVariables.Viewer.TextureCacheDirectory);
        if (string.IsNullOrWhiteSpace(cacheDirectory))
        {
            cacheDirectory = ReferenceDiskCachePaths.ResolveDefaultCacheDirectory(
                "ReferenceDecodedTextureCache12",
                DecoderVersion);
        }

        var cache = new ReferenceDecodedTextureDiskCache12(cacheDirectory);
        cache.RegisterWith(ResourceRegistry.Instance);
        cache.SchedulePrune();
        return cache;
    }

    private static long ResolveMaxCacheBytes()
    {
        const long defaultMb = 6144;
        var raw = EnvironmentVariables.Get(EnvironmentVariables.Viewer.TextureCacheMaxMegabytes);
        var mb = long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : defaultMb;
        return mb * 1024L * 1024L;
    }

    /// <summary>Looks up a cached payload for <paramref name="keyText" />. Returns true on a cache hit
    /// (<paramref name="payload" /> non-null for a real texture, null for a cached negative/not-found).</summary>
    internal bool TryLoad(string keyText, out GpuTexturePayload? payload, out bool isNegative) =>
        TryLoadCore(keyText, ReadPayload, out payload, out isNegative);

    /// <summary>Stores a payload (or a negative for not-found) under <paramref name="keyText" />.</summary>
    internal void Store(string keyText, GpuTexturePayload? payload) =>
        StoreCore(keyText, payload, WritePayload);

    private static void WritePayload(BinaryWriter writer, GpuTexturePayload payload)
    {
        writer.Write((int)payload.Format);
        writer.Write(payload.Width);
        writer.Write(payload.Height);
        ValidateRange(payload.ArraySize, 1, 6, "ArraySize");
        writer.Write(payload.ArraySize);
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

        var width = ReadInt32(reader, 1, MaxDimension);
        var height = ReadInt32(reader, 1, MaxDimension);
        var arraySize = ReadInt32(reader, 1, 6);
        var mipCount = ReadInt32(reader, 1, MaxMipLevels) * arraySize;
        var levels = new List<GpuTextureMipPayload>(mipCount);
        for (var i = 0; i < mipCount; i++)
        {
            var w = ReadInt32(reader, 1, MaxDimension);
            var h = ReadInt32(reader, 1, MaxDimension);
            var len = ReadInt32(reader, 0, MaxMipBytes);
            var bytes = reader.ReadBytes(len);
            if (bytes.Length != len)
            {
                throw new EndOfStreamException();
            }

            levels.Add(new GpuTextureMipPayload(w, h, bytes));
        }

        return new GpuTexturePayload((GpuTexturePayloadFormat)formatValue, width, height, levels, arraySize);
    }
}
