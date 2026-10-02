using System.Security.Cryptography;
using System.Text;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Vfs;
using Slfx77.Multitool.Core.Caching;
using Slfx77.Multitool.Core.Media;

namespace BethesdaMultitool.Core.AssetBrowse;

/// <summary>Persists only self-contained DDS/DDX thumbnails using exact readable-source provenance.</summary>
/// <remarks>The caller retains its source lease and runs this synchronous producer on a worker. A hit still
/// reads and hashes original bytes; only decoding and scaling are avoided. No source payload is persisted.</remarks>
internal sealed class DdsThumbnailProducer
{
    private const int MaximumCacheSourceBytes = 64 * 1024 * 1024;
    private const string Recipe = "bmt-dds-thumbnail/v1;DdsTextureDecoder;first-surface;base-mip;frame-0;rgba8;whole-nearest-up;alpha-weighted-box-down";
    private static readonly UTF8Encoding IdentityEncoding = new(false, true);
    private readonly ApplicationThumbnailCache _cache;

    /// <summary>Retains optional shared storage without opening, creating or reading a directory.</summary>
    /// <param name="cache">The application's shared 512 MiB default owner, or an explicitly configured test owner.</param>
    internal DdsThumbnailProducer(ApplicationThumbnailCache cache) =>
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));

    /// <summary>Reads the actual winning bytes once, then loads or produces their exact thumbnail.</summary>
    /// <param name="session">The exact session retained by the browser until this worker drains.</param>
    /// <param name="node">A real merged-VFS DDS/DDX path occurrence from that session.</param>
    /// <param name="cellPixels">The existing maximum pixel edge passed to ThumbnailScaler.</param>
    /// <param name="deviceScale">Captured finite positive presentation scale, included in the recipe identity.</param>
    /// <param name="cancellationToken">The caller/source lifetime; cancellation never becomes a cache miss.</param>
    /// <param name="cacheHit">True only when fresh pixels came from persistent storage, for scoped diagnostics.</param>
    /// <returns>The existing decoded/scaled result, or null for an unavailable/unsupported primary.</returns>
    internal RgbaThumbnail? TryRender(AssetBrowseSession session, AssetNode node, int cellPixels,
        double deviceScale, CancellationToken cancellationToken, out bool cacheHit) =>
        TryRender(session, node, cellPixels, deviceScale, cancellationToken, out cacheHit, out _);

    /// <summary>
    ///     Renders exactly as the shorter overload does and also describes the texture from the bytes it
    ///     read: a plain <c>DDS </c> header states the full size at its fixed offsets, so a cache hit
    ///     can be described without decoding. A DDX or an unreadable header leaves the description null.
    /// </summary>
    /// <param name="imageInfo">The texture's full stored size with one frame, or null when unknown or when nothing renders.</param>
    internal RgbaThumbnail? TryRender(AssetBrowseSession session, AssetNode node, int cellPixels,
        double deviceScale, CancellationToken cancellationToken, out bool cacheHit, out AssetImageInfo? imageInfo)
    {
        imageInfo = null;
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(node);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cellPixels);
        if (!double.IsFinite(deviceScale) || deviceScale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(deviceScale));
        }
        cacheHit = false;
        cancellationToken.ThrowIfCancellationRequested();
        var root = node;
        while (root.Parent is not null)
        {
            root = root.Parent;
        }
        var extension = Path.GetExtension(node.Name);
        if (!ReferenceEquals(root, session.Root) || node.Kind != AssetNodeKind.Texture ||
            !extension.Equals(".dds", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".ddx", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The thumbnail is not a texture occurrence from this source tree.", nameof(node));
        }
        var read = TryRead(session.FileSystem, node.VirtualPath);
        cancellationToken.ThrowIfCancellationRequested();
        if (read is null)
        {
            return null;
        }
        imageInfo = DescribeDdsHeader(read.Data);
        var key = TryCreateKey(session.SourcePath, node.VirtualPath, read, cellPixels, deviceScale);
        cancellationToken.ThrowIfCancellationRequested();
        var storage = key is null ? null : _cache.TryOpen(cancellationToken);
        if (storage is not null && key is not null && storage.TryRead(key, cancellationToken) is { } cached)
        {
            var result = new RgbaThumbnail(cached.Width, cached.Height, cached.Pixels.ToArray());
            cancellationToken.ThrowIfCancellationRequested();
            cacheHit = true;
            return result;
        }
        var decoded = TryDecode(read.Data);
        cancellationToken.ThrowIfCancellationRequested();
        if (decoded is null || decoded.Width <= 0 || decoded.Height <= 0)
        {
            return null;
        }
        var thumbnail = ThumbnailScaler.Fit(
            new RgbaThumbnail(decoded.Width, decoded.Height, decoded.Pixels), cellPixels);
        cancellationToken.ThrowIfCancellationRequested();
        if (storage is not null && key is not null)
        {
            storage.TryStore(key, new DecodedImage(thumbnail.Width, thumbnail.Height, thumbnail.Rgba),
                cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return thumbnail;
    }

    /// <summary>Preserves first-readable VFS resolution without substituting stat metadata or a second payload read.</summary>
    /// <param name="fileSystem">The retained source filesystem.</param>
    /// <param name="path">The original requested occurrence path.</param>
    /// <returns>Detached bytes with actual winning-layer metadata, or null under the existing read-failure policy.</returns>
    private static GameFileReadResult? TryRead(IGameFileSystem fileSystem, string path)
    {
        try
        {
            // This is the existing representable byte-array ceiling, not a thumbnail memory budget.
            // A smaller read cap could incorrectly fall through a large upper override to another layer.
            return fileSystem.TryReadAllBytesBounded(path, int.MaxValue);
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or IOException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Uses the unchanged DDS decoder, including its existing DDS-under-DDX behavior.</summary>
    /// <param name="bytes">The same exact bytes represented by the optional storage key.</param>
    /// <returns>The existing decoder result, or null under the gallery's existing failure policy.</returns>
    private static DecodedTexture? TryDecode(byte[] bytes)
    {
        try
        {
            return DdsTextureDecoder.Decode(bytes);
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or IOException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Encodes an unambiguous source/occurrence/content/recipe identity without labels or opening generations.</summary>
    /// <param name="sourcePath">The stable physical source root/archive, preserving case-sensitive spelling.</param>
    /// <param name="requestedPath">The merged VFS path occurrence; no unavailable archive ordinal is fabricated.</param>
    /// <param name="read">Actual primary metadata and exact bytes returned together by one VFS read.</param>
    /// <param name="cellPixels">The actual scaling bound.</param>
    /// <param name="deviceScale">The captured presentation scale; it does not change the existing pixel scaler.</param>
    /// <returns>A bounded key, or null when safe persistence is unavailable; decoding remains usable.</returns>
    private static ThumbnailCacheKey? TryCreateKey(string sourcePath, string requestedPath,
        GameFileReadResult read, int cellPixels, double deviceScale)
    {
        if (read.Data.Length > MaximumCacheSourceBytes || !Path.IsPathFullyQualified(sourcePath) ||
            !Path.IsPathFullyQualified(read.Entry.Source) || string.IsNullOrWhiteSpace(read.Entry.Path))
        {
            return null;
        }
        string[] fields = [Recipe, "merged-vfs-first-readable-path/v1", sourcePath,
            VfsPath.Normalize(requestedPath), read.Entry.Source, VfsPath.Normalize(read.Entry.Path)];
        try
        {
            // Four-byte lengths, then scalar fields and the fixed SHA-256 digest. Strict UTF-8 avoids
            // replacement-character aliases; oversized/invalid identities simply remain uncached.
            long length = 4 + 8 + 8 + 8 + SHA256.HashSizeInBytes;
            foreach (var field in fields)
            {
                length += 4L + IdentityEncoding.GetByteCount(field);
            }
            if (length > ThumbnailCacheKey.MaximumIdentityBytes)
            {
                return null;
            }
            using var identity = new MemoryStream((int)length);
            using var writer = new BinaryWriter(identity, IdentityEncoding, leaveOpen: true);
            foreach (var field in fields)
            {
                var bytes = IdentityEncoding.GetBytes(field);
                writer.Write(bytes.Length);
                writer.Write(bytes);
            }
            writer.Write(cellPixels);
            writer.Write(deviceScale);
            writer.Write(read.Entry.Size);
            writer.Write((long)read.Data.Length);
            writer.Write(SHA256.HashData(read.Data));
            writer.Flush();
            return new ThumbnailCacheKey(identity.GetBuffer().AsSpan(0, checked((int)identity.Length)));
        }
        catch (EncoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>
    ///     The full size a plain DDS header states (height at +12, width at +16, little-endian), or null
    ///     when the bytes do not open with the DDS magic or are too short to hold those fields.
    /// </summary>
    private static AssetImageInfo? DescribeDdsHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < 20 || data[0] != (byte)'D' || data[1] != (byte)'D' || data[2] != (byte)'S' || data[3] != (byte)' ')
        {
            return null;
        }
        var height = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        var width = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[16..]);
        return width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue
            ? null
            : AssetImageInfo.Single((int)width, (int)height);
    }
}
