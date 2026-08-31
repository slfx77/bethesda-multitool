using BethesdaMultitool.Core.Formats.Bsa.Extraction;
using BethesdaMultitool.Core.Formats.Bsa.Models;
using BethesdaMultitool.Core.Formats.Dds;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;

/// <summary>
///     Indexed texture archive source used by <see cref="NifTextureResolver" />.
///     <paramref name="ownedHandle" /> is what Dispose releases: the extractor itself for a
///     private open, or the registry lease when the extractor is a shared handle.
/// </summary>
internal sealed class NifTextureArchiveSource(
    BsaExtractor extractor,
    Dictionary<string, BsaFileRecord> fileIndex,
    IDisposable ownedHandle) : INifTextureSource
{
    public BsaExtractor Extractor { get; } = extractor;

    public Dictionary<string, BsaFileRecord> FileIndex { get; } = fileIndex;

    public DecodedTexture? TryLoad(string path)
    {
        try
        {
            var rawData = TryLoadRaw(path);
            if (rawData is null)
            {
                return null;
            }

            return NifTextureLoader.DecodeTextureData(rawData);
        }
        catch
        {
            return null;
        }
    }

    public bool Exists(string path)
    {
        return FileIndex.ContainsKey(path);
    }

    public byte[]? TryLoadRaw(string path)
    {
        if (!FileIndex.TryGetValue(path, out var fileRecord))
        {
            return null;
        }

        try
        {
            return Extractor.ExtractFile(fileRecord);
        }
        catch
        {
            return null;
        }
    }

    public bool TryGetAssetMetadata(string path, out NifTextureSourceAssetMetadata metadata)
    {
        metadata = default;
        if (!FileIndex.TryGetValue(path, out var fileRecord))
        {
            return false;
        }

        try
        {
            var archive = new FileInfo(Extractor.Archive.FilePath);
            if (!archive.Exists)
            {
                return false;
            }

            metadata = new NifTextureSourceAssetMetadata(
                archive.FullName,
                archive.Length,
                archive.LastWriteTimeUtc.Ticks,
                fileRecord.Offset,
                fileRecord.RawSize,
                fileRecord.Size,
                fileRecord.NameHash);
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
