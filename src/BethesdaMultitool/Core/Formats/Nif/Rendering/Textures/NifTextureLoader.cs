using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Ddx;
using DDXConv;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;

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
    internal static byte[] ConvertDdxIfNeeded(byte[] data)
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

    /// <summary>
    ///     Converts an Xbox 360 normal map and, when present, folds its separately-authored
    ///     <c>*_s.ddx</c> specular companion into the DXT5 alpha channel used by the FO3/FNV
    ///     material shader. The 360 corpus stores tangent-space XY as BC5/ATI2 in <c>*_n.ddx</c>
    ///     and the per-texel specular intensity as BC4/ATI1 in <c>*_s.ddx</c>; converting only the
    ///     first file leaves BC5 with no alpha and therefore drops the authored highlight.
    ///     <para>
    ///         A missing or unusable companion deliberately preserves the converted BC5 payload.
    ///         The D3D12 classic-material shader treats BC5-without-a-specular-map as a zero mask;
    ///         running the no-companion PC repack conversion here would produce BC1 whose sampled
    ///         alpha is one, incorrectly turning the absence of a mask into full-surface specular.
    ///     </para>
    /// </summary>
    internal static byte[] ConvertDdxNormalPairIfNeeded(
        byte[] data,
        string path,
        Func<string, byte[]?> loadCompanion)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(loadCompanion);

        var normalDds = ConvertDdxIfNeeded(data);
        // Pairing is an Xbox DDX storage convention, not a generic BC5 convention. Modern PC
        // materials legitimately bind `_n.dds` BC5 and `_s.dds` as two independent shader slots;
        // folding those together here would flatten their reconstructed normal Z and duplicate the
        // specular input. The live resolver passes the actual archive path, including its `.ddx`
        // fallback spelling, so the extension is the generation boundary we need.
        if (!path.EndsWith(".ddx", StringComparison.OrdinalIgnoreCase) ||
            !NormalMapMerge.IsNormalMapPath(path) ||
            !NormalMapMerge.IsAti2(normalDds))
        {
            return normalDds;
        }

        var specularPath = NormalMapMerge.ComputeSpecularPath(path);
        var specularRaw = specularPath is null ? null : loadCompanion(specularPath);
        if (specularRaw is null)
        {
            return normalDds;
        }

        try
        {
            var specularDds = ConvertDdxIfNeeded(specularRaw);
            var merged = DdsPostProcessor.MergeNormalSpecularMapsFromMemory(normalDds, specularDds);
            // The shared converter intentionally falls back to BC1 when the companion dimensions
            // do not match. That spelling is correct for a repacked PC game, whose runtime knows an
            // alpha-less normal means "no specular", but this viewer samples BC1 alpha as one. Only
            // accept the paired result when it really produced the expected DXT5 alpha lane.
            return HasDdsFourCc(merged, "DXT5") ? merged : normalDds;
        }
        catch
        {
            // A malformed/mismatched companion must not discard an otherwise usable BC5 normal.
            return normalDds;
        }
    }

    private static bool HasDdsFourCc(ReadOnlySpan<byte> data, ReadOnlySpan<char> fourCc)
    {
        return data.Length >= 88 && fourCc.Length == 4 &&
               data[84] == (byte)fourCc[0] &&
               data[85] == (byte)fourCc[1] &&
               data[86] == (byte)fourCc[2] &&
               data[87] == (byte)fourCc[3];
    }
}
