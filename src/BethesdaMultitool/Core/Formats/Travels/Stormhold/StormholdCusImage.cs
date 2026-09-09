using System.Buffers.Binary;
using System.Collections.Immutable;
using BethesdaMultitool.Core.Imaging;

namespace BethesdaMultitool.Core.Formats.Travels.Stormhold;

/// <summary>
///     A Stormhold <c>.cus</c> sprite — the "custom" image resource The Elder Scrolls Travels:
///     Stormhold (J2ME, 2003) stores loose inside its MIDlet JAR. Original RE from the bytes
///     (2026-09-05); no third-party reader for this format exists.
///     <para>
///         One 8-bit indexed image with its own small palette, big-endian throughout (the J2ME
///         <c>DataInputStream</c> house rule), and no magic at all:
///     </para>
///     <list type="bullet">
///         <item>0: i32 width <c>W</c> — 12..212 on retail.</item>
///         <item>4: i32 height <c>H</c> — 10..158 on retail.</item>
///         <item>8: u8 transparency flag; non-zero means a key colour is in effect (1 on 37/37).</item>
///         <item>9: u16 key colour, in the same 0RGB 4:4:4 encoding as a palette entry.</item>
///         <item>11: u8 palette entry count <c>N</c> — a byte, so the format bound is 0..255.</item>
///         <item>12: <c>N</c> x u16 palette entries, 0RGB 4:4:4.</item>
///         <item>12 + 2N: <c>W*H</c> bytes of palette indices, row-major, top row first.</item>
///     </list>
///     <para>
///         The layout tiles the file EXACTLY — <c>12 + 2N + W*H == length</c> on all 37 retail
///         files (measured 2026-09-05, 156 to 16,634 bytes) — which is the whole content probe,
///         since there is no signature to check. There is no trailer, no frame table and no
///         compression; a 4-bit-packed reading does not tile any retail file, so
///         <c>wardenheads4bit.cus</c> is 8 bits per pixel like the rest and the "4bit" in its name
///         only describes the art's colour budget.
///     </para>
///     <para>
///         <b>Palette entries are BIG-endian u16 with the top nibble clear</b> (581/581 retail
///         entries, so the 0RGB reading is safe) and expand
///         <c>R = bits 11-8, G = bits 7-4, B = bits 3-0</c>. That channel order is NOT decidable
///         from the bytes alone — RGB and BGR both produce plausible-looking sprites. It is
///         settled by the draw contract: the game hands these pixels to the Nokia
///         <c>DirectGraphics</c> <c>TYPE_USHORT_4444_ARGB</c> path after forcing the alpha nibble,
///         and that constant fixes the nibble order as A,R,G,B. The skin-tone statistic agrees —
///         under this order the trainer and warden body sheets decode to entries whose red
///         dominates green dominates blue, the ordering human skin has; under BGR they come out
///         blue.
///     </para>
///     <para>
///         Each 4-bit component is widened by nibble replication (<c>c8 = c4 * 17</c>, so 0xF maps
///         to 255 and 0x8 to 136), the standard 4444-&gt;8888 conversion. The exact expansion the
///         phone's runtime used is device-defined and unrecoverable, so this is a choice, not a
///         measurement.
///     </para>
///     <para>
///         <b>Transparency trap:</b> the transparent slot is the FIRST palette index whose entry
///         equals the key colour, and it is not always slot 0. Retail census: slot 0 in 34 files,
///         slot 4 in <c>trainerfembelt1</c> and <c>trainerfemneck</c>, slot 2 in
///         <c>wardenbody2f2half</c>. No retail file has duplicate palette entries, so "first
///         match" is unambiguous. Only that one index is transparent — the format carries no
///         partial alpha. A zero flag byte, or a key colour absent from the palette, is legal and
///         leaves <see cref="TransparentIndex" /> at -1 (neither is observed on retail, so that
///         path is untested against real data).
///     </para>
///     <para>
///         Retail censuses worth pinning: <c>N</c> is 16 in 31 files, 15 in 4, 13 in 1 and 12 in 1;
///         the key colour is 0x0FB2 in 32 files, 0x000F in 4 and 0x0F0F in 1, and is present in the
///         palette on 37/37; every pixel byte is below its file's <c>N</c> on 37/37.
///     </para>
/// </summary>
internal sealed class StormholdCusImage
{
    /// <summary>Bytes before the palette: i32 W, i32 H, u8 flag, u16 key, u8 palette count.</summary>
    public const int HeaderLength = 12;

    /// <summary>Bytes per palette entry (a big-endian u16).</summary>
    public const int PaletteEntryLength = 2;

    /// <summary>Byte offset of the palette entry count.</summary>
    public const int PaletteCountOffset = 11;

    /// <summary>
    ///     Upper bound the probe accepts for either dimension. Retail tops out at 212x158; the
    ///     cap exists so a run of plausible-looking garbage cannot claim a multi-megabyte image.
    /// </summary>
    public const int MaxDimension = 4096;

    private readonly byte[] _indices;

    private StormholdCusImage(
        string name,
        int width,
        int height,
        bool hasKeyColour,
        ushort keyColour444,
        ImmutableArray<ushort> palette444,
        int transparentIndex,
        Palette palette,
        byte[] indices)
    {
        Name = name;
        Width = width;
        Height = height;
        HasKeyColour = hasKeyColour;
        KeyColour444 = keyColour444;
        Palette444 = palette444;
        TransparentIndex = transparentIndex;
        Palette = palette;
        _indices = indices;
        Bitmap = new IndexedBitmap(width, height, indices);
    }

    /// <summary>Resource name as found in the JAR, for messages.</summary>
    public string Name { get; }

    /// <summary>Image width in pixels, and the stride of the index plane.</summary>
    public int Width { get; }

    /// <summary>Image height in pixels.</summary>
    public int Height { get; }

    /// <summary>Whether the transparency flag byte at offset 8 is non-zero.</summary>
    public bool HasKeyColour { get; }

    /// <summary>The raw 0RGB 4:4:4 key colour, read whether or not the flag is set.</summary>
    public ushort KeyColour444 { get; }

    /// <summary>The <c>N</c> raw palette entries, in file order, still 0RGB 4:4:4.</summary>
    public ImmutableArray<ushort> Palette444 { get; }

    /// <summary>
    ///     The first palette slot equal to <see cref="KeyColour444" />, or -1 when the flag is
    ///     clear or the key is not in the palette. Never assume 0 — see the type remarks.
    /// </summary>
    public int TransparentIndex { get; }

    /// <summary>
    ///     The 256-entry RGBA palette: slots <c>0..N-1</c> are the expanded entries with alpha 255
    ///     (alpha 0 on <see cref="TransparentIndex" />), slots <c>N..255</c> transparent black.
    ///     Those tail slots are unreachable — the parser rejects any pixel index at or above
    ///     <c>N</c> — and exist only so the palette is a well-formed 256-entry block.
    /// </summary>
    public Palette Palette { get; }

    /// <summary>The whole image as 8-bit indices; the file's pixel bytes need no remapping.</summary>
    public IndexedBitmap Bitmap { get; }

    /// <summary>
    ///     Content probe for a format with no magic: the header arithmetic must tile the file
    ///     exactly (<c>12 + 2N + W*H == length</c>) with both dimensions in 1..<see cref="MaxDimension" />.
    ///     Swept 2026-09-05 over the 2,147 non-<c>.cus</c> files in the four Travels fixture trees
    ///     (JAR classes, PNGs, <c>.dat</c> tables, manifests): ZERO false positives.
    /// </summary>
    public static bool TryProbe(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderLength)
        {
            return false;
        }

        var width = BinaryPrimitives.ReadInt32BigEndian(bytes);
        var height = BinaryPrimitives.ReadInt32BigEndian(bytes[4..]);
        if (width is < 1 or > MaxDimension || height is < 1 or > MaxDimension)
        {
            return false;
        }

        var expected = HeaderLength
                       + (long)PaletteEntryLength * bytes[PaletteCountOffset]
                       + (long)width * height;
        return expected == bytes.Length;
    }

    /// <summary>
    ///     Parses one <c>.cus</c>, throwing <see cref="InvalidDataException" /> — naming the file
    ///     and the offending byte position — when the layout does not tile or an index escapes the
    ///     palette.
    /// </summary>
    public static StormholdCusImage Parse(ReadOnlySpan<byte> bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (bytes.Length < HeaderLength)
        {
            throw new InvalidDataException(
                $"'{name}': a .cus header is {HeaderLength} bytes; the file ends at byte {bytes.Length}.");
        }

        var width = BinaryPrimitives.ReadInt32BigEndian(bytes);
        if (width is < 1 or > MaxDimension)
        {
            throw new InvalidDataException(
                $"'{name}': width {width} at byte 0 is outside 1..{MaxDimension}.");
        }

        var height = BinaryPrimitives.ReadInt32BigEndian(bytes[4..]);
        if (height is < 1 or > MaxDimension)
        {
            throw new InvalidDataException(
                $"'{name}': height {height} at byte 4 is outside 1..{MaxDimension}.");
        }

        var hasKeyColour = bytes[8] != 0;
        var keyColour = BinaryPrimitives.ReadUInt16BigEndian(bytes[9..]);
        int paletteCount = bytes[PaletteCountOffset];

        var pixelOffset = HeaderLength + PaletteEntryLength * paletteCount;
        if (pixelOffset > bytes.Length)
        {
            throw new InvalidDataException(
                $"'{name}': the {paletteCount}-entry palette ends at byte {pixelOffset}, past the {bytes.Length}-byte file.");
        }

        var pixelCount = (long)width * height;
        if (pixelOffset + pixelCount != bytes.Length)
        {
            throw new InvalidDataException(
                $"'{name}': {width}x{height} pixels from byte {pixelOffset} need {pixelOffset + pixelCount} bytes, "
                + $"but the file is {bytes.Length}.");
        }

        var palette444 = ImmutableArray.CreateBuilder<ushort>(paletteCount);
        var transparentIndex = -1;
        for (var i = 0; i < paletteCount; i++)
        {
            var entry = BinaryPrimitives.ReadUInt16BigEndian(bytes[(HeaderLength + i * PaletteEntryLength)..]);
            palette444.Add(entry);

            // FIRST match wins, and the match is not always slot 0 (see the type remarks).
            if (hasKeyColour && transparentIndex < 0 && entry == keyColour)
            {
                transparentIndex = i;
            }
        }

        var indices = bytes.Slice(pixelOffset, (int)pixelCount).ToArray();
        for (var i = 0; i < indices.Length; i++)
        {
            if (indices[i] >= paletteCount)
            {
                throw new InvalidDataException(
                    $"'{name}': pixel index {indices[i]} at byte {pixelOffset + i} is outside the "
                    + $"{paletteCount}-entry palette.");
            }
        }

        var entries = palette444.MoveToImmutable();
        return new StormholdCusImage(
            name,
            width,
            height,
            hasKeyColour,
            keyColour,
            entries,
            transparentIndex,
            BuildPalette(entries, transparentIndex),
            indices);
    }

    /// <summary>Convenience overload for callers holding a whole file.</summary>
    public static StormholdCusImage Parse(byte[] bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return Parse(bytes.AsSpan(), name);
    }

    /// <summary>
    ///     Views the image as the <paramref name="index" />-th of <paramref name="count" /> equal-width
    ///     strip frames.
    ///     <para>
    ///         <b>The frame count is not in the file.</b> Multi-pose art is stored as one horizontal
    ///         strip and the game supplies the count from its own monster descriptor tables, clipping
    ///         to <c>W / count</c> pixels and shifting the strip left by <c>frame * (W / count)</c>.
    ///         This method reproduces that integer arithmetic, remainder columns included: a caller
    ///         asking for 3 frames of the 137-pixel <c>wardenheads4bit.cus</c> gets three 45-pixel
    ///         frames and the last two columns are dropped, exactly as the game drops them.
    ///     </para>
    /// </summary>
    /// <param name="index">Zero-based frame, 0..<paramref name="count" />-1.</param>
    /// <param name="count">Frames the strip is divided into; must be 1..<see cref="Width" />.</param>
    public IndexedBitmap Frame(int index, int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, Width);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, count);

        var frameWidth = Width / count;
        var left = index * frameWidth;
        var frame = new byte[frameWidth * Height];
        for (var y = 0; y < Height; y++)
        {
            Array.Copy(_indices, y * Width + left, frame, y * frameWidth, frameWidth);
        }

        return new IndexedBitmap(frameWidth, Height, frame);
    }

    /// <summary>
    ///     Expands the 4:4:4 entries into a 256-entry RGBA palette by nibble replication, leaving
    ///     the unused tail black, then punches alpha 0 into the transparent slot and every unused
    ///     slot.
    /// </summary>
    private static Palette BuildPalette(ImmutableArray<ushort> palette444, int transparentIndex)
    {
        var rgb = new byte[Palette.RgbByteCount];
        for (var i = 0; i < palette444.Length; i++)
        {
            var entry = palette444[i];
            rgb[i * 3] = (byte)(((entry >> 8) & 0xF) * 17);
            rgb[i * 3 + 1] = (byte)(((entry >> 4) & 0xF) * 17);
            rgb[i * 3 + 2] = (byte)((entry & 0xF) * 17);
        }

        var palette = Palette.FromRgb8(rgb);
        for (var i = palette444.Length; i < Palette.EntryCount; i++)
        {
            palette = palette.WithTransparentIndex(i);
        }

        return transparentIndex >= 0 ? palette.WithTransparentIndex(transparentIndex) : palette;
    }
}
