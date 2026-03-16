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
        IEnumerable<INifTextureSource> sources)
    {
        foreach (var source in sources)
        {
            var texture = source.TryLoad(path);
            if (texture != null)
            {
                return texture;
            }
        }

        return null;
    }

    internal static DecodedTexture? DecodeTextureData(byte[] data)
    {
        var ddsData = ConvertDdxIfNeeded(data);
        return DdsTextureDecoder.Decode(ddsData);
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
