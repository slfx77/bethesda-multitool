using System.Globalization;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Imaging;

namespace BethesdaMultitool.Core.Formats.Tactics;

/// <summary>
///     A Fallout Tactics <c>&lt;zar&gt;</c> image — the GUI art in <c>gui_0.bos</c>, the thumbnails a
///     save carries, and the streams embedded in every <c>.til</c> tile and <c>.spr</c> sprite.
///     Read off the game's own code (BOS.exe, class <c>ImageZAR</c>, <c>image_zar.cpp</c>) in
///     <c>tools/GhidraProject/ClassicRE/BOS.exe.decompiled.txt</c>; every other Tactics reference
///     is GPL, so nothing is ported. Original RE 2026-09-07.
///     <para>
///         Layout, ALL little-endian — reader <c>FUN_006f7d50</c> (<c>ImageZAR::read</c>), writer
///         <c>FUN_006f7c20</c>, palette <c>FUN_00709a00</c> / <c>FUN_007099b0</c>:
///         <c>'&lt;zar&gt;' NUL</c> + ASCII version + NUL (accepted iff <c>1 &lt; version &lt; 5</c>,
///         else "Unknown zar file type"); i32 width, i32 height; <b>u8 hasPalette</b>; when it is
///         non-zero, u32 paletteCount then count x 4 bytes <b>B, G, R, x</b> and —
///         <b>
///             only when
///             version &gt; 3
///         </b>
///         — u8 <see cref="ShadowIndex" />; then, unconditionally, u32
///         dataLength and the RLE block. The shadow byte sits INSIDE the hasPalette branch
///         (<c>if (hasPalette &amp;&amp; (Palette::read(), version &gt; 3)) read(this+0x3b)</c>), so a
///         palette-less stream has none whatever its version. A retail GUI file (v4, 256 entries)
///         therefore puts its length at 1046 and its data at 1050, and an empty save slot (0x0, no
///         palette, length 0) is exactly 21 bytes.
///     </para>
///     <para>
///         ⛔ The backlog's four "refuted" codec families were all run from offset 1045 — the data
///         is at 1050. With the shadow byte and the length accounted for, the 2-bit-mode /
///         6-bit-count family the board scored 0/120 IS the codec: 839/839 GUI ZARs and 31,127/31,127
///         tile-embedded streams tile exactly (width x height pixels emitted AND dataLength consumed).
///     </para>
///     <para>
///         ⚑ <b>RLE</b> (row table <c>FUN_006f8c50</c>, 32-bit decode <c>FUN_006f9a10</c>, 8-bit +
///         alpha decode <c>FUN_006f9cd0</c>): per row, control byte <c>c</c> with <c>mode = c &amp; 3</c>
///         and <c>count = c &gt;&gt; 2</c>. Mode 0 = <c>count</c> TRANSPARENT pixels, no payload; 1 =
///         <c>count</c> OPAQUE palette indices; 2 = <c>count</c> (index, alpha) PAIRS; 3 = <c>count</c>
///         alpha bytes whose colour is <c>palette[shadowIndex]</c>. <c>x += count</c> on every mode;
///         the row closes only when <c>x == width</c>, and <c>x &gt; width</c> is the game's error
///         "ZAR run went past edge of image" (image_zar.cpp:0x281). A count of 0 is legal — the
///         encoder <c>FUN_006f8880</c> emits an empty mode-2 byte before a row that starts in mode 3.
///     </para>
///     <para>
///         ⚑ <b>Transparency and byte order.</b> <c>FUN_006f9a10</c> builds each output u32 as
///         <c>(alpha &lt;&lt; 24) | (paletteEntry &amp; 0x00FFFFFF)</c> with masks R = 0xFF0000,
///         G = 0xFF00, B = 0xFF — memory order B, G, R, A. A mode-0 pixel is the whole u32 written
///         as 0 (transparent black), mode 1 gets alpha 0xFF, modes 2 and 3 carry their alpha byte
///         per pixel. The palette's fourth byte is DISCARDED (overwritten by the run alpha), so it
///         is neither alpha nor a colour key. <see cref="Decode()" /> emits the repo's R, G, B, A
///         order — the same pixels, component-swapped for <c>PngWriter</c> / <c>DecodedTexture</c>.
///     </para>
///     <para>
///         ⚠ What tiling CANNOT discriminate on the retail data, so each rests on the code: mode 1
///         vs mode 3 (both 1 byte per pixel) is split by <c>FUN_006f9a10</c> and by the visual —
///         <c>deathSense.zar</c>'s mode-3 runs are anti-aliased black strokes; row closure vs
///         runs-may-cross-rows scores 839/839 either way (no retail run crosses a row) and is
///         enforced because <c>FUN_006f8c50</c> does; the game never checks for trailing bytes after
///         the last row, this reader does (0 leftovers on every retail stream).
///     </para>
/// </summary>
internal sealed class TacticsZarImage
{
    /// <summary>Mode of one RLE run, in the game's numbering (<c>c &amp; 3</c>).</summary>
    public enum RunMode
    {
        Transparent = 0,
        Opaque = 1,
        Translucent = 2,
        Shadow = 3
    }

    /// <summary>The tag every ZAR opens with.</summary>
    public const string Tag = "zar";

    /// <summary>Lowest version <c>FUN_006f7d50</c> accepts (<c>1 &lt; version</c>).</summary>
    public const int MinimumVersion = 2;

    /// <summary>Highest version <c>FUN_006f7d50</c> accepts (<c>version &lt; 5</c>).</summary>
    public const int MaximumVersion = 4;

    /// <summary>The shadow-index byte is read only from this version on (<c>3 &lt; version</c>).</summary>
    public const int ShadowIndexVersion = 4;

    /// <summary>Palette entries on every retail file (839/839 GUI, 31,127/31,127 tile streams).</summary>
    public const int RetailPaletteEntries = 256;

    /// <summary>Bytes of an EMPTY record: tag framing + width + height + hasPalette + length.</summary>
    public const int EmptyRecordLength = 21;

    /// <summary>Bytes from a retail v4 GUI record's start to its RLE block (<c>1046</c> holds the length).</summary>
    public const int RetailPixelBlockOffset = EmptyRecordLength + RetailPaletteEntries * 4 + 1 + 4;

    /// <summary>Longest run a control byte can express (6-bit count).</summary>
    public const int MaximumRunLength = 63;

    /// <summary>The game's own error text (<c>FUN_006f8c50</c>, image_zar.cpp:0x281).</summary>
    public const string RunPastEdgeMessage = "ZAR run went past edge of image";

    /// <summary>The game's own error text for a version outside 2..4 (image_zar.cpp:0xa8).</summary>
    public const string UnknownVersionMessage = "Unknown zar file type";

    /// <summary>A sanity bound on the palette count — a palette index is one byte, so more is unreachable.</summary>
    private const int MaximumPaletteEntries = 4096;

    private readonly string _name;

    private TacticsZarImage(
        string name,
        int version,
        int width,
        int height,
        ReadOnlyMemory<byte> paletteBgrx,
        byte shadowIndex,
        ReadOnlyMemory<byte> pixelBlock,
        int recordLength)
    {
        _name = name;
        Version = version;
        Width = width;
        Height = height;
        PaletteBgrx = paletteBgrx;
        ShadowIndex = shadowIndex;
        PixelBlock = pixelBlock;
        RecordLength = recordLength;
    }

    /// <summary>The ASCII version parsed as an integer — 2, 3 or 4.</summary>
    public int Version { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>True when the record carries pixels; an empty save slot is 0 x 0 with no palette.</summary>
    public bool HasImage => Width > 0 && Height > 0;

    /// <summary>
    ///     True when the record carries its own palette. A GUI ZAR always does; a stream embedded in
    ///     a sprite does not (it indexes the sprite's layer palette) and must decode through
    ///     <see cref="Decode(Palette)" />.
    /// </summary>
    public bool HasPalette => PaletteBgrx.Length > 0;

    /// <summary>The palette exactly as stored, 4 bytes per entry: B, G, R, then the byte the game discards.</summary>
    public ReadOnlyMemory<byte> PaletteBgrx { get; }

    /// <summary>Entries in <see cref="PaletteBgrx" /> (256 on every retail file, 0 without a palette).</summary>
    public int PaletteEntries => PaletteBgrx.Length / 4;

    /// <summary>
    ///     The palette index every mode-3 (shadow) pixel takes its colour from. Stored only on
    ///     version 4 records with a palette; 0 otherwise, which is also what the game's constructor
    ///     leaves in the field (<c>this+0x3b</c>). Non-zero on 48 of the 839 GUI files, every one
    ///     of which uses mode 3.
    /// </summary>
    public byte ShadowIndex { get; }

    /// <summary>The RLE block, exactly as stored.</summary>
    public ReadOnlyMemory<byte> PixelBlock { get; }

    /// <summary>Total bytes of the record, so a container can step past it.</summary>
    public int RecordLength { get; }

    /// <summary>Content probe: the framing with the <c>zar</c> tag.</summary>
    public static bool IsZar(ReadOnlySpan<byte> bytes)
    {
        return TacticsTagChunk.Is(bytes, Tag);
    }

    /// <summary>
    ///     Reads a loose ZAR file: one record that must fill <paramref name="bytes" /> exactly
    ///     (<c>dataOffset + dataLength == file size</c> on 839/839 GUI files).
    /// </summary>
    public static TacticsZarImage Parse(ReadOnlyMemory<byte> bytes, string name)
    {
        var cursor = new TacticsCursor(bytes, name);
        var image = Read(cursor);
        cursor.RequireEnd("<zar> record");
        return image;
    }

    /// <summary>Reads a loose ZAR file, reporting why rather than throwing.</summary>
    public static bool TryParse(ReadOnlyMemory<byte> bytes, string name, out TacticsZarImage image, out string error)
    {
        try
        {
            image = Parse(bytes, name);
            error = string.Empty;
            return true;
        }
        catch (InvalidDataException e)
        {
            image = null!;
            error = e.Message;
            return false;
        }
    }

    /// <summary>
    ///     Reads a record at the cursor and leaves it positioned after the record — the embedded
    ///     case: a save header carries eight in a row, a tile up to fifteen, a sprite one per layer.
    ///     This is <c>FUN_006f7d50</c> field for field.
    /// </summary>
    public static TacticsZarImage Read(TacticsCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);

        var start = cursor.Position;
        var chunk = cursor.Tag(Tag);

        // FUN_00703eb0 reads at most four version characters and atoi()s them; FUN_006f7d50
        // then requires 1 < version < 5.
        if (chunk.Version.Length > 4
            || !int.TryParse(chunk.Version, NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            || version is < MinimumVersion or > MaximumVersion)
        {
            throw cursor.Fail(start,
                $"{UnknownVersionMessage}: '<zar>' version '{chunk.Version}' is not {MinimumVersion}..{MaximumVersion}");
        }

        var width = cursor.Count(1 << 14, "width");
        var height = cursor.Count(1 << 14, "height");
        var hasPalette = cursor.U8();

        var palette = ReadOnlyMemory<byte>.Empty;
        byte shadowIndex = 0;
        if (hasPalette != 0)
        {
            var paletteCount = cursor.Count(MaximumPaletteEntries, "palette");
            palette = cursor.Bytes(paletteCount * 4, "the palette");
            if (version >= ShadowIndexVersion)
            {
                shadowIndex = cursor.U8();
            }
        }

        var dataLength = cursor.Count(int.MaxValue, "pixel block");
        var block = cursor.Bytes(dataLength, "the pixel block");
        return new TacticsZarImage(cursor.Name, version, width, height, palette, shadowIndex, block,
            cursor.Position - start);
    }

    /// <summary>
    ///     The stored palette as R, G, B with alpha 255. The stored order is B, G, R — the luma
    ///     weights in the game's nearest-colour search <c>FUN_00709bd0</c> are 11/59/30 on bytes
    ///     0/1/2, and <c>FUN_006f9a10</c>'s masks put R in bits 16-23. Entries beyond 256 cannot be
    ///     indexed and are dropped; a shorter palette is padded with black.
    /// </summary>
    public Palette ToPalette()
    {
        if (!HasPalette)
        {
            throw new InvalidOperationException(
                $"{_name}: this <zar> carries no palette; decode it through the owning sprite's palette.");
        }

        var rgb = new byte[Palette.RgbByteCount];
        var source = PaletteBgrx.Span;
        var entries = Math.Min(PaletteEntries, Palette.EntryCount);
        for (var i = 0; i < entries; i++)
        {
            rgb[i * 3] = source[i * 4 + 2];
            rgb[i * 3 + 1] = source[i * 4 + 1];
            rgb[i * 3 + 2] = source[i * 4];
        }

        return Palette.FromRgb8(rgb);
    }

    /// <summary>
    ///     Decodes the pixel block through the record's own palette to RGBA (R, G, B, A byte
    ///     order), refusing any block that does not tile: a run past the row's edge, a payload past
    ///     the block, a row the block cannot finish, or bytes left after the last row.
    /// </summary>
    public DecodedTexture Decode()
    {
        return Decode(ToPalette(), PaletteEntries);
    }

    /// <summary>
    ///     Decodes through an external palette — the route for a stream embedded in a sprite, which
    ///     carries no palette of its own and indexes one of the sprite's four layer palettes.
    /// </summary>
    public DecodedTexture Decode(Palette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        return Decode(palette, Palette.EntryCount);
    }

    /// <summary>
    ///     The 8-bit view <c>FUN_006f9cd0</c> produces: mode 0 writes index 0 and alpha 0, mode 1 the
    ///     index and alpha 0xFF, mode 2 both from its pairs, mode 3 <see cref="ShadowIndex" /> and
    ///     the payload alpha. The index plane alone loses the alpha the game keeps beside it.
    /// </summary>
    public Planes DecodeIndexed()
    {
        RequireImage();

        var indices = new byte[Width * Height];
        var alpha = new byte[Width * Height];
        Walk((y, x, mode, count, payload) =>
        {
            var at = y * Width + x;
            switch (mode)
            {
                case RunMode.Opaque:
                    for (var i = 0; i < count; i++)
                    {
                        indices[at + i] = payload[i];
                        alpha[at + i] = 0xFF;
                    }

                    break;
                case RunMode.Translucent:
                    for (var i = 0; i < count; i++)
                    {
                        indices[at + i] = payload[i * 2];
                        alpha[at + i] = payload[i * 2 + 1];
                    }

                    break;
                case RunMode.Shadow:
                    for (var i = 0; i < count; i++)
                    {
                        indices[at + i] = ShadowIndex;
                        alpha[at + i] = payload[i];
                    }

                    break;
                case RunMode.Transparent:
                default:
                    // Both planes stay 0.
                    break;
            }
        });

        return new Planes(new IndexedBitmap(Width, Height, indices), alpha);
    }

    /// <summary>Counts the control bytes of each mode, walking the block with the same closure rules as a decode.</summary>
    public RunCensus CountRuns()
    {
        RequireImage();

        int transparent = 0, opaque = 0, translucent = 0, shadow = 0;
        Walk((_, _, mode, _, _) =>
        {
            switch (mode)
            {
                case RunMode.Transparent:
                    transparent++;
                    break;
                case RunMode.Opaque:
                    opaque++;
                    break;
                case RunMode.Translucent:
                    translucent++;
                    break;
                default:
                    shadow++;
                    break;
            }
        });

        return new RunCensus(transparent, opaque, translucent, shadow);
    }

    private DecodedTexture Decode(Palette palette, int paletteEntries)
    {
        RequireImage();

        var rgba = palette.Rgba.ToArray();
        var pixels = new byte[Width * Height * 4];
        var shadowIndex = ShadowIndex;
        if (shadowIndex >= paletteEntries)
        {
            throw Fail($"shadow index {shadowIndex} lies outside the {paletteEntries}-entry palette");
        }

        Walk((y, x, mode, count, payload) =>
        {
            var at = (y * Width + x) * 4;
            switch (mode)
            {
                case RunMode.Opaque:
                    for (var i = 0; i < count; i++, at += 4)
                    {
                        var index = payload[i];
                        RequireIndex(index, paletteEntries, y, x + i);
                        pixels[at] = rgba[index * 4];
                        pixels[at + 1] = rgba[index * 4 + 1];
                        pixels[at + 2] = rgba[index * 4 + 2];
                        pixels[at + 3] = 0xFF;
                    }

                    break;
                case RunMode.Translucent:
                    for (var i = 0; i < count; i++, at += 4)
                    {
                        var index = payload[i * 2];
                        RequireIndex(index, paletteEntries, y, x + i);
                        pixels[at] = rgba[index * 4];
                        pixels[at + 1] = rgba[index * 4 + 1];
                        pixels[at + 2] = rgba[index * 4 + 2];
                        pixels[at + 3] = payload[i * 2 + 1];
                    }

                    break;
                case RunMode.Shadow:
                    for (var i = 0; i < count; i++, at += 4)
                    {
                        pixels[at] = rgba[shadowIndex * 4];
                        pixels[at + 1] = rgba[shadowIndex * 4 + 1];
                        pixels[at + 2] = rgba[shadowIndex * 4 + 2];
                        pixels[at + 3] = payload[i];
                    }

                    break;
                case RunMode.Transparent:
                default:
                    // FUN_006f9a10 writes the whole u32 as 0: transparent black, already there.
                    break;
            }
        });

        return DecodedTexture.FromBaseLevel(pixels, Width, Height, false);
    }

    /// <summary>
    ///     The row walk of <c>FUN_006f8c50</c>: read a control byte, step over its payload (0, 1, 2
    ///     or 1 bytes per pixel for modes 0..3), add the count to x; when x reaches the width the
    ///     row closes if x equals it exactly and is the game's error otherwise. Height rows, then
    ///     the block must be spent.
    /// </summary>
    private void Walk(RunHandler handler)
    {
        var block = PixelBlock.Span;
        var position = 0;

        for (var y = 0; y < Height; y++)
        {
            var x = 0;
            while (true)
            {
                if (position >= block.Length)
                {
                    throw Fail(
                        $"row {y} is unfinished at x={x} of {Width}: the {block.Length}-byte pixel block is spent");
                }

                var control = block[position++];
                var mode = (RunMode)(control & 3);
                var count = control >> 2;
                var payloadLength = mode switch
                {
                    RunMode.Transparent => 0,
                    RunMode.Translucent => count * 2,
                    _ => count
                };

                if (position + payloadLength > block.Length)
                {
                    throw Fail(
                        $"row {y}: a mode-{(int)mode} run of {count} at x={x} needs {payloadLength} payload bytes but the pixel block ends {block.Length - position} bytes on");
                }

                // The game builds its row table (FUN_006f8c50) before it decodes a pixel, so an
                // overrunning run is refused before anything is written — check, then dispatch.
                if (x + count > Width)
                {
                    throw Fail(
                        $"row {y}: {RunPastEdgeMessage} (a run of {count} at x={x} reaches {x + count} of width {Width})");
                }

                handler(y, x, mode, count, block.Slice(position, payloadLength));
                position += payloadLength;
                x += count;

                if (x == Width)
                {
                    break;
                }
            }
        }

        if (position != block.Length)
        {
            throw Fail(
                $"{Width}x{Height} decoded from {position} of {block.Length} pixel-block bytes; {block.Length - position} left over");
        }
    }

    private void RequireImage()
    {
        if (!HasImage)
        {
            throw new InvalidOperationException($"{_name}: an empty <zar> ({Width}x{Height}) has no pixels.");
        }
    }

    private void RequireIndex(int index, int paletteEntries, int y, int x)
    {
        if (index >= paletteEntries)
        {
            throw Fail($"row {y} x={x}: palette index {index} lies outside the {paletteEntries}-entry palette");
        }
    }

    private InvalidDataException Fail(string message)
    {
        return new InvalidDataException($"{_name}: <zar> {message}.");
    }

    /// <summary>How many control bytes of each mode a pixel block holds.</summary>
    public readonly record struct RunCensus(int Transparent, int Opaque, int Translucent, int Shadow)
    {
        public int Total => Transparent + Opaque + Translucent + Shadow;
    }

    /// <summary>
    ///     The two planes <c>FUN_006f9cd0</c> produces: an 8-bit index image and a separate 8-bit
    ///     alpha image of the same size.
    /// </summary>
    public readonly record struct Planes(IndexedBitmap Indices, byte[] Alpha);

    private delegate void RunHandler(int y, int x, RunMode mode, int count, ReadOnlySpan<byte> payload);
}
