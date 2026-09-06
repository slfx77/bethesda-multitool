using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Imaging;

namespace BethesdaMultitool.Core.Formats.Travels.Shadowkey;

/// <summary>
///     One row of a sprite: the single horizontal span <c>[X0, X1)</c> that carries pixels, and
///     that span's palette indices. A fully transparent row is stored as
///     <c>(0xFFFF, 0xFFFF)</c> with no pixel bytes and reports <see cref="IsEmpty" />.
/// </summary>
internal readonly record struct ShadowkeySpriteRow(ushort X0, ushort X1, ReadOnlyMemory<byte> Indices)
{
    /// <summary>The word both span bounds carry on a fully transparent row.</summary>
    public const ushort EmptyMarker = 0xFFFF;

    /// <summary>True for a fully transparent row (12,581 of the retail pack's rows).</summary>
    public bool IsEmpty => X0 == EmptyMarker && X1 == EmptyMarker;
}

/// <summary>A decoded sprite: indices, the palette to resolve them, and the slot chosen for transparency.</summary>
/// <param name="Bitmap">Row-major indices; pixels outside the row spans hold <paramref name="TransparentIndex" />.</param>
/// <param name="Palette">The sprite's own palette, with the transparent slots' alpha zeroed.</param>
/// <param name="TransparentIndex">The palette slot used to fill unspanned pixels.</param>
/// <param name="TransparentIndexAliasesPixels">
///     True when <paramref name="TransparentIndex" /> is also a real pixel value in this sprite, so
///     those pixels decode transparent too. Two of the 253 retail sprites use all 256 indices and
///     hit this; every other sprite has a spare slot.
/// </param>
internal sealed record ShadowkeySpriteImage(
    IndexedBitmap Bitmap,
    Palette Palette,
    byte TransparentIndex,
    bool TransparentIndexAliasesPixels)
{
    /// <summary>Resolves the indices through the palette into RGBA.</summary>
    public DecodedTexture ToDecodedTexture()
    {
        return Bitmap.ToDecodedTexture(Palette);
    }
}

/// <summary>
///     One blob of <c>global.spr</c>: a single-frame, row-span-encoded indexed image carrying its
///     <b>own</b> 256-entry palette.
///     <para>
///         Layout (little-endian): <c>u16 width</c>, <c>u16 height</c>, 256 x <c>u16</c> palette,
///         then one record per row from the top: <c>u16 x0, u16 x1</c> followed by
///         <c>x1 - x0</c> palette indices for columns <c>x0..x1-1</c>, or the marker
///         <c>(0xFFFF, 0xFFFF)</c> for a fully transparent row. Columns outside the span are
///         transparent; there is exactly one span per row and it is never zero length.
///     </para>
///     <para>
///         Palette entries are 0x0RGB 4:4:4 (R = bits 11..8, G = 7..4, B = 3..0), the same encoding
///         the mesh textures use; the entries after the last used one are the filler
///         <see cref="PaletteFiller" />, so <see cref="UsedPaletteLength" /> is the real length
///         (5..256 across retail, 127 sprites using all 256).
///     </para>
///     <para>
///         Trap: the zone <c>&lt;zone&gt;.pal</c> files are <b>not</b> these palettes. Tested on all
///         253 retail sprites 2026-09-05: at most 4 of 256 entries agree positionally with
///         <c>azra.pal</c> and only 39% of the embedded colours occur in it at all; rendering a
///         sprite through the zone palette gives noise. Sprites are self-contained.
///     </para>
/// </summary>
internal sealed record ShadowkeySprite(
    int Index,
    int Width,
    int Height,
    ushort[] PaletteEntries,
    IReadOnlyList<ShadowkeySpriteRow> Rows)
{
    /// <summary>The word filling the palette after its last used entry.</summary>
    public const ushort PaletteFiller = 0xCCCC;

    /// <summary>
    ///     Palette slots before the trailing <see cref="PaletteFiller" /> run. Every retail pixel
    ///     index is below its own sprite's value, which is what makes a sprite self-contained.
    /// </summary>
    public int UsedPaletteLength
    {
        get
        {
            var used = PaletteEntries.Length;
            while (used > 0 && PaletteEntries[used - 1] == PaletteFiller)
            {
                used--;
            }

            return used;
        }
    }

    /// <summary>Pixels covered by a row span (the rest of the image is transparent).</summary>
    public int SpanPixelCount
    {
        get
        {
            var total = 0;
            foreach (var row in Rows)
            {
                total += row.Indices.Length;
            }

            return total;
        }
    }

    /// <summary>Fully transparent rows.</summary>
    public int EmptyRowCount
    {
        get
        {
            var empty = 0;
            foreach (var row in Rows)
            {
                if (row.IsEmpty)
                {
                    empty++;
                }
            }

            return empty;
        }
    }

    /// <summary>
    ///     Paints the row spans into a row-major index buffer, filling everything they do not cover
    ///     with <paramref name="transparentIndex" />.
    /// </summary>
    public IndexedBitmap ToIndexedBitmap(byte transparentIndex)
    {
        var indices = new byte[Width * Height];
        if (transparentIndex != 0)
        {
            Array.Fill(indices, transparentIndex);
        }

        for (var y = 0; y < Rows.Count; y++)
        {
            var row = Rows[y];
            if (row.IsEmpty)
            {
                continue;
            }

            row.Indices.Span.CopyTo(indices.AsSpan((y * Width) + row.X0));
        }

        return new IndexedBitmap(Width, Height, indices);
    }

    /// <summary>
    ///     Builds the sprite's palette: each 4:4:4 entry expanded by nibble replication
    ///     (<c>c8 = c4 * 17</c>), then <paramref name="transparentIndex" /> — and, when
    ///     <paramref name="magentaIsTransparent" /> is set, every slot holding
    ///     <see cref="ShadowkeyMesh.MagentaColourKey" /> — given alpha 0.
    ///     <para>
    ///         The magenta key is a <b>hypothesis</b>: 162 of 253 retail sprites carry it inside
    ///         their spans and the renders only show it where a hole in the shape belongs (the base
    ///         of the explosion puff, the top of the flame column). Palette slot 0 is emphatically
    ///         not the key — it is white on 198 sprites, black on 20 and magenta on 19.
    ///     </para>
    /// </summary>
    public Palette BuildPalette(byte transparentIndex, bool magentaIsTransparent = true)
    {
        var rgb = new byte[Palette.RgbByteCount];
        var entries = Math.Min(PaletteEntries.Length, Palette.EntryCount);
        for (var i = 0; i < entries; i++)
        {
            var entry = PaletteEntries[i];
            rgb[i * 3] = (byte)(((entry >> 8) & 0xF) * 17);
            rgb[(i * 3) + 1] = (byte)(((entry >> 4) & 0xF) * 17);
            rgb[(i * 3) + 2] = (byte)((entry & 0xF) * 17);
        }

        var palette = Palette.FromRgb8(rgb).WithTransparentIndex(transparentIndex);
        if (magentaIsTransparent)
        {
            for (var i = 0; i < entries; i++)
            {
                if (PaletteEntries[i] == ShadowkeyMesh.MagentaColourKey)
                {
                    palette = palette.WithTransparentIndex(i);
                }
            }
        }

        return palette;
    }

    /// <summary>
    ///     The lowest palette slot no pixel of this sprite uses, or null when every slot is in use
    ///     (2 of the 253 retail sprites). Such a slot can fill the transparent area without
    ///     changing any painted pixel.
    /// </summary>
    public byte? FindUnusedIndex()
    {
        var used = new bool[Palette.EntryCount];
        foreach (var row in Rows)
        {
            foreach (var index in row.Indices.Span)
            {
                used[index] = true;
            }
        }

        for (var i = 0; i < used.Length; i++)
        {
            if (!used[i])
            {
                return (byte)i;
            }
        }

        return null;
    }

    /// <summary>
    ///     Decodes the sprite, choosing the transparent slot itself: a palette slot no pixel uses
    ///     when one exists, otherwise 255 — which then also blanks the real pixels that use it, as
    ///     <see cref="ShadowkeySpriteImage.TransparentIndexAliasesPixels" /> reports.
    /// </summary>
    public ShadowkeySpriteImage ToImage(bool magentaIsTransparent = true)
    {
        var unused = FindUnusedIndex();
        var transparentIndex = unused ?? (byte)(Palette.EntryCount - 1);
        return new ShadowkeySpriteImage(
            ToIndexedBitmap(transparentIndex),
            BuildPalette(transparentIndex, magentaIsTransparent),
            transparentIndex,
            unused is null);
    }
}

/// <summary>
///     Shadowkey's global sprite pack, <c>global.spr</c> (N-Gage, <b>little-endian</b>). Original RE
///     2026-09-05 from the retail file; no licensable reader exists for it.
///     <para>
///         The container has <b>no magic and no count field</b>: it is <c>u32 size[N]</c> followed
///         by the N blobs concatenated in index order. N is recovered by tiling — the only N for
///         which <c>4N + sum(size[0..N-1])</c> equals the file length. On the 1,646,707-byte retail
///         file that is <b>N = 384</b>, with entries 0..252 non-empty (<b>253</b> sprites) and
///         253..383 reserved zero-size slots. (The first two words, 0x1292 and 0x1627, are simply
///         <c>size[0]</c> and <c>size[1]</c>; reading them as a header is the trap this format
///         sets.) The zone <c>&lt;zone&gt;_sprites.txt</c> lists index this table directly.
///     </para>
///     <para>
///         Because the running total grows by <c>4 + size[N]</c> at every step it is strictly
///         increasing, so a tiling solution is unique when it exists; the uniqueness check below is
///         a guard, and the case that actually bites is a file that tiles at <b>no</b> N.
///     </para>
///     <para>
///         Measured on retail 2026-09-05: all 253 non-empty blobs tile to the byte under
///         <see cref="ShadowkeySprite" />'s layout, for 1,386,555 span pixels and 12,581 empty rows;
///         every pixel index is below its own sprite's used palette length. Sizes run 176x208 (the
///         N-Gage screen, 117 sprites) down to 6x6 icons, plus the 322x13 scrolling compass strip.
///     </para>
/// </summary>
internal sealed class ShadowkeySpritePack
{
    /// <summary>Bytes per size-table entry.</summary>
    public const int SizeEntryLength = 4;

    /// <summary>Bytes of a sprite blob before its first row: width, height and the 256-entry palette.</summary>
    public const int SpriteHeaderLength = 4 + (Palette.EntryCount * 2);

    private readonly byte[] _bytes;
    private readonly ShadowkeySprite?[] _sprites;
    private readonly bool[] _parsed;
    private readonly uint[] _offsets;

    private ShadowkeySpritePack(string name, byte[] bytes, uint[] sizes, uint[] offsets)
    {
        Name = name;
        _bytes = bytes;
        Sizes = sizes;
        _offsets = offsets;
        _sprites = new ShadowkeySprite?[sizes.Length];
        _parsed = new bool[sizes.Length];
    }

    /// <summary>Source name, for messages.</summary>
    public string Name { get; }

    /// <summary>Slots in the pack — 384 on retail, 131 of them empty.</summary>
    public int Count => Sizes.Count;

    /// <summary>Byte length of each slot's blob, 0 for a reserved slot.</summary>
    public IReadOnlyList<uint> Sizes { get; }

    /// <summary>
    ///     Parses the pack, solving the entry count by tiling. Throws
    ///     <see cref="InvalidDataException" /> when no entry count tiles the file, or (defensively)
    ///     when more than one does.
    /// </summary>
    public static ShadowkeySpritePack Parse(byte[] bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(name);

        var count = SolveEntryCount(bytes, name);
        var sizes = new uint[count];
        var offsets = new uint[count];
        var offset = (uint)(count * SizeEntryLength);
        for (var i = 0; i < count; i++)
        {
            sizes[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * SizeEntryLength));
            offsets[i] = offset;
            offset += sizes[i];
        }

        return new ShadowkeySpritePack(name, bytes, sizes, offsets);
    }

    /// <summary>The raw bytes of one slot's blob — empty for a reserved slot.</summary>
    public ReadOnlyMemory<byte> GetEntryBytes(int index)
    {
        RequireIndex(index);
        return _bytes.AsMemory((int)_offsets[index], (int)Sizes[index]);
    }

    /// <summary>
    ///     Parses the sprite in one slot, or returns null for a zero-size slot. The result is
    ///     cached.
    /// </summary>
    public ShadowkeySprite? GetSprite(int index)
    {
        RequireIndex(index);
        if (_parsed[index])
        {
            return _sprites[index];
        }

        var sprite = Sizes[index] == 0
            ? null
            : ParseSprite(_bytes.AsSpan((int)_offsets[index], (int)Sizes[index]), index, $"{Name}[{index}]");

        _sprites[index] = sprite;
        _parsed[index] = true;
        return sprite;
    }

    /// <summary>
    ///     Walks one sprite blob. Every failure names the byte position <b>inside the blob</b>, so
    ///     add the slot's pack offset when chasing one down in a hex editor.
    /// </summary>
    public static ShadowkeySprite ParseSprite(ReadOnlySpan<byte> bytes, int index, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (bytes.Length < SpriteHeaderLength)
        {
            throw new InvalidDataException(
                $"'{name}': the blob is {bytes.Length} bytes, too short for the {SpriteHeaderLength}-byte header and palette.");
        }

        int width = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        int height = BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]);

        var palette = new ushort[Palette.EntryCount];
        for (var i = 0; i < palette.Length; i++)
        {
            palette[i] = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(4 + (i * 2))..]);
        }

        var rows = new ShadowkeySpriteRow[height];
        var position = SpriteHeaderLength;
        for (var y = 0; y < height; y++)
        {
            if (position + 4 > bytes.Length)
            {
                throw new InvalidDataException(
                    $"'{name}': row {y}'s span header needs 4 bytes at byte {position}, past the {bytes.Length}-byte blob.");
            }

            var x0 = BinaryPrimitives.ReadUInt16LittleEndian(bytes[position..]);
            var x1 = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(position + 2)..]);
            position += 4;

            if (x0 == ShadowkeySpriteRow.EmptyMarker && x1 == ShadowkeySpriteRow.EmptyMarker)
            {
                rows[y] = new ShadowkeySpriteRow(x0, x1, ReadOnlyMemory<byte>.Empty);
                continue;
            }

            if (x0 > x1 || x1 > width)
            {
                throw new InvalidDataException(
                    $"'{name}': row {y} at byte {position - 4} spans [{x0}, {x1}) of a {width}-pixel row.");
            }

            var length = x1 - x0;
            if (position + length > bytes.Length)
            {
                throw new InvalidDataException(
                    $"'{name}': row {y}'s {length} pixels at byte {position} run past the {bytes.Length}-byte blob.");
            }

            rows[y] = new ShadowkeySpriteRow(x0, x1, bytes.Slice(position, length).ToArray());
            position += length;
        }

        if (position != bytes.Length)
        {
            throw new InvalidDataException(
                $"'{name}': the {height} rows end at byte {position} but the blob is {bytes.Length} bytes.");
        }

        return new ShadowkeySprite(index, width, height, palette, rows);
    }

    /// <summary>
    ///     Finds the entry count whose size table tiles the file exactly. Scanning is cheap: the
    ///     running total grows by at least <see cref="SizeEntryLength" /> per candidate, so the scan
    ///     stops the moment it overshoots the file.
    /// </summary>
    private static int SolveEntryCount(ReadOnlySpan<byte> bytes, string name)
    {
        if (bytes.Length < SizeEntryLength)
        {
            throw new InvalidDataException(
                $"'{name}': the pack is {bytes.Length} bytes, too short to hold even one size word.");
        }

        var solution = -1;
        long total = 0;
        var maximum = bytes.Length / SizeEntryLength;
        for (var count = 1; count <= maximum; count++)
        {
            total += BinaryPrimitives.ReadUInt32LittleEndian(bytes[((count - 1) * SizeEntryLength)..]);
            var tiled = ((long)count * SizeEntryLength) + total;
            if (tiled == bytes.Length)
            {
                if (solution >= 0)
                {
                    throw new InvalidDataException(
                        $"'{name}': the size table tiles the {bytes.Length}-byte pack at both {solution} and {count} entries; the entry count is ambiguous.");
                }

                solution = count;
            }
            else if (tiled > bytes.Length)
            {
                break;
            }
        }

        if (solution < 0)
        {
            throw new InvalidDataException(
                $"'{name}': no entry count from 1 to {maximum} tiles the {bytes.Length}-byte pack; the size table at byte 0 does not describe this file.");
        }

        return solution;
    }

    private void RequireIndex(int index)
    {
        if (index < 0 || index >= Sizes.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index), index, $"'{Name}': the pack has {Sizes.Count} slots.");
        }
    }
}
