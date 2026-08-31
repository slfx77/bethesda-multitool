using BethesdaMultitool.Core.Formats.Dds;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;

/// <summary>
///     Abstraction over texture lookup sources used by <see cref="NifTextureResolver" />.
/// </summary>
internal interface INifTextureSource : IDisposable
{
    DecodedTexture? TryLoad(string path);

    byte[]? TryLoadRaw(string path);

    /// <summary>
    ///     Whether <paramref name="path" /> is present in this source, WITHOUT extracting/decoding it.
    ///     Lets callers probe the loaded game's assets at load time (e.g. "does this game ship a moon
    ///     texture?") cheaply — archive sources answer from their in-memory file index, loose sources
    ///     from a filesystem stat. <paramref name="path" /> must already be normalized the same way
    ///     <see cref="TryLoadRaw" /> expects.
    /// </summary>
    bool Exists(string path);

    /// <summary>
    ///     Returns a cheap, stable identity for the source entry that would satisfy
    ///     <paramref name="path" />, without extracting its payload. Archive implementations use
    ///     their already-built file index plus container filesystem metadata; loose sources stat the
    ///     resolved file directly.
    /// </summary>
    bool TryGetAssetMetadata(string path, out NifTextureSourceAssetMetadata metadata);
}

/// <summary>
///     Filesystem and archive-record metadata sufficient to invalidate a dependent persistent cache
///     entry when its source asset changes, without hashing or extracting the asset itself.
/// </summary>
internal readonly record struct NifTextureSourceAssetMetadata(
    string SourcePath,
    long SourceLength,
    long SourceLastWriteUtcTicks,
    ulong? EntryOffset = null,
    ulong? EntryRawSize = null,
    ulong? EntrySize = null,
    ulong? EntryNameHash = null,
    uint? EntryDirectoryHash = null,
    int? EntryIndex = null);
