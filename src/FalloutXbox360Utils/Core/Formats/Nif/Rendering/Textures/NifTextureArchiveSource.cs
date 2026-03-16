using FalloutXbox360Utils.Core.Formats.Bsa;
using FalloutXbox360Utils.Core.Formats.Dds;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Textures;

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
        if (!FileIndex.TryGetValue(path, out var fileRecord))
        {
            return null;
        }

        try
        {
            var rawData = Extractor.ExtractFile(fileRecord);
            return NifTextureLoader.DecodeTextureData(rawData);
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
