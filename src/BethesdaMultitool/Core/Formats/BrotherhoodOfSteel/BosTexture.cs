using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Imaging;

namespace BethesdaMultitool.Core.Formats.BrotherhoodOfSteel;

/// <summary>One host-to-local IMAGE transfer inside a <see cref="BosTexture" />.</summary>
/// <param name="Index">Position among the texture's transfers (0 is the CLUT when the texture has one).</param>
/// <param name="BasePointer">
///     BITBLTBUF DBP — the destination in 256-byte GS words-of-64 units (0 in every file; relocated
///     at run time).
/// </param>
/// <param name="BufferWidth">BITBLTBUF DBW — the destination buffer width in 64-pixel pages.</param>
/// <param name="PixelFormat">BITBLTBUF DPSM — 0 (PSMCT32) on every shipped transfer.</param>
/// <param name="X">TRXPOS DSAX — destination x in PSMCT32 pixels.</param>
/// <param name="Y">TRXPOS DSAY — destination y.</param>
/// <param name="Width">TRXREG RRW.</param>
/// <param name="Height">TRXREG RRH.</param>
/// <param name="DataOffset">Offset of the image data inside the section.</param>
/// <param name="Length">Bytes of image data (== Width × Height × 4).</param>
internal readonly record struct BosTextureUpload(
    int Index,
    int BasePointer,
    int BufferWidth,
    int PixelFormat,
    int X,
    int Y,
    int Width,
    int Height,
    int DataOffset,
    int Length);

/// <summary>
///     A <c>.tex</c> texture of Fallout: Brotherhood of Steel — a 128-byte header followed by the
///     GS GIF packets that upload it, kept as a memory image the engine relocates in place
///     (<c>0x0019F0E8</c>: <c>+0x1c</c> 0 → −1, <c>+0x10</c> += base). Original RE 2026-09-07 against
///     <c>SLUS_205.39</c> and the disc; visual oracle passed on the logo, the perk drawings and the
///     loading screen.
///     <para>
///         Header (LE): <c>+0</c> u16 width · <c>+2</c> u16 height · <c>+4</c> u16 · <c>+6</c> u16
///         payload QWORDS with <c>+6 × 16 + 0x80 == section size</c> · <c>+8</c> u32 flags (bit
///         <c>0x20</c> = true colour, 3 of 2,876; <c>0x100</c> = font glyph ranges, only inside
///         <c>.fnt</c>) · <c>+0xc</c> the low word of a TEX0 without its TBP0: bits 0–5 TBW (the PSMT8
///         buffer width in 64-texel units, 2 × the transfer's DBW), 6–11 PSM (0x13 = PSMT8, 0 on the
///         three true-colour),
///         <b>
///             12–15 TW = ceil(log2 width) and 16–19 TH = ceil(log2 height) on
///             2,876 of 2,876
///         </b>
///         , bit 20 TCC = 1 (RGBA) on all, bits 21–22 TFX = 3 (HIGHLIGHT2) on 2,524
///         / 0 (MODULATE) on 352, nothing above (measured 2026-09-07) · <c>+0x10</c>
///         = 0x80, the GIF offset · <c>+0x1c</c> = 0, the relocation flag. Then, from <c>+0x80</c>:
///         an A+D packet (<c>BITBLTBUF</c>/<c>TRXPOS</c>/<c>TRXREG</c>/<c>TRXDIR</c>) and an IMAGE
///         packet per transfer — the first a 16×16 PSMCT32 image that is the 256-entry CLUT in CSM1
///         order, the rest the pixels <b>as PSMCT32 at half the width and height</b>, i.e. PSMT8
///         texels in the GS block layout (<see cref="BosGsLayout" />). Non-power-of-two art splits
///         the pixels over several rectangles placed by <c>TRXPOS</c>.
///     </para>
///     <para>
///         ⚑ Measured over all 227 clumps: 2,876 sections are <c>.tex</c>-shaped; the packet walk
///         consumes every one exactly (2,876/2,876); every transfer is PSMCT32 with
///         <c>RRW × RRH × 4</c> bytes; 2,873 open with the CLUT and 3 (flag 0x20) are true colour;
///         the pixel rectangles cover the full <c>w/2 × h/2</c> on 2,411 of the 2,873 CLUT textures
///         (the 3 true-colour ones cover their full <c>w × h</c>, so <see cref="IsFullyCovered" />
///         holds on 2,414) and leave part of it un-uploaded on 462 (those texels are reported as
///         uncovered and rendered transparent — the game never samples them). Decoding through the GS layout is what turns
///         the "speckle"
///         of a linear read into the authored image; the game's own 256-entry block table
///         (<c>0x0019F400</c>) reproduces the same swizzle 256/256.
///     </para>
///     <para>
///         ⚠ The streamed <c>&lt;level&gt;.tex</c> sections of the <c>_T</c> clumps are NOT this
///         format: they are PACKS of textures indexed by the level's <c>.lmp</c> (open); none of
///         the 54 passes <see cref="IsTexture" />.
///     </para>
/// </summary>
internal sealed class BosTexture
{
    /// <summary>Bytes of header before the GIF packets.</summary>
    public const int HeaderLength = 0x80;

    /// <summary>Flag bit set on the three true-colour textures (no CLUT; the pixels are the image).</summary>
    public const uint TrueColourFlag = 0x20;

    /// <summary>Bytes of a 256-entry CLUT transfer.</summary>
    public const int ClutLength = 1024;

    /// <summary>GS alpha meaning fully opaque (0x80 = 1.0).</summary>
    public const byte OpaqueAlpha = 0x80;

    private const int RegisterBitbltbuf = 0x50;
    private const int RegisterTrxpos = 0x51;
    private const int RegisterTrxreg = 0x52;
    private const ulong AdRegisterList = 0xE;

    private BosTexture(string name, int width, int height, ushort unknown4, ushort payloadQwords, uint flags,
        uint packed,
        IReadOnlyList<BosTextureUpload> uploads, uint[]? clut, byte[]? indices, byte[] rgba, int coveredTexels,
        bool alphaIsFlag)
    {
        Name = name;
        Width = width;
        Height = height;
        Unknown4 = unknown4;
        PayloadQwords = payloadQwords;
        Flags = flags;
        Packed = packed;
        Uploads = uploads;
        Clut = clut;
        Indices = indices;
        Rgba = rgba;
        CoveredTexels = coveredTexels;
        AlphaIsFlag = alphaIsFlag;
    }

    /// <summary>Source name, for messages.</summary>
    public string Name { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>The header's <c>+4</c> — not established (20 on 1,020 of 2,876, then 260, 24, 68, …).</summary>
    public ushort Unknown4 { get; }

    /// <summary>The header's <c>+6</c>: qwords of GIF data after the header.</summary>
    public ushort PayloadQwords { get; }

    /// <summary>The header's <c>+8</c>.</summary>
    public uint Flags { get; }

    /// <summary>The header's <c>+0xc</c>: the low word of a TEX0 without TBP0 (TBW, PSM, TW, TH, TCC, TFX).</summary>
    public uint Packed { get; }

    /// <summary>The PSM the packed word names (bits 6–11).</summary>
    public int PixelStorageFormat => (int)((Packed >> 6) & 0x3F);

    /// <summary>TEX0 TW, bits 12–15: <c>ceil(log2 Width)</c> on every shipped texture.</summary>
    public int WidthLog2 => (int)((Packed >> 12) & 0xF);

    /// <summary>TEX0 TH, bits 16–19: <c>ceil(log2 Height)</c> on every shipped texture.</summary>
    public int HeightLog2 => (int)((Packed >> 16) & 0xF);

    /// <summary>Every IMAGE transfer, in packet order.</summary>
    public IReadOnlyList<BosTextureUpload> Uploads { get; }

    /// <summary>The 256-entry CLUT in LOGICAL order as stored (R | G≪8 | B≪16 | A≪24, alpha 0–0x80), or null for true colour.</summary>
    public uint[]? Clut { get; }

    /// <summary>Row-major PSMT8 texel indices, or null for true colour.</summary>
    public byte[]? Indices { get; }

    /// <summary>
    ///     Row-major RGBA8. Alpha is rescaled from the GS 0x80-opaque convention, except on a
    ///     texture whose sampled alphas are all 0 or 1 (<see cref="AlphaIsFlag" />), where 1 is
    ///     opaque; uncovered texels are transparent black.
    /// </summary>
    public byte[] Rgba { get; }

    /// <summary>Texels whose GS address was written by a pixel transfer; <c>Width × Height</c> when fully covered.</summary>
    public int CoveredTexels { get; }

    /// <summary>
    ///     True when every sampled texel's GS alpha is 0 or 1 — 2,137 of the 2,873 CLUT textures
    ///     on the disc, whose alpha is a coverage flag rather than a translucency (an alpha of
    ///     1/128 is not a visible surface in any blend mode). Their <see cref="Rgba" /> alpha is
    ///     0 or 255; the 736 graded textures are scaled from 0x80 = opaque.
    /// </summary>
    public bool AlphaIsFlag { get; }

    public bool IsTrueColour => Clut is null;

    public bool IsFullyCovered => CoveredTexels == Width * Height;

    /// <summary>
    ///     Shape probe: <c>+0x10 == 0x80</c>, <c>+0x1c == 0</c>, <c>+6 × 16 + 0x80 == length</c> and a
    ///     plausible size. Exact on every one of the 2,876 sections it accepts; the 54 streamed
    ///     texture packs and every other section family fail it.
    /// </summary>
    public static bool IsTexture(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderLength + 16)
        {
            return false;
        }

        var width = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        var height = BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]);
        var qwords = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]);
        return width is > 0 and <= 2048 && height is > 0 and <= 2048 &&
               BinaryPrimitives.ReadUInt32LittleEndian(bytes[0x10..]) == HeaderLength &&
               BinaryPrimitives.ReadUInt32LittleEndian(bytes[0x1c..]) == 0 &&
               qwords * 16 + HeaderLength == bytes.Length;
    }

    /// <summary>Every section of <paramref name="clump" /> that passes <see cref="IsTexture" />, in file order.</summary>
    public static IEnumerable<BosClumpSection> FindTextureSections(ReadOnlyMemory<byte> bytes, BosClumpFile clump)
    {
        ArgumentNullException.ThrowIfNull(clump);
        return clump.Sections.Where(section => IsTexture(bytes.Span.Slice(section.Offset, section.Size)));
    }

    /// <summary>Parses and decodes a texture, throwing <see cref="InvalidDataException" /> when it does not fit.</summary>
    public static BosTexture Parse(ReadOnlySpan<byte> bytes, string name)
    {
        if (!TryParse(bytes, name, out var texture, out var error))
        {
            throw new InvalidDataException(error);
        }

        return texture;
    }

    /// <summary>Parses and decodes a texture, reporting why rather than throwing.</summary>
    public static bool TryParse(ReadOnlySpan<byte> bytes, string name, out BosTexture texture, out string error)
    {
        texture = null!;
        if (!IsTexture(bytes))
        {
            error = $"{name}: not a .tex (needs +0x10 == 0x80, +0x1c == 0 and +6 × 16 + 0x80 == {bytes.Length}).";
            return false;
        }

        var width = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        var height = BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]);
        var unknown4 = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
        var qwords = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]);
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        var packed = BinaryPrimitives.ReadUInt32LittleEndian(bytes[0xc..]);

        if (!TryWalkPackets(bytes, name, out var uploads, out error))
        {
            return false;
        }

        if (uploads.Count == 0)
        {
            error = $"{name}: the GIF packets carry no IMAGE transfer.";
            return false;
        }

        // ⚑ The CLUT is the first transfer, a 16×16 PSMCT32 image (2,873 of 2,876); the three
        // true-colour textures carry flag 0x20 and no CLUT. The flag and the content agree on
        // 2,876/2,876, and a disagreement means this is not the layout measured here.
        var first = uploads[0];
        var hasClut = first.Width == 16 && first.Height == 16 && first.Length == ClutLength &&
                      (flags & TrueColourFlag) == 0;
        if (!hasClut && (flags & TrueColourFlag) == 0)
        {
            error =
                $"{name}: the first transfer is {first.Width}×{first.Height}, not a 16×16 CLUT, yet flag 0x20 is clear.";
            return false;
        }

        var pixelUploads = hasClut ? uploads.Skip(1).ToList() : uploads.ToList();
        if (pixelUploads.Count == 0)
        {
            error = $"{name}: a CLUT with no pixel transfer.";
            return false;
        }

        foreach (var upload in pixelUploads)
        {
            if (upload.PixelFormat != BosGsLayout.Psmct32)
            {
                error =
                    $"{name}: transfer {upload.Index} is PSM 0x{upload.PixelFormat:X}, not the PSMCT32 every shipped transfer uses.";
                return false;
            }

            if (upload.BufferWidth <= 0)
            {
                error = $"{name}: transfer {upload.Index} has a zero buffer width.";
                return false;
            }
        }

        var pixelBase = pixelUploads[0].BasePointer;
        var pixelBufferWidth = pixelUploads[0].BufferWidth;
        var memoryLength = RequiredMemory(pixelUploads, height, pixelBase, pixelBufferWidth);
        var memory = new byte[memoryLength];
        var written = new byte[memoryLength];
        foreach (var upload in pixelUploads)
        {
            var data = bytes.Slice(upload.DataOffset, upload.Length);
            for (var y = 0; y < upload.Height; y++)
            {
                for (var x = 0; x < upload.Width; x++)
                {
                    var address = upload.BasePointer * 256 +
                                  BosGsLayout.Psmct32WordAddress(upload.X + x, upload.Y + y, upload.BufferWidth) * 4;
                    var source = (y * upload.Width + x) * 4;
                    data.Slice(source, 4).CopyTo(memory.AsSpan(address, 4));
                    written.AsSpan(address, 4).Fill(1);
                }
            }
        }

        var rgba = new byte[width * height * 4];
        var covered = 0;
        var alphaIsFlag = false;
        byte[]? indices = null;
        uint[]? clut = null;
        if (hasClut)
        {
            clut = BosGsLayout.ReadClut256(bytes.Slice(first.DataOffset, first.Length));
            indices = new byte[width * height];
            var coveredTexels = new bool[width * height];
            var maxSampledAlpha = 0;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var address = pixelBase * 256 + BosGsLayout.Psmt8ByteAddress(x, y, pixelBufferWidth);
                    var texel = y * width + x;
                    if (written[address] == 0)
                    {
                        continue;
                    }

                    covered++;
                    coveredTexels[texel] = true;
                    var index = memory[address];
                    indices[texel] = index;
                    var colour = clut[index];
                    var gsAlpha = (byte)(colour >> 24);
                    maxSampledAlpha = Math.Max(maxSampledAlpha, gsAlpha);
                    rgba[texel * 4] = (byte)colour;
                    rgba[texel * 4 + 1] = (byte)(colour >> 8);
                    rgba[texel * 4 + 2] = (byte)(colour >> 16);
                    rgba[texel * 4 + 3] = gsAlpha;
                }
            }

            // ⚑ ALPHA IS A FLAG ON 2,137 OF THE 2,873 CLUT TEXTURES (measured 2026-09-07): every
            // texel they sample carries a GS alpha of 0 or 1 — 1,885 of them from a CLUT whose
            // every entry is ≤ 1, 252 from the low entries of a graded CLUT. At the GS scale
            // (0x80 = 1.0) an alpha of 1 is 0.8%: no blend mode could show such a surface, so
            // the game can only be drawing them unblended, and the value marks coverage, not
            // translucency. Scaling it would export 2,137 textures — the level walls, floors
            // and armour — as blank PNGs. The other 736 carry a graded ramp and are scaled.
            alphaIsFlag = maxSampledAlpha <= 1;
            for (var texel = 0; texel < coveredTexels.Length; texel++)
            {
                if (coveredTexels[texel])
                {
                    var at = texel * 4 + 3;
                    if (alphaIsFlag)
                    {
                        rgba[at] = rgba[at] == 0 ? (byte)0 : (byte)255;
                    }
                    else
                    {
                        rgba[at] = ScaleAlpha(rgba[at]);
                    }
                }
            }
        }
        else
        {
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var address = pixelBase * 256 + BosGsLayout.Psmct32WordAddress(x, y, pixelBufferWidth) * 4;
                    var texel = y * width + x;
                    if (written[address] == 0)
                    {
                        continue;
                    }

                    covered++;
                    rgba[texel * 4] = memory[address];
                    rgba[texel * 4 + 1] = memory[address + 1];
                    rgba[texel * 4 + 2] = memory[address + 2];
                    rgba[texel * 4 + 3] = ScaleAlpha(memory[address + 3]);
                }
            }
        }

        texture = new BosTexture(name, width, height, unknown4, qwords, flags, packed, uploads, clut, indices, rgba,
            covered, alphaIsFlag);
        error = string.Empty;
        return true;
    }

    /// <summary>The GS alpha convention: 0x80 is opaque, so 8-bit alpha is <c>a × 255 / 128</c>, saturated.</summary>
    public static byte ScaleAlpha(byte gsAlpha)
    {
        return (byte)Math.Min(255, (gsAlpha * 255 + 64) / 128);
    }

    /// <summary>The decoded image as the texture seam the PNG writer and GUI consume.</summary>
    public DecodedTexture ToDecodedTexture()
    {
        return DecodedTexture.FromBaseLevel(Rgba, Width, Height, false);
    }

    /// <summary>The PSMT8 indices as a bitmap (all zero for a true-colour texture, which has none).</summary>
    public IndexedBitmap ToIndexedBitmap()
    {
        return new IndexedBitmap(Width, Height, Indices ?? new byte[Width * Height]);
    }

    /// <summary>
    ///     Walks the GIF packets from <c>+0x80</c>: A+D packets (FLG 0, NREG 1, REGS 0xE) set the
    ///     transfer registers, IMAGE packets (FLG 2) carry the data. The walk must end exactly at
    ///     the section's end — it does on 2,876/2,876.
    /// </summary>
    private static bool TryWalkPackets(ReadOnlySpan<byte> bytes, string name, out List<BosTextureUpload> uploads,
        out string error)
    {
        uploads = [];
        ulong bitbltbuf = 0;
        ulong trxpos = 0;
        ulong trxreg = 0;
        var at = HeaderLength;
        while (at < bytes.Length)
        {
            if (at + 16 > bytes.Length)
            {
                error = $"{name}: {bytes.Length - at} stray bytes after the last GIF packet.";
                return false;
            }

            var tagLow = BinaryPrimitives.ReadUInt64LittleEndian(bytes[at..]);
            var tagHigh = BinaryPrimitives.ReadUInt64LittleEndian(bytes[(at + 8)..]);
            var loops = (int)(tagLow & 0x7FFF);
            var flag = (int)((tagLow >> 58) & 3);
            var registerCount = (int)((tagLow >> 60) & 0xF);
            at += 16;

            if (flag == 0 && registerCount == 1 && tagHigh == AdRegisterList)
            {
                if (at + loops * 16 > bytes.Length)
                {
                    error = $"{name}: an A+D packet of {loops} registers runs past the section.";
                    return false;
                }

                for (var i = 0; i < loops; i++)
                {
                    var data = BinaryPrimitives.ReadUInt64LittleEndian(bytes[at..]);
                    var register = bytes[at + 8];
                    switch (register)
                    {
                        case RegisterBitbltbuf:
                            bitbltbuf = data;
                            break;
                        case RegisterTrxpos:
                            trxpos = data;
                            break;
                        case RegisterTrxreg:
                            trxreg = data;
                            break;
                    }

                    at += 16;
                }

                continue;
            }

            if (flag == 2)
            {
                var length = loops * 16;
                if (at + length > bytes.Length)
                {
                    error = $"{name}: an IMAGE packet of {length} bytes runs past the section.";
                    return false;
                }

                var upload = new BosTextureUpload(
                    uploads.Count,
                    (int)((bitbltbuf >> 32) & 0x3FFF),
                    (int)((bitbltbuf >> 48) & 0x3F),
                    (int)((bitbltbuf >> 56) & 0x3F),
                    (int)((trxpos >> 32) & 0x7FF),
                    (int)((trxpos >> 48) & 0x7FF),
                    (int)(trxreg & 0xFFF),
                    (int)((trxreg >> 32) & 0xFFF),
                    at,
                    length);
                if (upload.Width * upload.Height * 4 != length)
                {
                    error =
                        $"{name}: IMAGE packet {uploads.Count} carries {length} bytes for a {upload.Width}×{upload.Height} PSMCT32 transfer.";
                    return false;
                }

                uploads.Add(upload);
                at += length;
                continue;
            }

            error =
                $"{name}: GIF tag at +0x{at - 16:X} has FLG {flag} / NREG {registerCount} / REGS 0x{tagHigh:X}, which is neither A+D nor IMAGE.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    ///     Bytes of emulated GS memory the transfers and the read-back can touch: PSMCT32 pages are
    ///     64×32 pixels and PSMT8 pages 128×64 texels, both 8,192 bytes; one extra page row covers a
    ///     transfer whose x reaches past the buffer width.
    /// </summary>
    private static int RequiredMemory(List<BosTextureUpload> pixelUploads, int height, int pixelBase,
        int pixelBufferWidth)
    {
        var bytes = pixelBase * 256L + ((height + 63) / 64 + 1L) * pixelBufferWidth * 8192;
        foreach (var upload in pixelUploads)
        {
            var rows = (upload.Y + upload.Height + 31) / 32 + 1L;
            bytes = Math.Max(bytes, upload.BasePointer * 256L + rows * upload.BufferWidth * 8192);
        }

        return checked((int)bytes);
    }
}
