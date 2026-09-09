using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Imaging;

namespace BethesdaMultitool.Core.Formats.BrotherhoodOfSteel;

/// <summary>
///     A texture section of the <b>Xbox</b> release of Fallout: Brotherhood of Steel (2004) — an
///     8-bit palettized image the loader hands straight to Direct3D as <c>D3DFMT_P8</c>. Original
///     RE 2026-09-08 off <c>default.xbe</c>, the asset-type dispatch at <c>0x000399A0</c> (type 2 =
///     texture) into the loader at <c>0x00084EC0</c>.
///     <para>
///         ⚑ <b>What the loader does, line by line</b> (<c>0x00084EC0</c>, entered with the section
///         base in <c>param_1</c>): it returns immediately unless <c>+0x14</c> is 0 (the
///         already-relocated flag, which it then sets to <c>-1</c>); it turns <c>+8</c> from an
///         OFFSET into a pointer (<c>*(int*)(base+8) += base</c>); it acts only when
///         <c>byte(+5) &amp; 0x40</c> is set; it rounds width and height UP to powers of two, calls
///         <c>CreateTexture(w2, h2, 1, 1, 0, <b>0x0B</b>, 3)</c> — <c>D3DFMT_P8</c> — copies the
///         image ROW BY ROW (<c>memcpy(dst, data + <b>0x400</b> + width*row, width)</c>, dst
///         advancing by <c>w2</c>), then <c>XGSwizzleRect</c>s that linear buffer into the texture
///         and builds a 256-entry <c>D3DPalette</c> from the first <c>0x400</c> bytes as
///         <c>A &lt;&lt; 24 | b0 &lt;&lt; 16 | b1 &lt;&lt; 8 | b2</c>, i.e. the file stores
///         <b>R, G, B, A</b>.
///     </para>
///     <para>
///         ⚠⚠ <b>The loader also SATURATES the palette, and it does so on every retail texture</b>
///         (<c>0x00085083</c>: <c>test byte ptr [esi+4], 8</c> — flags bit <c>0x08</c> — then
///         <c>jne</c> PAST the boost). When that bit is CLEAR each of the three colour components
///         becomes <c>(c − mean) × 1.3 + mean + 0.5</c>, truncated and clamped to 0…255, with
///         <c>mean = (r + g + b) × 0.33333334</c>; the arithmetic is SSE single-precision
///         (<c>subss/mulss/addss/cvttss2si</c>), so <see cref="float" /> reproduces it exactly, and
///         alpha is written through untouched. Every shipped flags low byte is <c>0x04</c>,
///         <c>0x14</c>, <c>0x44</c> or <c>0x54</c>, so bit 3 is clear on <b>3,686 of 3,686</b> and
///         the console draws EVERY texture 30% more saturated than the bytes on the disc.
///         <see cref="Rgba" /> is the STORED image — that is what matches the PS2 decode byte for
///         byte — and <see cref="RgbaAsDrawn" /> is what the console puts on screen.
///     </para>
///     <para>
///         So the layout is: <c>+0</c> u16 width · <c>+2</c> u16 height · <c>+4</c> u32 flags
///         (bit <c>0x4000</c> = this palettized form; <c>0x4014</c> on 2,873 sections, then
///         <c>0xC014</c> 412, <c>0xC044</c> 207, <c>0x4044</c> 91, <c>0xD044</c> 51,
///         <c>0xC054</c> 47, and four rarer values over the last 5 — the other bits are NOT
///         decoded) · <c>+8</c> u32 <b>data offset</b>, <c>0x38</c> on 3,686 of 3,686 ·
///         <c>+12</c> u32 0 · <c>+0x14</c> u32 relocation flag. At the data offset: a 1,024-byte
///         RGBA palette, then <c>width × height</c> row-major indices at the ACTUAL width — the
///         power-of-two rounding and the swizzle happen at load time, so
///         <b>
///             nothing on disc is
///             swizzled
///         </b>
///         .
///     </para>
///     <para>
///         ⚑ <b>EXACT TILING, 3,686 of 3,686.</b> <c>dataOffset + 1024 + width × height</c> equals
///         the section length on every section this accepts, across all 230 Xbox clumps (21,905
///         sections).
///     </para>
///     <para>
///         ⚑⚑
///         <b>
///             The control that makes that mean something is the RIVAL LENGTH, run over the whole
///             corpus
///         </b>
///         (re-measured 2026-09-08): a reader with the palette length replaced by 0, 256,
///         512, 768 or 2,048 accepts <b>0 of the 21,905 sections</b>, and one that ignores
///         <c>+8</c> for a fixed header length of 16, 24, 32, 48, 64 or 128 accepts <b>0</b> as
///         well. Only 1,024 and <c>0x38</c> fit anything at all, and they fit exactly the same
///         3,686. The layout is FORCED by the data, not merely compatible with it.
///     </para>
///     <para>
///         ⚠ <b>What the flag bit alone does NOT do.</b> <c>5,682</c> of the 21,905 sections carry
///         bit <c>0x4000</c> somewhere in their <c>+4</c> dword; 3,686 are textures and the other
///         <b>1,996 are refused before the size arithmetic is ever reached</b> — 1,948 by the
///         dimension gate and 48 by the LENGTH gate, which is <c>length &lt;= 0x38 + 1024</c> and so
///         catches 47 sections strictly shorter than a header plus a palette AND one that is
///         exactly 1,080 bytes (<c>global_x.clp#B3D41265</c>, a 0 × 0 placeholder: header, palette,
///         no pixels). ⚠ That one is why the gate must be read as <c>&lt;=</c> rather than
///         <c>&lt;</c>, and it never reaches the dimension check. 1,725 of those
///         refusals are RIFF WAVE sound sections whose <c>riffSize</c> happens to carry the bit
///         (their width/height read as <c>'RIFF'</c> = 18,770 × 17,990 and their data offset as
///         <c>'WAVE'</c>); the other 271 are non-RIFF. ⛔ An earlier note here said "223 further
///         sections … every one of them fails the SIZE ARITHMETIC": both the count and the
///         mechanism were wrong — 223 is only the non-RIFF share of the dimension-gate refusals,
///         and nothing in this corpus is refused by the arithmetic.
///     </para>
///     <para>
///         ⚑⚑ <b>CROSS-CONSOLE ORACLE — the strongest check available, and it passes.</b> The two
///         discs key their clump sections with the same <see cref="BosAssetHash" />, so a texture
///         can be decoded on BOTH and the results compared. 858 tags carry a texture on each disc;
///         838 agree on dimensions, and on <b>590 of them the decoded RGB is BYTE-IDENTICAL</b> to
///         the PS2 decode through <see cref="BosTexture" /> — a completely different container, a
///         completely different swizzle, the same pixels. 74 more correlate above 0.99 (the same
///         art through a different 256-colour quantiser) and 3 above 0.9. A wrong reading of this
///         layout scores zero. ⚠ The remaining 168 are HUD/inventory strips and <c>sfx</c> art that
///         genuinely differs between the ports, plus PS2 decodes with un-uploaded regions.
///         ⚑ Of the 20 whose dimensions differ, ALL are <c>(w, odd h)</c> on Xbox against
///         <c>(w, h+1)</c> on PS2: the GS uploads a PSMT8 image as PSMCT32 at half height, so the
///         PS2 build padded every odd height to an even one.
///     </para>
///     <para>
///         ⚠ <b>Alpha is 0–254, not 0–255.</b> 3,465 of the 3,686 palettes peak at exactly 254.
///         Measured against the PS2 CLUTs whose RGB is byte-identical: on the 349 pairs whose PS2
///         CLUT is flag-alpha (every entry 0 or 1) the map is 0 → 0 and 1 → 254 on
///         <b>
///             349 of
///             349
///         </b>
///         ; on 171 of 299 graded pairs it is exactly <c>(2 × a) &amp; 0xFF</c>, which wraps
///         a PS2 0x80 to 0. The loader writes the byte straight into <c>A8R8G8B8</c>, so
///         <see cref="Rgba" /> reports it unscaled and 254 is what "opaque" looks like here.
///     </para>
///     <para>
///         ⛔ This is NOT a DXT/BCn format and there is no swizzle to undo — both were plausible
///         (an Xbox port would normally use them) and both are refuted by the loader, which asks
///         for <c>D3DFMT_P8</c> and does the swizzling itself. ⛔ It is also NOT
///         <see cref="BosTexture" />: the PS2 sections are GIF packet streams and none of them
///         satisfies <see cref="IsTexture" />.
///     </para>
/// </summary>
internal sealed class BosXboxTexture
{
    /// <summary>
    ///     Bytes of texture header before the data offset can point anywhere useful. The loader
    ///     reads <c>+0</c>…<c>+0x14</c> inclusive (<c>+0x14</c> is the relocation flag it sets to
    ///     <c>-1</c>), so the struct is <c>0x18</c> bytes and no payload can begin inside it.
    ///     ⚠ This is a constant of THIS type: <see cref="BosClumpFile.HeaderLength" /> happens to
    ///     be 0x18 as well, and the probe used to borrow it — a coincidence that would have turned
    ///     an unrelated container change into a silent change of this gate.
    /// </summary>
    public const int HeaderLength = 0x18;

    /// <summary>Bytes of palette before the indices: 256 entries of RGBA.</summary>
    public const int PaletteLength = 1024;

    /// <summary>
    ///     The flag bit the loader tests (<c>byte(+5) &amp; 0x40</c>) before treating a section as
    ///     a palettized image.
    /// </summary>
    public const uint PalettizedFlag = 0x4000;

    /// <summary>The data offset every shipped texture declares.</summary>
    public const int StandardDataOffset = 0x38;

    /// <summary>The largest alpha any shipped palette carries; see the type remarks.</summary>
    public const byte OpaqueAlpha = 254;

    /// <summary>
    ///     The flags bit that SUPPRESSES the loader's palette saturation boost. Clear on 3,686 of
    ///     the 3,686 shipped textures, so the boost is applied to all of them.
    /// </summary>
    public const uint SaturationSuppressedFlag = 0x08;

    /// <summary>The loader's saturation factor (<c>0x000E6850</c> = <c>1.3f</c>).</summary>
    public const float SaturationFactor = 1.3f;

    /// <summary>The loader's reciprocal-of-three constant (<c>0x000E40FC</c> = <c>0.33333334f</c>).</summary>
    public const float OneThird = 0.33333334f;

    private BosXboxTexture(
        string name, int width, int height, uint flags, int dataOffset, uint[] palette, byte[] indices, byte[] rgba)
    {
        Name = name;
        Width = width;
        Height = height;
        Flags = flags;
        DataOffset = dataOffset;
        Palette = palette;
        Indices = indices;
        Rgba = rgba;
    }

    /// <summary>Source name, for messages.</summary>
    public string Name { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>
    ///     The header's <c>+4</c>. Two bits are decoded: <see cref="PalettizedFlag" /> selects this
    ///     form and <see cref="SaturationSuppressedFlag" /> suppresses the loader's saturation
    ///     boost. The rest are not established — the shipped values are <c>0x4014</c> (2,873),
    ///     <c>0xC014</c> (412), <c>0xC044</c> (207), <c>0x4044</c> (91), <c>0xD044</c> (51),
    ///     <c>0xC054</c> (47) and four rarer values over the last 5.
    /// </summary>
    public uint Flags { get; }

    /// <summary>The header's <c>+8</c>: where the palette begins, <see cref="StandardDataOffset" /> on every shipped section.</summary>
    public int DataOffset { get; }

    /// <summary>The 256-entry palette as stored, packed <c>R | G≪8 | B≪16 | A≪24</c>.</summary>
    public uint[] Palette { get; }

    /// <summary>Row-major 8-bit palette indices, <see cref="Width" /> × <see cref="Height" /> of them.</summary>
    public byte[] Indices { get; }

    /// <summary>
    ///     Row-major RGBA8 <b>as STORED</b> — the palette byte straight through, which is what
    ///     matches the PS2 decode. Alpha is the stored byte; see the remarks on the 0–254 range.
    ///     ⚠ It is NOT what the console draws: see <see cref="RgbaAsDrawn" />.
    /// </summary>
    public byte[] Rgba { get; }

    /// <summary>
    ///     True when the loader would apply its saturation boost to this texture's palette — i.e.
    ///     flags bit <see cref="SaturationSuppressedFlag" /> is clear. Every shipped texture.
    /// </summary>
    public bool IsSaturationBoosted => (Flags & SaturationSuppressedFlag) == 0;

    /// <summary>True when both dimensions are powers of two (2,782 of the 3,686 shipped sections).</summary>
    public bool IsPowerOfTwo => IsPowerOfTwoValue(Width) && IsPowerOfTwoValue(Height);

    /// <summary>
    ///     Content probe. The flag bit alone is nowhere near enough — 5,682 sections carry it and
    ///     only 3,686 are textures — so the dimensions, the offset and the size arithmetic all
    ///     have to hold. See the type remarks for which gate refuses what.
    /// </summary>
    public static bool IsTexture(ReadOnlySpan<byte> bytes)
    {
        return TryReadHeader(bytes, out _, out _, out _, out _);
    }

    /// <summary>Parses and decodes, throwing <see cref="InvalidDataException" /> when it does not fit.</summary>
    public static BosXboxTexture Parse(ReadOnlySpan<byte> bytes, string name)
    {
        if (!TryParse(bytes, name, out var texture, out var error))
        {
            throw new InvalidDataException(error);
        }

        return texture;
    }

    /// <summary>Parses and decodes, reporting why rather than throwing.</summary>
    public static bool TryParse(ReadOnlySpan<byte> bytes, string name, out BosXboxTexture texture, out string error)
    {
        texture = null!;
        if (!TryReadHeader(bytes, out var width, out var height, out var flags, out var dataOffset))
        {
            error = $"{name}: not an Xbox .tex (needs flag 0x{PalettizedFlag:X4} and " +
                    $"dataOffset + {PaletteLength} + width × height == {bytes.Length}).";
            return false;
        }

        var palette = new uint[256];
        for (var i = 0; i < 256; i++)
        {
            var at = dataOffset + i * 4;
            palette[i] = (uint)(bytes[at] | (bytes[at + 1] << 8) | (bytes[at + 2] << 16) | (bytes[at + 3] << 24));
        }

        var texels = width * height;
        var indices = bytes.Slice(dataOffset + PaletteLength, texels).ToArray();
        var rgba = new byte[texels * 4];
        for (var texel = 0; texel < texels; texel++)
        {
            var colour = palette[indices[texel]];
            var at = texel * 4;
            rgba[at] = (byte)colour;
            rgba[at + 1] = (byte)(colour >> 8);
            rgba[at + 2] = (byte)(colour >> 16);
            rgba[at + 3] = (byte)(colour >> 24);
        }

        texture = new BosXboxTexture(name, width, height, flags, dataOffset, palette, indices, rgba);
        error = string.Empty;
        return true;
    }

    /// <summary>Every section of <paramref name="clump" /> that passes <see cref="IsTexture" />, in file order.</summary>
    public static IEnumerable<BosClumpSection> FindTextureSections(ReadOnlyMemory<byte> bytes, BosClumpFile clump)
    {
        ArgumentNullException.ThrowIfNull(clump);
        return clump.Sections.Where(section => IsTexture(bytes.Span.Slice(section.Offset, section.Size)));
    }

    /// <summary>The decoded image as the texture seam the PNG writer and GUI consume.</summary>
    public DecodedTexture ToDecodedTexture()
    {
        return DecodedTexture.FromBaseLevel(Rgba, Width, Height, false);
    }

    /// <summary>
    ///     The image the CONSOLE draws: <see cref="Rgba" /> with the loader's palette saturation
    ///     boost applied, when <see cref="IsSaturationBoosted" /> says the loader would apply it
    ///     (every shipped texture). Identical to <see cref="Rgba" /> otherwise.
    /// </summary>
    public byte[] RgbaAsDrawn()
    {
        if (!IsSaturationBoosted)
        {
            return (byte[])Rgba.Clone();
        }

        var boosted = new uint[Palette.Length];
        for (var i = 0; i < Palette.Length; i++)
        {
            boosted[i] = BoostPaletteEntry(Palette[i]);
        }

        var rgba = new byte[Indices.Length * 4];
        for (var texel = 0; texel < Indices.Length; texel++)
        {
            var colour = boosted[Indices[texel]];
            var at = texel * 4;
            rgba[at] = (byte)colour;
            rgba[at + 1] = (byte)(colour >> 8);
            rgba[at + 2] = (byte)(colour >> 16);
            rgba[at + 3] = (byte)(colour >> 24);
        }

        return rgba;
    }

    /// <summary>
    ///     One palette entry through the loader's boost: each colour component becomes
    ///     <c>(c − mean) × 1.3 + mean + 0.5</c> truncated and clamped to 0…255, with the mean of
    ///     the three components computed as <c>(r + g + b) × 0.33333334</c>. Single-precision,
    ///     because the loader's arithmetic is <c>subss/mulss/addss/cvttss2si</c>. Alpha passes
    ///     through untouched.
    /// </summary>
    public static uint BoostPaletteEntry(uint packed)
    {
        var r = (int)(packed & 0xFF);
        var g = (int)((packed >> 8) & 0xFF);
        var b = (int)((packed >> 16) & 0xFF);
        var mean = (r + g + b) * OneThird;
        var a = packed & 0xFF000000u;
        return a |
               (uint)Boost(r, mean) |
               ((uint)Boost(g, mean) << 8) |
               ((uint)Boost(b, mean) << 16);
    }

    private static int Boost(int component, float mean)
    {
        var value = (int)((component - mean) * SaturationFactor + mean + 0.5f);
        return Math.Clamp(value, 0, 255);
    }

    /// <summary>The palette indices as a bitmap.</summary>
    public IndexedBitmap ToIndexedBitmap()
    {
        return new IndexedBitmap(Width, Height, Indices);
    }

    private static bool IsPowerOfTwoValue(int value)
    {
        return value > 0 && (value & (value - 1)) == 0;
    }

    private static bool TryReadHeader(
        ReadOnlySpan<byte> bytes, out int width, out int height, out uint flags, out int dataOffset)
    {
        width = 0;
        height = 0;
        flags = 0;
        dataOffset = 0;
        if (bytes.Length <= StandardDataOffset + PaletteLength)
        {
            return false;
        }

        width = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        height = BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]);
        flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        if (width is <= 0 or > 4096 || height is <= 0 or > 4096 || (flags & PalettizedFlag) == 0)
        {
            return false;
        }

        if (offset < HeaderLength || offset > (uint)bytes.Length)
        {
            return false;
        }

        dataOffset = (int)offset;
        return dataOffset + PaletteLength + width * height == bytes.Length;
    }
}
