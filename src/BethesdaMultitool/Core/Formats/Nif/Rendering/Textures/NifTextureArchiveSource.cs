using BethesdaMultitool.Core.Formats.Bsa.Extraction;
using BethesdaMultitool.Core.Formats.Bsa.Models;
using BethesdaMultitool.Core.Formats.Bsa;
using BethesdaMultitool.Core.Formats.Dds;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;

/// <summary>
///     Indexed texture archive source used by <see cref="NifTextureResolver" />.
/// </summary>
internal sealed class NifTextureArchiveSource(
    BsaExtractor extractor,
    Dictionary<string, BsaFileRecord> fileIndex) : INifTextureSource
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

    public bool Exists(string path) => FileIndex.ContainsKey(path);

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

    public void Dispose()
    {
        Extractor.Dispose();
    }
}
