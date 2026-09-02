using System.Buffers.Binary;

namespace BethesdaMultitool.Core.Imaging;

/// <summary>
///     A 256-entry colour palette for classic indexed-colour images, stored as RGBA bytes
///     (r, g, b, a per entry — the same channel order <c>DecodedTexture</c> pixels use).
///     Immutable: modifiers such as <see cref="WithTransparentIndex" /> return a new palette.
///     All entries default to alpha 255; transparency is opt-in per game convention
///     (Arena/Daggerfall and Fallout FRM both treat index 0 as transparent).
/// </summary>
internal sealed class Palette
{
    /// <summary>Entries in every palette.</summary>
    public const int EntryCount = 256;

    /// <summary>Byte length of a 256-entry RGB triplet block (256 * 3).</summary>
    public const int RgbByteCount = EntryCount * 3;

    /// <summary>Byte length of a COL palette file: 8-byte header + 768 RGB bytes.</summary>
    public const int ColFileLength = 776;

    /// <summary>Magic value in the Daggerfall COL header.</summary>
    public const ushort DaggerfallColMagic = 0xB123;

    private readonly byte[] _rgba;

    private Palette(byte[] rgba)
    {
        _rgba = rgba;
    }

    /// <summary>Raw RGBA bytes, 4 per entry, 1024 total.</summary>
    public ReadOnlySpan<byte> Rgba => _rgba;

    /// <summary>Returns entry <paramref name="index" /> as (r, g, b, a).</summary>
    public (byte R, byte G, byte B, byte A) GetEntry(int index)
    {
        if (index is < 0 or >= EntryCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var offset = index * 4;
        return (_rgba[offset], _rgba[offset + 1], _rgba[offset + 2], _rgba[offset + 3]);
    }

    /// <summary>
    ///     Builds a palette from 768 bytes of 6-bit VGA RGB triplets (component range 0..0x3F),
    ///     promoting each component with <c>rgb8 = (v &lt;&lt; 2) | (v &gt;&gt; 4)</c> so the range
    ///     endpoints map exactly (0x00 -> 0x00, 0x3F -> 0xFF). Alpha is 255 for every entry.
    /// </summary>
    public static Palette FromVga6Bit(ReadOnlySpan<byte> rgb768)
    {
        if (rgb768.Length != RgbByteCount)
        {
            throw new ArgumentException(
                $"Expected {RgbByteCount} RGB bytes, got {rgb768.Length}.", nameof(rgb768));
        }

        var rgba = new byte[EntryCount * 4];
        for (var i = 0; i < EntryCount; i++)
        {
            var src = i * 3;
            var dst = i * 4;
            rgba[dst] = PromoteVga6Bit(rgb768[src]);
            rgba[dst + 1] = PromoteVga6Bit(rgb768[src + 1]);
            rgba[dst + 2] = PromoteVga6Bit(rgb768[src + 2]);
            rgba[dst + 3] = 255;
        }

        return new Palette(rgba);
    }

    /// <summary>
    ///     Builds a palette from 768 bytes of full-range 8-bit RGB triplets, copied as-is.
    ///     Alpha is 255 for every entry.
    /// </summary>
    public static Palette FromRgb8(ReadOnlySpan<byte> rgb768)
    {
        if (rgb768.Length != RgbByteCount)
        {
            throw new ArgumentException(
                $"Expected {RgbByteCount} RGB bytes, got {rgb768.Length}.", nameof(rgb768));
        }

        var rgba = new byte[EntryCount * 4];
        for (var i = 0; i < EntryCount; i++)
        {
            var src = i * 3;
            var dst = i * 4;
            rgba[dst] = rgb768[src];
            rgba[dst + 1] = rgb768[src + 1];
            rgba[dst + 2] = rgb768[src + 2];
            rgba[dst + 3] = 255;
        }

        return new Palette(rgba);
    }

    /// <summary>
    ///     Loads an Arena COL palette file: u32 LE length (must be 776), u32 LE version
    ///     (<see cref="DaggerfallColMagic" />), then 768 bytes of FULL-RANGE 8-bit RGB.
    ///     <para>
    ///         The components are NOT 6-bit VGA values and must not be promoted. Measured on the
    ///         retail install 2026-09-01: all four shipped palettes (PAL, CHARSHT, DAYTIME,
    ///         DREARY) carry version 0xB123 and components up to 255, 255, 212 and 87 — every one
    ///         above the 6-bit ceiling of 63. Promoting them scrambles colour: the truncating
    ///         <c>(v &lt;&lt; 2) | (v &gt;&gt; 4)</c> shift turns the grey 139,127,127 into cyan
    ///         44,255,255, which is what a wrongly-promoted creature sprite looks like.
    ///     </para>
    ///     <para>
    ///         Arena and Daggerfall COL files are in fact the same format, magic included; the two
    ///         entry points exist only so callers can name the game they are reading.
    ///     </para>
    /// </summary>
    public static Palette LoadArenaCol(ReadOnlySpan<byte> file)
    {
        return LoadCol(file, "Arena");
    }

    private static Palette LoadCol(ReadOnlySpan<byte> file, string game)
    {
        if (file.Length != ColFileLength)
        {
            throw new InvalidDataException(
                $"{game} COL file must be {ColFileLength} bytes, got {file.Length}.");
        }

        var declaredLength = BinaryPrimitives.ReadUInt32LittleEndian(file);
        if (declaredLength != ColFileLength)
        {
            throw new InvalidDataException(
                $"{game} COL header declares length {declaredLength}, expected {ColFileLength}.");
        }

        var magic = BinaryPrimitives.ReadUInt16LittleEndian(file[4..]);
        if (magic != DaggerfallColMagic)
        {
            throw new InvalidDataException(
                $"{game} COL magic mismatch: 0x{magic:X4}, expected 0x{DaggerfallColMagic:X4}.");
        }

        return FromRgb8(file.Slice(8, RgbByteCount));
    }

    /// <summary>
    ///     Loads a Daggerfall COL palette file: i32 LE size, u16 LE magic 0xB123, u16 LE version,
    ///     then 768 bytes of FULL-RANGE 8-bit RGB. Identical in layout to
    ///     <see cref="LoadArenaCol" /> — neither game's components are 6-bit.
    /// </summary>
    public static Palette LoadDaggerfallCol(ReadOnlySpan<byte> file)
    {
        return LoadCol(file, "Daggerfall");
    }

    /// <summary>
    ///     Returns a copy of this palette with entry <paramref name="index" />'s alpha set to 0.
    ///     Colour channels are untouched; this palette is not modified.
    /// </summary>
    public Palette WithTransparentIndex(int index)
    {
        if (index is < 0 or >= EntryCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var rgba = (byte[])_rgba.Clone();
        rgba[index * 4 + 3] = 0;
        return new Palette(rgba);
    }

    /// <summary>
    ///     Returns a copy whose entry <c>i</c> takes its colour from entry <c>map[i]</c> — the
    ///     substitution an index-remap table performs (Arena's <c>.LGT</c> light levels are the
    ///     first user). Alpha follows the ORIGINAL index, because transparency is a property of
    ///     the source pixel and not of whatever colour it is remapped to. This palette is not
    ///     modified.
    /// </summary>
    public Palette Remapped(ReadOnlySpan<byte> map)
    {
        if (map.Length != EntryCount)
        {
            throw new ArgumentException(
                $"A palette remap table has {EntryCount} entries; got {map.Length}.", nameof(map));
        }

        var rgba = new byte[_rgba.Length];
        for (var i = 0; i < EntryCount; i++)
        {
            var source = map[i] * 4;
            rgba[(i * 4) + 0] = _rgba[source + 0];
            rgba[(i * 4) + 1] = _rgba[source + 1];
            rgba[(i * 4) + 2] = _rgba[source + 2];
            rgba[(i * 4) + 3] = _rgba[(i * 4) + 3];
        }

        return new Palette(rgba);
    }

    /// <summary>Promotes a 6-bit VGA component to 8 bits: <c>(v &lt;&lt; 2) | (v &gt;&gt; 4)</c>.</summary>
    private static byte PromoteVga6Bit(byte value)
    {
        return (byte)((value << 2) | (value >> 4));
    }
}
