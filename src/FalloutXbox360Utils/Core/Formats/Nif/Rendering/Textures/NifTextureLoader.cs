using DDXConv;
using FalloutXbox360Utils.Core.Formats.Dds;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Textures;

/// <summary>
///     Loads and decodes textures from indexed BSA sources.
/// </summary>
internal static class NifTextureLoader
{
    internal static DecodedTexture? TryLoadFromSources(
        string path,
        IEnumerable<NifTextureArchiveSource> sources)
    {
        foreach (var source in sources)
        {
            if (!source.FileIndex.TryGetValue(path, out var fileRecord))
            {
                continue;
            }

            try
            {
                var rawData = source.Extractor.ExtractFile(fileRecord);
                var ddsData = ConvertDdxIfNeeded(rawData);
                return DdsTextureDecoder.Decode(ddsData);
            }
            catch
            {
                // Try the next archive.
            }
        }

        return null;
    }

    /// <summary>
     ///     If the data is a DDX texture (Xbox 360 format), convert it to DDS in memory.
     /// </summary>
    private static byte[] ConvertDdxIfNeeded(byte[] data)
    {
        if (data.Length < 4)
        {
            return data;
        }

        var is3Xdo = data[0] == '3' &&
                     data[1] == 'X' &&
                     data[2] == 'D' &&
                     data[3] == 'O';
        var is3Xdr = data[0] == '3' &&
                     data[1] == 'X' &&
                     data[2] == 'D' &&
                     data[3] == 'R';

        if (!is3Xdo && !is3Xdr)
        {
            return data;
        }

        try
        {
            var parser = new DdxParser();
            return parser.ConvertDdxToDds(data);
        }
        catch
        {
            return data;
        }
    }
}
