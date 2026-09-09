// ORIGINAL CLEAN-ROOM IMPLEMENTATION.
//
// Written from our own reverse engineering of RAD Game Tools' Smacker decoder as statically linked
// into two shipped games — Redguard's RG.EXE ("*** Smacker Version: 3.2b***") and Battlespire's
// GAME.EXE ("*** Smacker Version: 3.0k***") — via our Ghidra decompilations at
// tools/GhidraProject/ClassicRE/RG.EXE.decompiled.txt and GAME.EXE.decompiled.txt and our own
// capstone disassembly of the unpacked LE images, as written up in the specification document
//   scratchpad .../gap2/smacker/SPEC.md  ("Smacker Video (SMK2) - Format Specification"),
//   sections 4 (palette chunk, FUN_000ce270) and 5 (block stream: FUN_001144b0 / FUN_001112e0 and
//   the 4x4 block handlers FUN_00113eb0 mono, FUN_00114050 full, FUN_001141d0 void,
//   FUN_00114320 solid, plus the 256 map-expander stubs at PTR_FUN_003aeb10).
//
// NO FFmpeg- or libav-derived code, and no other third-party Smacker implementation, was consulted,
// read, copied or paraphrased. ffmpeg was used only as a sealed black box: run as an executable,
// with its output pixels compared to ours.

namespace BethesdaMultitool.Core.Formats.Smacker;

/// <summary>
///     Decodes a Smacker movie's frames onto ONE persistent 8-bit canvas plus a persistent
///     256-entry palette, exactly as the game does: a frame's block stream only touches the blocks
///     it codes ("void" runs leave the canvas alone), and a frame without a palette chunk keeps the
///     palette of the frame before it.
///     <para>
///         ⚠ Because of that, frame N does not exist without every frame before it — there is no
///         intra-only coding and the retail corpus sets the keyframe bit on no frame at all —
///         so <see cref="DecodeFrame" /> replays from frame 0 when asked to seek backwards, and decodes forward otherwise. Frames are indexed, 1 byte
///         per pixel, so replaying is cheap and nothing is materialised.
///     </para>
/// </summary>
internal sealed class SmackerVideoDecoder
{
    /// <summary>
    ///     The 6-bit-to-8-bit palette expansion, 64 bytes dumped from RG.EXE at flat address
    ///     <c>0x3accc5</c> (GAME.EXE: <c>0xf5891</c>, byte-identical). Every palette byte in the
    ///     stream is an index into this table (<c>*puVar8 = (&amp;DAT_003accc5)[bVar3]</c>).
    /// </summary>
    internal static ReadOnlySpan<byte> PaletteMap =>
    [
        0x00, 0x04, 0x08, 0x0C, 0x10, 0x14, 0x18, 0x1C, 0x20, 0x24, 0x28, 0x2C, 0x30, 0x34, 0x38, 0x3C,
        0x41, 0x45, 0x49, 0x4D, 0x51, 0x55, 0x59, 0x5D, 0x61, 0x65, 0x69, 0x6D, 0x71, 0x75, 0x79, 0x7D,
        0x82, 0x86, 0x8A, 0x8E, 0x92, 0x96, 0x9A, 0x9E, 0xA2, 0xA6, 0xAA, 0xAE, 0xB2, 0xB6, 0xBA, 0xBE,
        0xC3, 0xC7, 0xCB, 0xCF, 0xD3, 0xD7, 0xDB, 0xDF, 0xE3, 0xE7, 0xEB, 0xEF, 0xF3, 0xF7, 0xFB, 0xFF
    ];

    /// <summary>
    ///     Run lengths indexed by bits 2-7 of a TYPE value, 64 dwords dumped from RG.EXE at flat
    ///     address <c>0x3ae9f0</c> (GAME.EXE: <c>0xf76f0</c>, identical): 1..59 then
    ///     128, 256, 512, 1024, 2048.
    /// </summary>
    internal static ReadOnlySpan<ushort> RunLengths =>
    [
        1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28,
        29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53, 54,
        55, 56, 57, 58, 59, 128, 256, 512, 1024, 2048
    ];

    private const int BlockMono = 0;
    private const int BlockVoid = 2;
    private const int BlockSolid = 3;

    private readonly SmackerFile _file;
    private readonly byte[] _canvas;
    private readonly int _stride;
    private readonly byte[] _palette = new byte[768];
    private readonly int _blocksWide;
    private readonly int _blocksHigh;
    private int _nextFrame;

    internal SmackerVideoDecoder(SmackerFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        _file = file;
        _blocksWide = (file.Width + 3) >> 2;
        _blocksHigh = (file.Height + 3) >> 2;
        // The canvas is padded to whole 4x4 blocks so a block on the last column or row never
        // writes outside its own rows; the retail corpus is 640x240 / 640x480 and needs none.
        _stride = _blocksWide * 4;
        _canvas = new byte[_stride * _blocksHigh * 4];
    }

    /// <summary>The container being decoded.</summary>
    internal SmackerFile File => _file;

    /// <summary>Copies the persistent indexed canvas after the most recent decode, row-major Width x Height.</summary>
    internal byte[] GetFrameIndices()
    {
        var width = _file.Width;
        var height = _file.Height;
        var indices = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            Buffer.BlockCopy(_canvas, y * _stride, indices, y * width, width);
        }

        return indices;
    }

    /// <summary>The persistent palette after the most recent decode, 256 x RGB, full-range 8-bit.</summary>
    internal ReadOnlySpan<byte> PaletteRgb => _palette;

    /// <summary>Index of the frame the next sequential decode would produce.</summary>
    internal int NextFrame => _nextFrame;

    /// <summary>Per-block-type counts over the most recent <see cref="DecodeNext" /> (mono, full, void, solid).</summary>
    internal int[] LastBlockCounts { get; } = new int[4];

    /// <summary>Bits of the most recent frame's video chunk the block stream consumed.</summary>
    internal long LastVideoBitsUsed { get; private set; }

    /// <summary>Rewinds to before frame 0: a zero canvas and a zero palette (SmackDoFrame zeroes both when it starts at frame 0).</summary>
    internal void Reset()
    {
        Array.Clear(_canvas);
        Array.Clear(_palette);
        _nextFrame = 0;
    }

    /// <summary>
    ///     Decodes frame <paramref name="index" /> (0..FrameCount-1, or FrameCount for the ring
    ///     frame when the file has one), replaying earlier frames as needed, and leaves the result
    ///     in <see cref="GetFrameIndices" /> / <see cref="PaletteRgb" />.
    /// </summary>
    internal void DecodeFrame(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _file.FrameOffsets.Count);

        if (index < _nextFrame - 1)
        {
            Reset();
        }
        else if (index == _nextFrame - 1)
        {
            return;
        }

        // ⚠ No keyframe shortcut: the size table's keyframe bit is set on no retail frame, and a
        // frame that is intra-coded still inherits the PALETTE of the frames before it, so the
        // only safe seek is a replay from frame 0.
        while (_nextFrame <= index)
        {
            DecodeNext();
        }
    }

    /// <summary>Decodes the next frame in sequence.</summary>
    internal void DecodeNext()
    {
        var index = _nextFrame;
        var layout = _file.GetFrameLayout(index);
        if (layout.Palette.IsPresent)
        {
            DecodePalette(_file.Bytes, layout.Palette.Offset, layout.Palette.Length, _palette);
        }

        DecodeBlocks(layout.Video);
        _nextFrame = index + 1;
    }

    /// <summary>
    ///     Applies one palette chunk (from its length byte) to <paramref name="palette" />, which
    ///     holds the PREVIOUS palette on entry. Per FUN_000ce270: a byte with bit 7 set keeps
    ///     <c>(b &amp; 0x7F) + 1</c> entries from the previous palette at the current index; a
    ///     byte with bit 6 set copies <c>(b &amp; 0x3F) + 1</c> entries from the previous palette
    ///     starting at the index in the NEXT byte; otherwise three bytes of 6-bit R, G, B follow,
    ///     each expanded through <see cref="PaletteMap" />. The walk stops once 256 entries are set.
    /// </summary>
    internal static void DecodePalette(byte[] bytes, int offset, int length, byte[] palette)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(palette);

        var previous = (byte[])palette.Clone();
        var end = offset + length;
        var p = offset + 1;
        var index = 0;
        while (index < 256)
        {
            if (p >= end)
            {
                throw new InvalidDataException("A Smacker palette chunk ended before 256 entries were set.");
            }

            var b = bytes[p];
            if ((b & 0x80) != 0)
            {
                var count = (b & 0x7F) + 1;
                CopyEntries(previous, index, palette, index, count);
                index += count;
                p += 1;
            }
            else if ((b & 0x40) != 0)
            {
                if (p + 1 >= end)
                {
                    throw new InvalidDataException("A Smacker palette copy run has no source index.");
                }

                var count = (b & 0x3F) + 1;
                CopyEntries(previous, bytes[p + 1], palette, index, count);
                index += count;
                p += 2;
            }
            else
            {
                if (p + 2 >= end)
                {
                    throw new InvalidDataException("A Smacker palette colour is truncated.");
                }

                var map = PaletteMap;
                palette[index * 3] = map[b & 0x3F];
                palette[index * 3 + 1] = map[bytes[p + 1] & 0x3F];
                palette[index * 3 + 2] = map[bytes[p + 2] & 0x3F];
                index += 1;
                p += 3;
            }
        }
    }

    private static void CopyEntries(byte[] source, int sourceIndex, byte[] target, int targetIndex, int count)
    {
        // The game copies 3 x count bytes with no bounds check; a run that would spill past entry
        // 255 is clamped so a hostile chunk cannot write outside the palette.
        var n = Math.Min(count, 256 - Math.Max(sourceIndex, targetIndex));
        if (n > 0)
        {
            Buffer.BlockCopy(source, sourceIndex * 3, target, targetIndex * 3, n * 3);
        }
    }

    private void DecodeBlocks(SmackerByteRange video)
    {
        var file = _file;
        var map = file.MapTree;
        var color = file.ColorTree;
        var full = file.FullTree;
        var type = file.TypeTree;
        map.ResetLastValues();
        color.ResetLastValues();
        full.ResetLastValues();
        type.ResetLastValues();
        Array.Clear(LastBlockCounts);

        var reader = new SmackerBitReader(file.Bytes, video.Offset, video.Length);
        var width = _stride;
        var canvas = _canvas;
        var blocksWide = _blocksWide;
        var total = blocksWide * _blocksHigh;
        var block = 0;
        var runs = RunLengths;

        while (block < total)
        {
            var t = type.Decode(reader);
            var kind = t & 3;
            int run = runs[(t >> 2) & 0x3F];
            LastBlockCounts[kind] += run;

            switch (kind)
            {
                case BlockVoid:
                    block += run;
                    break;

                case BlockSolid:
                {
                    var c = (byte)(t >> 8);
                    for (; run > 0 && block < total; run--, block++)
                    {
                        var o = BlockOffset(block, blocksWide, width);
                        for (var r = 0; r < 4; r++, o += width)
                        {
                            canvas[o] = c;
                            canvas[o + 1] = c;
                            canvas[o + 2] = c;
                            canvas[o + 3] = c;
                        }
                    }

                    break;
                }

                case BlockMono:
                    for (; run > 0 && block < total; run--, block++)
                    {
                        // MCLR: low byte = colour for a 0 bit, high byte = colour for a 1 bit.
                        // MMAP: bit 4r + x selects row r, pixel x (byte 0 rows 0-1, byte 1 rows 2-3).
                        var c = color.Decode(reader);
                        var m = map.Decode(reader);
                        var c0 = (byte)c;
                        var c1 = (byte)(c >> 8);
                        var o = BlockOffset(block, blocksWide, width);
                        for (var r = 0; r < 4; r++, o += width, m >>= 4)
                        {
                            canvas[o] = (m & 1) != 0 ? c1 : c0;
                            canvas[o + 1] = (m & 2) != 0 ? c1 : c0;
                            canvas[o + 2] = (m & 4) != 0 ? c1 : c0;
                            canvas[o + 3] = (m & 8) != 0 ? c1 : c0;
                        }
                    }

                    break;

                default:
                    for (; run > 0 && block < total; run--, block++)
                    {
                        // Per row, two FULL values: the FIRST is the right pair (pixels 2, 3), the
                        // SECOND the left pair (pixels 0, 1); low byte = left pixel of the pair.
                        var o = BlockOffset(block, blocksWide, width);
                        for (var r = 0; r < 4; r++, o += width)
                        {
                            var right = full.Decode(reader);
                            var left = full.Decode(reader);
                            canvas[o] = (byte)left;
                            canvas[o + 1] = (byte)(left >> 8);
                            canvas[o + 2] = (byte)right;
                            canvas[o + 3] = (byte)(right >> 8);
                        }
                    }

                    break;
            }
        }

        LastVideoBitsUsed = reader.BitPosition;
    }

    private static int BlockOffset(int block, int blocksWide, int width)
    {
        return (block / blocksWide) * 4 * width + (block % blocksWide) * 4;
    }
}
