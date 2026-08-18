namespace BethesdaMultitool.Core.Formats.Ddx;

/// <summary>
///     Single authority for the Xbox 360 → PC normal-map convention.
///     <para>
///         The 360 stores a normal map as a 2-channel BC5/ATI2 <c>*_n.ddx</c> with the specular
///         mask in a separate <c>*_s.ddx</c>. Vanilla PC Fallout NV instead ships one DXT5
///         <c>*_n.dds</c> carrying the reconstructed 3-channel normal in RGB and the specular
///         mask in alpha — FNV's runtime DDS loader does not accept ATI2 at all. Converting a
///         360 texture pair therefore means merging the two files, not just transcoding them.
///     </para>
///     <para>
///         Every caller must agree on which files pair up, or the merge silently falls back to
///         a flat gray-128 alpha and every surface renders with a uniform 50% specular mask.
///     </para>
/// </summary>
public static class NormalMapMerge
{
    private const string NormalSuffix = "_n";
    private const string SpecularSuffix = "_s";

    /// <summary>
    ///     True when the path names a normal map by the <c>_n</c> filename convention.
    /// </summary>
    public static bool IsNormalMapPath(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        return stem.EndsWith(NormalSuffix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Maps a normal-map path to its specular companion in the same directory, preserving the
    ///     original extension (<c>.ddx</c> for a 360 source, <c>.dds</c> for converted output).
    ///     Returns null when the path is not a normal map.
    /// </summary>
    public static string? ComputeSpecularPath(string normalPath)
    {
        if (!IsNormalMapPath(normalPath))
        {
            return null;
        }

        var dir = Path.GetDirectoryName(normalPath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(normalPath);
        var specName = stem[..^NormalSuffix.Length] + SpecularSuffix + Path.GetExtension(normalPath);
        return string.IsNullOrEmpty(dir) ? specName : Path.Combine(dir, specName);
    }

    /// <summary>
    ///     True when the DDS bytes carry the ATI2/BC5 four-CC, i.e. the 2-channel form the PC
    ///     engine cannot load. The four-CC lives at offset 84 of the DDS header.
    /// </summary>
    public static bool IsAti2(ReadOnlySpan<byte> dds)
    {
        return dds.Length >= 88
               && dds[84] == (byte)'A'
               && dds[85] == (byte)'T'
               && dds[86] == (byte)'I'
               && dds[87] == (byte)'2';
    }
}
