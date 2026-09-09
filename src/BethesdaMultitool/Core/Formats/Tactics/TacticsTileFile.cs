using System.Globalization;

namespace BethesdaMultitool.Core.Formats.Tactics;

/// <summary>
///     The type a tile declares (<c>this+0x25</c>). Read off the Tile Editor's own combo box:
///     <c>FUN_00642910</c> builds the "Type" list and <c>FUN_00645d30</c> switches on the field to
///     select the entry — case 0 "Wall", 1 "Floor", 2 "Object", 3 "Stair", 4 "Roof". The config
///     importer <c>FUN_00646350</c> writes the same numbering from the same names.
///     <para>
///         ⚑ <b>Confirmed OUTSIDE the binary by the tile artists' own naming convention</b>
///         (<c>Set_Type_Material_Name_…</c>, e.g. <c>BOS_Floor_Metal_PipeGrateCentre_F_1_NE.til</c>).
///         Over the 29,572 retail tiles that carry an in-range type and a three-part name, the type
///         word matches on <b>28,606 (96.7%)</b>: 0 reads "wall" or "cap" 9,342 of 9,408 times,
///         1 "floor" 9,431 of 10,083, 2 "object" 8,919 of 8,999, 3 "stair" or "step" 338 of 340,
///         4 "roof" 576 of 742. Nothing about that convention is derivable from the file layout, so
///         a numbering that was shuffled or off by one could not score it.
///     </para>
///     <para>
///         ⚠ 382 of the 29,957 retail tiles store a value OUTSIDE 0..4 (111, 147, 253, and others),
///         and every one of them is version 6 (192 tiles) or 7 (190) — all 24,023 tiles at versions
///         8, 9 and 10 are in range. The game's own switch has a <c>default:</c> that leaves the
///         combo box unset, so those files load; a reader must surface the raw value rather than
///         reject or clamp it, which is why this is a plain enum cast and not a validated one.
///     </para>
/// </summary>
internal enum TacticsTileType
{
    Wall = 0,
    Floor = 1,
    Object = 2,
    Stair = 3,
    Roof = 4
}

/// <summary>
///     The material a tile declares (<c>this+0x26</c>), from the Tile Editor's "Material" combo —
///     the same <c>FUN_00642910</c> / <c>FUN_00645d30</c> pair. ⚠ This is NOT the sprite material
///     enum: a <c>&lt;sprite&gt;</c>'s u16 material runs concrete/brick/metal_thin/… while a tile's
///     runs stone/gravel/metal/wood/water/snow. Two enums, two formats, same field name.
///     <para>
///         ⚑ <b>Same outside-the-binary control</b> (the name's third part): 0 reads "stone" 9,013
///         of 9,193 times, 1 "gravel" 4,821 of 4,829, 2 "metal" 10,556 of 10,687, 3 "wood" 3,699 of
///         3,967, 4 "water" 222 of 256 — <b>28,311 of 28,932 (97.9%)</b>.
///         ⚠⚠ <b>The exception is <see cref="Snow" />, and it is instructive</b>: all 808 of its
///         tiles are named "…_Gravel_…", so the name oracle scores it 0/808 and, taken alone, would
///         look like a refutation. Their PATHS settle it instead — every one of the 808 lives under
///         a Snow directory (Mountain FLOORS\Slopes\Snow 480, SnowCaps 104, RockWALLS\CapsSNOW 100,
///         SnowDrifts 62, ExternalToSnow 32, SnowPiles 28, Mountain FLOORS\Snow 2), 808 of 808.
///         A control that cannot reach a population says nothing about it.
///     </para>
///     <para>
///         ⚠ Same caveat as <see cref="TacticsTileType" />, and it lands even more narrowly: 214 of
///         the 29,957 retail tiles are outside 0..5 and ALL 214 are version 6. Every one of the
///         28,472 tiles at versions 7 to 10 is in range.
///     </para>
/// </summary>
internal enum TacticsTileMaterial
{
    Stone = 0,
    Gravel = 1,
    Metal = 2,
    Wood = 3,
    Water = 4,
    Snow = 5
}

/// <summary>
///     Bits of a tile's u16 flag word (<c>this+0x08</c>), named by the Tile Editor's checkboxes.
///     <para>
///         ⚑ Proven by BOTH directions of the editor's dialog binding, which name and number the
///         same eight bits: <c>FUN_00642910</c> creates each checkbox with its caption and stores
///         the widget at a fixed offset in the editor object (Ethereal +0x17d, Cover +0x181,
///         Window +0x171, Trellis +0x175, "No Alpha" +0x179, Climbable +0x189, Invisible +0x18d,
///         NoShadow +0x191); <c>FUN_00645b30</c> reads those same eight widgets back and shifts
///         each into bits 0, 3, 2, 11, 10, 4, 7, 5 respectively; <c>FUN_00645d30</c> pushes the
///         same bits back out to the same widgets. Caption-to-bit is therefore fixed by the widget
///         OFFSET, not by call order — which is what makes the mapping non-contiguous and still
///         unambiguous.
///     </para>
///     <para>
///         ⚠ Bits 1, 6, 8, 9 and 12-15 have no editor checkbox and are NOT named here. Bit 1 is
///         real data, not padding — 154 of the 29,957 retail tiles carry it — but ⚠ all 154 are
///         v9 (103) or v10 (51). ⚠ v8 stores the word directly too (a whole u8) and its byte is 0 on
///         all 1,850, so the only versions that could ever have produced bit 1 by reconstruction are
///         v7 and v6. That <c>version &lt; 8</c> path in
///         <c>FUN_006f04c0</c>, which would rebuild bit 1 out of a legacy byte of its own, is never
///         exercised by a non-zero retail value: NO tile below v9 sets ANY flag bit (v6 0 of 1,485,
///         v7 0 of 4,479, v8 0 of 1,850). What bit 1 MEANS is unknown; it is carried verbatim.
///     </para>
///     <para>
///         ⚑ <b>The retail census is what makes this mapping falsifiable, and it holds.</b> Over
///         all 29,957 tiles exactly nine bits are ever set — 0 (1,433), 1 (154), 2 (784), 3 (2,362),
///         4 (26), 5 (7), 7 (1), 10 (120), 11 (2,154) — and bits 6, 8, 9 and 12-15 are set on ZERO
///         tiles. Eight of the nine are precisely the eight the editor names. The obvious wrong
///         reading (eight contiguous flags in bits 0-7) predicts traffic on bits 6 and none on 10
///         or 11, and the data says the opposite on both counts.
///     </para>
///     <para>
///         ⚠ That census reaches only PART of the corpus, so say what it covers: every set bit in
///         the retail 29,957 is on a v9 tile (215 tiles, and only bits 0-3) or a v10 tile (4,470).
///         v6, v7 and v8 set nothing at all, so nothing here — including the refutation of the
///         contiguous reading, which rests entirely on v10's bits 10 and 11 — speaks for the
///         7,814 tiles below v9. Their flag path is verified by the reader's exact tiling only,
///         and the reason it is never exercised is visible in the bytes: the two that feed bits 0
///         and 1 below v8 are ZERO on all 1,485 v6 and all 4,479 v7 tiles, and the byte v6..v8
///         discards is 100 on all 7,814 of them (v6's second discarded byte is 0 on all 1,485).
///     </para>
/// </summary>
[Flags]
internal enum TacticsTileFlags
{
    None = 0,
    Ethereal = 1 << 0,
    Window = 1 << 2,
    Cover = 1 << 3,
    Climbable = 1 << 4,
    NoShadow = 1 << 5,
    Invisible = 1 << 7,
    NoAlpha = 1 << 10,
    Trellis = 1 << 11
}

/// <summary>
///     One image inside a <c>.til</c>: an embedded <see cref="TacticsZarImage" /> plus the eight
///     bytes the tile stores beside it.
///     <para>
///         <c>FUN_006f0740</c> keeps two parallel vectors — <c>this+0x0c</c> of <c>ImageZAR*</c>
///         (stride 4) and <c>this+0x1c</c> of 8-byte records (stride 8) — and fills slot
///         <c>i</c> of both in one loop, so the pairing is the file's own.
///     </para>
///     <para>
///         ⚑
///         <b>
///             The eight bytes are a per-image DRAW OFFSET, and a multi-image tile is an
///             ANIMATION.
///         </b>
///         The tile draw <c>FUN_00782530</c> selects the record with
///         <c>index = counter % recordCount</c> — the count taken from the same
///         <c>(this+0x20 - this+0x1c) / 8</c> vector — reads its two dwords, and adds them to the
///         screen position it is about to draw at:
///         <c>x = base.x + placement.x + record[0]</c>, <c>y = base.y + placement.y + record[1]</c>.
///         So <see cref="OffsetX" />/<see cref="OffsetY" /> shift one animation frame relative to
///         the tile's own position, exactly as a sprite layer's (ox, oy) does.
///     </para>
///     <para>
///         ⚠ Retail barely exercises it: the pair is (0, 0) on 30,846 of the 31,127 images, so the
///         data alone could not have told you what these bytes are — only the draw code could. They
///         are read as UNSIGNED here because that is how the file stores them and how the game's
///         <c>read()</c> takes them; the addition in <c>FUN_00782530</c> is signed, so a caller that
///         needs a negative shift must reinterpret.
///     </para>
/// </summary>
internal sealed record TacticsTileImage(TacticsZarImage Image, uint OffsetX, uint OffsetY);

/// <summary>
///     A Fallout Tactics <c>.til</c> tile — the isometric map art, 29,957 of them in
///     <c>core\tiles_0.bos</c>. Original RE 2026-09-07 from BOS.exe's own reader/writer pair; every
///     other Tactics reference is GPL, so nothing is ported.
///     <para>
///         ⚑ <b>Two chunks, both in the family's <c>'&lt;tag&gt;' NUL version NUL</c> framing.</b>
///         All little-endian.
///     </para>
///     <para>
///         <b>1. The <c>&lt;tile&gt;</c> header</b> — reader <c>FUN_006f04c0</c>
///         (<c>C:\dev\phoenix\map\tile.cpp</c>, error "Unknown tile file type" at line 0x91),
///         writer <c>FUN_006f03a0</c>. The version must satisfy <c>5 &lt; v &lt; 11</c>, i.e.
///         <b>6..10</b>; retail ships all five (v10 10,893, v9 11,250, v8 1,850, v7 4,479,
///         v6 1,485). Body:
///         <list type="bullet">
///             <item>
///                 3 bytes <see cref="TacticsTileHeader.BoundingBoxX" />,
///                 <see cref="TacticsTileHeader.BoundingBoxY" />, <see cref="TacticsTileHeader.BoundingBoxZ" />
///                 — <c>this+0x12..0x14</c>; the editor's
///                 "Bounding Box" x/y/z spin boxes and the config key <c>bounding_box_size</c>
///                 ("Integer3") in <c>FUN_00646350</c>.
///             </item>
///             <item>
///                 8 bytes <see cref="TacticsTileHeader.FootPositionX" />, <see cref="TacticsTileHeader.FootPositionY" /> —
///                 <c>this+0x15</c>/<c>+0x19</c>; the config key <c>foot_position</c> ("Point"), shown
///                 in the editor as "Texture Coords". SIGNED: 119 retail tiles have a negative x.
///             </item>
///             <item>
///                 8 bytes <see cref="TacticsTileHeader.ImageWidth" />, <see cref="TacticsTileHeader.ImageHeight" /> —
///                 <c>this+0x1d</c>/<c>+0x21</c>; equal to the first embedded image's dimensions on
///                 29,922 of 29,957 files, which is the only evidence for the name (the editor never
///                 shows them and the config importer never writes them).
///             </item>
///             <item>
///                 u8 <see cref="TacticsTileHeader.Type" /> (<c>this+0x25</c>), u8 <see cref="TacticsTileHeader.Material" />
///                 (<c>this+0x26</c>).
///             </item>
///             <item>the flag word, which is where the FIVE VERSIONS differ — see below.</item>
///         </list>
///     </para>
///     <para>
///         ⚠⚠ <b>The flag word is version-shaped and the older forms are NOT a truncated u16.</b>
///         <c>FUN_006f04c0</c> reads a THROWAWAY byte on every version below 9, and only then
///         branches: <c>if (version &lt; 8)</c> a byte whose bit 0 becomes flag bit 0, then — on v6
///         only — a SECOND throwaway byte, then a byte whose bit 0 becomes flag bit 1;
///         <c>else if (version &lt; 10)</c> a full u8; <c>else</c> a u16.
///         ⚠ <b>v8 takes the <c>else if</c> branch</b> — throwaway + a WHOLE flag byte, not the
///         two one-bit fields v7 and v6 use. So only v6 and v7 carry one-bit fields with every bit
///         above 1 zero by construction, and the header lengths, framing included, are
///         <b>34 / 33 / 32 / 31 / 33</b> bytes for v6 / v7 / v8 / v9 / v10 — measured on
///         1,485 / 4,479 / 1,850 / 11,250 / 10,893 retail tiles, each version landing on exactly
///         one length. Retail also pins the discarded and legacy bytes: the first throwaway is
///         <c>100</c> on all 7,814 sub-v9 tiles, v6's second is <c>0</c> on all 1,485, and v8's
///         flag byte is <c>0</c> on all 1,850.
///     </para>
///     <para>
///         ⚠⚠
///         <b>
///             A u16 reader desynchronises 17,214 files and silently MIS-VALUES a further
///             1,850.
///         </b>
///         v9 (11,250), v7 (4,479) and v6 (1,485) end the header on the wrong byte and
///         the walk fails. v8 does NOT: throwaway + u8 spans the same two bytes a u16 does, so the
///         file still tiles and the flags merely read as <c>0x0064</c> = 100 instead of 0. ⛔ Do
///         not quote a single desync figure that folds the two together — an exact-tiling check
///         cannot see the v8 error at all, and only the flag census can.
///     </para>
///     <para>
///         <b>2. The <c>&lt;tiledata&gt;</c> block</b> — reader <c>FUN_006f0740</c> (version must be
///         exactly 1, error "Unknown tiledata file type" at tile.cpp:0xe3), writer
///         <c>FUN_006f0ab0</c>: a u32 image count, then per image an embedded <c>&lt;zar&gt;</c>
///         (through the ImageZAR vtable slot <c>+0x78</c> = <c>FUN_006f7d50</c>) followed by 8 raw
///         bytes, and finally ONE shared palette (<c>FUN_00709a00</c>: u32 count + count x 4 bytes)
///         that <c>FUN_006f0da0</c> then assigns to every image. ⚠ A zero image count returns
///         early, so a tile with no images has NO trailing palette either — the reader must not
///         demand one.
///     </para>
///     <para>
///         ⚑ <b>Tiling: 29,957 of 29,957 retail <c>.til</c> consume the file EXACTLY</b> — every
///         embedded ZAR emits width x height pixels and spends its own block, the 8-byte records
///         and the shared palette follow, and the walk lands on the last byte. 31,127 embedded
///         images in total (29,803 files carry exactly one; the largest carries 15). Every embedded
///         ZAR carries its own 256-entry palette and it is byte-identical to the shared palette on
///         31,127 of 31,127, so a tile image can be decoded on its own.
///     </para>
///     <para>
///         ⚑ <b>The same header is the mission map's tile record.</b> An inflated <c>.mis</c> world
///         embeds 99,596 <c>&lt;tile&gt;</c> chunks at a measured stride of exactly 33 bytes
///         (10 bytes of v10 framing + this 23-byte v10 body), and the four dwords whose meaning the
///         backlog recorded as open are this record's <c>foot_position</c> and image size: the
///         mission-side ranges (-40..352, 5..507, 5..581, 5..489) are CONTAINED in the ranges the
///         29,957 standalone tiles produce (-40..352, 5..507, <b>2</b>..581, <b>2</b>..489). ⚠ The
///         two agree exactly on <c>foot_position</c> — negative x included — and the mission side
///         is merely missing the smallest images on the two size fields, so say "contained in",
///         never "the same ranges". That is what settles them: the earlier "grid coordinate"
///         readings were refuted because they are not coordinates at all.
///     </para>
/// </summary>
internal sealed class TacticsTileFile
{
    /// <summary>The tag a tile header opens with.</summary>
    public const string Tag = "tile";

    /// <summary>The tag of the image block that follows it in a <c>.til</c> file.</summary>
    public const string DataTag = "tiledata";

    /// <summary>Lowest header version <c>FUN_006f04c0</c> accepts (<c>5 &lt; version</c>).</summary>
    public const int MinimumVersion = 6;

    /// <summary>Highest header version <c>FUN_006f04c0</c> accepts (<c>version &lt; 11</c>).</summary>
    public const int MaximumVersion = 10;

    /// <summary>The only <c>&lt;tiledata&gt;</c> version <c>FUN_006f0740</c> accepts.</summary>
    public const int DataVersion = 1;

    /// <summary>Bytes of a version-10 header record, framing included — the mission grid's stride.</summary>
    public const int Version10RecordLength = 33;

    /// <summary>Bytes stored beside every embedded image (<c>FUN_006f0740</c>, stride 8).</summary>
    public const int ImageExtraLength = 8;

    /// <summary>The game's own error text for a header version outside 6..10 (tile.cpp:0x91).</summary>
    public const string UnknownTileMessage = "Unknown tile file type";

    /// <summary>The game's own error text for a <c>&lt;tiledata&gt;</c> version other than 1 (tile.cpp:0xe3).</summary>
    public const string UnknownTileDataMessage = "Unknown tiledata file type";

    /// <summary>A sanity bound on the image count; the largest retail tile holds 15.</summary>
    private const int MaximumImages = 4096;

    private TacticsTileFile(
        TacticsTileHeader header,
        IReadOnlyList<TacticsTileImage> images,
        ReadOnlyMemory<byte> sharedPaletteBgrx)
    {
        Header = header;
        Images = images;
        SharedPaletteBgrx = sharedPaletteBgrx;
    }

    /// <summary>The <c>&lt;tile&gt;</c> record — the same 23-byte body the mission grid stores.</summary>
    public TacticsTileHeader Header { get; }

    /// <summary>The embedded images, in file order.</summary>
    public IReadOnlyList<TacticsTileImage> Images { get; }

    /// <summary>
    ///     The palette read after the last image and handed to all of them by
    ///     <c>FUN_006f0da0</c>, 4 bytes per entry (B, G, R, then the byte the blitter discards).
    ///     Empty when the tile carries no images at all.
    /// </summary>
    public ReadOnlyMemory<byte> SharedPaletteBgrx { get; }

    /// <summary>Content probe: the family framing with the <c>tile</c> tag.</summary>
    public static bool IsTile(ReadOnlySpan<byte> bytes)
    {
        return TacticsTagChunk.Is(bytes, Tag);
    }

    /// <summary>
    ///     Reads a whole <c>.til</c>: the header, the <c>&lt;tiledata&gt;</c> block, and nothing
    ///     after it. 29,957 of 29,957 retail tiles land exactly on the end of the file.
    /// </summary>
    public static TacticsTileFile Parse(ReadOnlyMemory<byte> bytes, string name)
    {
        var cursor = new TacticsCursor(bytes, name);
        var header = ReadHeader(cursor);

        var start = cursor.Position;
        var chunk = cursor.Tag(DataTag);
        if (!TryVersion(chunk.Version, out var dataVersion) || dataVersion != DataVersion)
        {
            throw cursor.Fail(start,
                $"{UnknownTileDataMessage}: '<{DataTag}>' version '{chunk.Version}' is not {DataVersion}");
        }

        var count = cursor.Count(MaximumImages, "image");
        var images = new List<TacticsTileImage>(count);
        for (var i = 0; i < count; i++)
        {
            var image = TacticsZarImage.Read(cursor);
            var extra = cursor.Bytes(ImageExtraLength, $"image {i}'s trailing bytes").Span;
            images.Add(new TacticsTileImage(
                image,
                BitConverter.ToUInt32(extra),
                BitConverter.ToUInt32(extra[4..])));
        }

        // FUN_006f0740 returns before the palette read when the count is zero, so a tile with no
        // images legitimately ends here.
        var palette = ReadOnlyMemory<byte>.Empty;
        if (count > 0)
        {
            var entries = cursor.Count(TacticsZarImage.RetailPaletteEntries * 16, "shared palette");
            palette = cursor.Bytes(entries * 4, "the shared palette");
        }

        cursor.RequireEnd($"<{Tag}> file");
        return new TacticsTileFile(header, images, palette);
    }

    /// <summary>Reads a whole <c>.til</c>, reporting why rather than throwing.</summary>
    public static bool TryParse(ReadOnlyMemory<byte> bytes, string name, out TacticsTileFile tile, out string error)
    {
        try
        {
            tile = Parse(bytes, name);
            error = string.Empty;
            return true;
        }
        catch (InvalidDataException e)
        {
            tile = null!;
            error = e.Message;
            return false;
        }
    }

    /// <summary>
    ///     Reads one <c>&lt;tile&gt;</c> record at the cursor and leaves it positioned after it —
    ///     <c>FUN_006f04c0</c> field for field. This is also the mission grid's record, which is why
    ///     it is public rather than folded into <see cref="Parse" />.
    /// </summary>
    public static TacticsTileHeader ReadHeader(TacticsCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);

        var start = cursor.Position;
        var chunk = cursor.Tag(Tag);
        if (!TryVersion(chunk.Version, out var version) || version is < MinimumVersion or > MaximumVersion)
        {
            throw cursor.Fail(start,
                $"{UnknownTileMessage}: '<{Tag}>' version '{chunk.Version}' is not {MinimumVersion}..{MaximumVersion}");
        }

        var boxX = cursor.U8();
        var boxY = cursor.U8();
        var boxZ = cursor.U8();
        var footX = cursor.I32();
        var footY = cursor.I32();
        var imageWidth = cursor.I32();
        var imageHeight = cursor.I32();
        var type = cursor.U8();
        var material = cursor.U8();

        // The version-shaped flag word. FUN_006f04c0 zeroes the field first, so every bit an older
        // version does not carry stays clear.
        ushort flags = 0;
        var discardedA = (byte?)null;
        var discardedB = (byte?)null;
        if (version < 9)
        {
            discardedA = cursor.U8();
        }

        if (version < 8)
        {
            flags = (ushort)(cursor.U8() & 1);
            if (version < 7)
            {
                discardedB = cursor.U8();
            }

            flags |= (ushort)((cursor.U8() & 1) << 1);
        }
        else if (version < 10)
        {
            flags = cursor.U8();
        }
        else
        {
            flags = cursor.U16();
        }

        return new TacticsTileHeader(
            version,
            boxX,
            boxY,
            boxZ,
            footX,
            footY,
            imageWidth,
            imageHeight,
            (TacticsTileType)type,
            (TacticsTileMaterial)material,
            (TacticsTileFlags)flags,
            discardedA,
            discardedB,
            cursor.Position - start);
    }

    private static bool TryVersion(string text, out int version)
    {
        // FUN_00703eb0 reads at most four version characters before atoi()ing them.
        version = 0;
        return text.Length <= 4
               && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out version);
    }
}
