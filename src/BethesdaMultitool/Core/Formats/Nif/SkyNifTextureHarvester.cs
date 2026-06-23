using System.Text;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Nif;

/// <summary>
///     What celestial function a sky-dome <c>SkyShaderProperty</c> / <c>BSSkyShaderProperty</c> serves.
///     Mirrors the NIF schema enum <c>SkyObjectType</c> (see <c>nif.xml</c>). The engine
///     (<c>Sky::ReloadAllTextures</c>) keys each sky object's texture by this type, so it's the
///     grounded way to tell a climate's stars texture from its clouds / sun-glare in the model.
/// </summary>
public enum SkyObjectType
{
    /// <summary>BSSM_SKY_TEXTURE — the base atmosphere/sky texture.</summary>
    SkyTexture = 0,

    /// <summary>BSSM_SKY_SUNGLARE — the sun-glare billboard texture.</summary>
    SunGlare = 1,

    /// <summary>BSSM_SKY — the sky gradient.</summary>
    Sky = 2,

    /// <summary>BSSM_SKY_CLOUDS — a cloud layer (overridden per active weather at runtime).</summary>
    Clouds = 3,

    /// <summary>BSSM_SKY_STARS — the night-sky stars dome (the climate MODL's headline texture).</summary>
    Stars = 5,

    /// <summary>BSSM_SKY_MOON_STARS_MASK — the moon/stars fade mask (not the moon disc itself).</summary>
    MoonStarsMask = 7,
}

/// <summary>One sky-shader block harvested from a sky-dome NIF: its texture path and what it's for.</summary>
public readonly record struct SkyNifTexture(SkyObjectType Type, string FileName);

/// <summary>
///     Harvests sky-element texture paths from a Bethesda sky-dome NIF (the climate's CLMT <c>MODL</c>,
///     e.g. <c>Sky\Stars.nif</c>). Such NIFs carry each celestial texture on a sky-shader block whose
///     <c>Sky Object Type</c> classifies it (stars / clouds / sun-glare / sky). Reading those blocks lets
///     the viewer derive the stars (and other) sky textures from the actually-loaded climate model
///     instead of a hand-maintained per-game path table.
///     <para>
///         Handles BOTH the FO3/FNV <c>SkyShaderProperty</c> and the Skyrim+ <c>BSSkyShaderProperty</c>.
///         Their headers differ (and are version-dependent), but both blocks END with the same two
///         fields — a SizedString <c>FileName</c>/<c>Source Texture</c> then a uint <c>Sky Object Type</c>.
///         So the block is parsed from its END (anchor on the trailing type, then the SizedString before
///         it), which sidesteps the variable header entirely. Confirmed against the FNV PC and Skyrim LE
///         <c>Sky\Stars.nif</c> (FNV: <c>textures\sky\SkyStars.dds</c> type 5; Skyrim: four STARS blocks —
///         <c>SkyStars.dds</c> + constellations + galaxy). Big-endian (Xbox 360) NIFs work too — the
///         texture strings are ASCII and the length/type are read with the file's endianness.
///     </para>
/// </summary>
public static class SkyNifTextureHarvester
{
    private const int MaxFileNameLength = 512;

    /// <summary>
    ///     Parses <paramref name="nifData" /> and returns every sky-shader block's texture + type, in
    ///     block order. Empty when the bytes aren't a parseable NIF or contain no sky-shader blocks.
    /// </summary>
    public static IReadOnlyList<SkyNifTexture> Harvest(byte[]? nifData)
    {
        if (nifData is null || nifData.Length < 50)
        {
            return [];
        }

        var nif = NifParser.Parse(nifData);
        if (nif is null)
        {
            return [];
        }

        var be = nif.IsBigEndian;
        var result = new List<SkyNifTexture>();
        foreach (var block in nif.Blocks)
        {
            if (block.TypeName is not ("SkyShaderProperty" or "BSSkyShaderProperty"))
            {
                continue;
            }

            if (TryReadSkyShaderProperty(nifData, block.DataOffset, block.Size, be, out var type, out var fileName) &&
                !string.IsNullOrWhiteSpace(fileName))
            {
                result.Add(new SkyNifTexture(type, fileName!));
            }
        }

        return result;
    }

    /// <summary>
    ///     Reads ONLY the trailing <c>Sky Object Type</c> uint32 of a sky-shader block. Present on every
    ///     <c>SkyShaderProperty</c>/<c>BSSkyShaderProperty</c> regardless of whether it carries a texture
    ///     (the gradient atmosphere layer has an empty FileName but still a type), so the geometry
    ///     extractor can classify a sky layer even when <see cref="TryReadSkyShaderProperty" /> finds no
    ///     path. Null when the block is too small. Internal for unit testing.
    /// </summary>
    internal static SkyObjectType? ReadSkyObjectType(byte[] data, int dataOffset, int size, bool be)
    {
        if (dataOffset < 0 || size < 4)
        {
            return null;
        }

        var typePos = Math.Min(dataOffset + size, data.Length) - 4;
        if (typePos < dataOffset)
        {
            return null;
        }

        return (SkyObjectType)BinaryUtils.ReadUInt32(data, typePos, be);
    }

    /// <summary>
    ///     Reads a single sky-shader block's FileName + Sky Object Type from the block content at
    ///     <paramref name="dataOffset" /> (length <paramref name="size" />). Both <c>SkyShaderProperty</c>
    ///     (FO3/FNV) and <c>BSSkyShaderProperty</c> (Skyrim+) end with <c>[SizedString FileName][uint
    ///     SkyObjectType]</c>, so the parse anchors on the END: read the trailing type, then walk back to
    ///     the SizedString length prefix that exactly bridges to it. This avoids the version-dependent
    ///     header (FO3/FNV has a 2-byte NiProperty.Flags + 3 shader uints; Skyrim has shader-flag uints +
    ///     UV offset/scale — neither needs to be understood). Internal for unit testing.
    /// </summary>
    internal static bool TryReadSkyShaderProperty(
        byte[] data, int dataOffset, int size, bool be, out SkyObjectType type, out string? fileName)
    {
        type = default;
        fileName = null;

        if (dataOffset < 0 || size < 8)
        {
            return false;
        }

        var end = Math.Min(dataOffset + size, data.Length);
        if (end - dataOffset < 8)
        {
            return false;
        }

        // Sky Object Type is the final uint32; the FileName SizedString ends right before it.
        var strEnd = end - 4;
        var rawType = BinaryUtils.ReadUInt32(data, strEnd, be);

        // Walk back for the SizedString length prefix L whose [L][L bytes] lands exactly on strEnd. Bytes
        // of the texture path read as a uint32 are far larger than MaxFileNameLength, so the real length
        // prefix is the unique match in the content region; require a path-like ('.') ASCII payload.
        for (var lenPos = strEnd - 5; lenPos >= dataOffset; lenPos--)
        {
            var length = BinaryUtils.ReadUInt32(data, lenPos, be);
            if (length is 0 or > MaxFileNameLength)
            {
                continue;
            }

            if (lenPos + 4L + length != strEnd)
            {
                continue;
            }

            var contentStart = lenPos + 4;
            if (!IsPathLikeAscii(data, contentStart, (int)length))
            {
                continue;
            }

            fileName = Encoding.ASCII.GetString(data, contentStart, (int)length);
            type = (SkyObjectType)rawType;
            return true;
        }

        return false;
    }

    // A texture path: all bytes printable ASCII and at least one '.' (the extension). The dot rules out
    // spurious "length bridges to strEnd" matches inside a truncated/garbage block.
    private static bool IsPathLikeAscii(byte[] data, int start, int length)
    {
        var hasDot = false;
        for (var i = start; i < start + length; i++)
        {
            var b = data[i];
            if (b is < 0x20 or > 0x7E)
            {
                return false;
            }

            if (b == (byte)'.')
            {
                hasDot = true;
            }
        }

        return hasDot;
    }
}
