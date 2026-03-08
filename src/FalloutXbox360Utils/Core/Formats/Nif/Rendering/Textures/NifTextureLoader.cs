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
                if (ddsData.Length >= 128)
                {
                    var width = BitConverter.ToUInt32(ddsData, 16);
                    var height = BitConverter.ToUInt32(ddsData, 12);
                    var fourCc = System.Text.Encoding.ASCII
                        .GetString(ddsData, 84, 4)
                        .TrimEnd('\0');
                    Console.Error.WriteLine(
                        $"[TEX] {path}: raw={rawData.Length}B dds={ddsData.Length}B {width}x{height} {fourCc}");
                }

                var decoded = DdsTextureDecoder.Decode(ddsData);
                LogOutfitSamples(path, decoded);
                return decoded;
            }
            catch
            {
                // Try the next archive.
            }
        }

        return null;
    }

    private static void LogOutfitSamples(string path, DecodedTexture? decoded)
    {
        if (decoded == null ||
            !path.Contains("outfitf", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var centerX = decoded.Width / 2;
        var centerY = decoded.Height / 2;
        for (var sampleY = -2; sampleY <= 2; sampleY++)
        {
            var pixelIndex = ((centerY + sampleY * 50) * decoded.Width + centerX) * 4;
            if (pixelIndex < 0 || pixelIndex + 3 >= decoded.Pixels.Length)
            {
                continue;
            }

            Console.Error.WriteLine(
                $"[TEX-SAMPLE] outfitf pixel ({centerX},{centerY + sampleY * 50}): " +
                $"R={decoded.Pixels[pixelIndex]} " +
                $"G={decoded.Pixels[pixelIndex + 1]} " +
                $"B={decoded.Pixels[pixelIndex + 2]} " +
                $"A={decoded.Pixels[pixelIndex + 3]}");
        }
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
