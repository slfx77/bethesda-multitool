using BethesdaMultitool.Core.Formats.Bsa.Ba2;
using BethesdaMultitool.Core.Formats.Dds;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;

/// <summary>
///     Indexed texture archive source backed by a BA2 (Fallout 4 / Fallout 76) archive. The BA2
///     parallel to <see cref="NifTextureArchiveSource" />: same <see cref="INifTextureSource" />
///     contract, so the resolver treats BSA- and BA2-sourced textures identically. DX10 entries are
///     extracted as full .dds (synthesized header + chunks) by <see cref="Ba2Extractor" />.
///     <paramref name="ownedHandle" /> is what Dispose releases: the extractor itself for a
///     private open, or the registry lease when the extractor is a shared handle.
/// </summary>
internal sealed class Ba2TextureArchiveSource(
    Ba2Extractor extractor,
    Dictionary<string, Ba2FileRecord> fileIndex,
    IDisposable ownedHandle) : INifTextureSource
{
    public DecodedTexture? TryLoad(string path)
    {
        try
        {
            var rawData = TryLoadRaw(path);
            return rawData is null ? null : NifTextureLoader.DecodeTextureData(rawData);
        }
        catch
        {
            return null;
        }
    }

    public bool Exists(string path)
    {
        return fileIndex.ContainsKey(path);
    }

    public byte[]? TryLoadRaw(string path)
    {
        if (!fileIndex.TryGetValue(path, out var fileRecord))
        {
            return null;
        }

        try
        {
            return extractor.ExtractFile(fileRecord);
        }
        catch
        {
            return null;
        }
    }

    public bool TryGetAssetMetadata(string path, out NifTextureSourceAssetMetadata metadata)
    {
        metadata = default;
        if (!fileIndex.TryGetValue(path, out var fileRecord))
        {
            return false;
        }

        try
        {
            var archive = new FileInfo(extractor.Archive.FilePath);
            if (!archive.Exists)
            {
                return false;
            }

            metadata = new NifTextureSourceAssetMetadata(
                archive.FullName,
                archive.Length,
                archive.LastWriteTimeUtc.Ticks,
                fileRecord.Offset,
                fileRecord.PackedSize,
                fileRecord.RealSize,
                fileRecord.NameHash,
                fileRecord.DirHash,
                fileRecord.Index);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        ownedHandle.Dispose();
    }
}
