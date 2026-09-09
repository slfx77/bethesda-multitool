// ORIGINAL CLEAN-ROOM TESTS for the ORIGINAL CLEAN-ROOM Smacker decoder under
// src/BethesdaMultitool/Core/Formats/Smacker/. Every expectation here comes from our own reverse
// engineering of RAD Game Tools' Smacker decoder as statically linked into Redguard's RG.EXE
// (3.2b) and Battlespire's GAME.EXE (3.0k), as written up in the specification document
//   scratchpad .../gap2/smacker/SPEC.md  ("Smacker Video (SMK2) - Format Specification"),
// or from an independent measurement of the retail bitstreams.
//
// NO FFmpeg- or libav-derived code, and no other third-party Smacker implementation, was consulted,
// read, copied or paraphrased. ffmpeg was used only as a sealed black box, run as an executable to
// compare output pixels and samples.

using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Smacker;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Smacker;

/// <summary>
///     Synthetic checks over the clean-room Smacker decoder: the bit reader, the two tree
///     codings with their escape slots, the palette chunk, the audio DPCM, the container probe,
///     and a complete two-block movie built here BY HAND from the specification rather than
///     captured from the decoder's own output.
///     <para>
///         The hand-built movie is the load-bearing test: its bits are laid down field by field
///         from SPEC.md sections 1-5 by a writer that lives in this file, and its pixels are
///         pinned from the same document's block semantics (MCLR low byte for a 0 map bit, MMAP
///         bit 4r+x for row r pixel x, a TYPE value's high byte as the solid colour).
///     </para>
/// </summary>
public sealed class SmackerDecoderTests
{
    /// <summary>LSB-first bit writer, the inverse of the reader under test — written from the SPEC, not from the reader.</summary>
    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = [];
        private int _bit;

        public int BitCount { get; private set; }

        public void Write(uint value, int count)
        {
            for (var i = 0; i < count; i++)
            {
                if (_bit == 0)
                {
                    _bytes.Add(0);
                }

                if (((value >> i) & 1) != 0)
                {
                    _bytes[^1] |= (byte)(1 << _bit);
                }

                _bit = (_bit + 1) & 7;
                BitCount++;
            }
        }

        /// <summary>An 8-bit tree with one leaf: [1 present][0 leaf][8 bits][0 end].</summary>
        public void Tree8Leaf(byte value)
        {
            Write(1, 1);
            Write(0, 1);
            Write(value, 8);
            Write(0, 1);
        }

        /// <summary>An 8-bit tree with two leaves: [1][1 branch][0 a][0 b][0 end].</summary>
        public void Tree8Pair(byte left, byte right)
        {
            Write(1, 1);
            Write(1, 1);
            Write(0, 1);
            Write(left, 8);
            Write(0, 1);
            Write(right, 8);
            Write(0, 1);
        }

        public byte[] ToArray(int padToMultipleOf = 1)
        {
            var bytes = _bytes.ToArray();
            var length = (bytes.Length + padToMultipleOf - 1) / padToMultipleOf * padToMultipleOf;
            Array.Resize(ref bytes, length);
            return bytes;
        }
    }

    [Fact]
    public void BitReaderReturnsBitsLeastSignificantFirst()
    {
        // 0xE5 = 1110 0101; read LSB-first the bits are 1,0,1,0,0,1,1,1.
        var reader = new SmackerBitReader([0xE5, 0x1A], 0, 2);

        Assert.Equal(5u, reader.Read(3));
        Assert.Equal(28u, reader.Read(5));
        Assert.Equal(0x1Au, reader.Read(8));
        Assert.Equal(16, reader.BitPosition);
        Assert.True(reader.IsExhausted);
        Assert.Equal(0, reader.ReadBit());
    }

    [Fact]
    public void EightBitTreeDecodesItsPreOrderShape()
    {
        // [1][1][0 0x2A][1][0 0x0B][0 0x0C][0]: root branch, left leaf 0x2A, right branch (0x0B, 0x0C).
        var w = new BitWriter();
        w.Write(1, 1);
        w.Write(1, 1);
        w.Write(0, 1);
        w.Write(0x2A, 8);
        w.Write(1, 1);
        w.Write(0, 1);
        w.Write(0x0B, 8);
        w.Write(0, 1);
        w.Write(0x0C, 8);
        w.Write(0, 1);
        // Then the symbols: 0 -> 0x2A, 1 0 -> 0x0B, 1 1 -> 0x0C.
        w.Write(0b11_01_0, 5); // LSB-first: 0, then 1 0, then 1 1.
        var bytes = w.ToArray();

        var reader = new SmackerBitReader(bytes, 0, bytes.Length);
        var tree = SmackerTree8.Read(reader);

        Assert.Equal(3, tree.LeafCount);
        Assert.Equal(0x2A, tree.Decode(reader));
        Assert.Equal(0x0B, tree.Decode(reader));
        Assert.Equal(0x0C, tree.Decode(reader));
    }

    [Fact]
    public void AbsentTreeDecodesZeroWithoutConsumingBits()
    {
        var reader = new SmackerBitReader([0xFE], 0, 1);
        var tree = SmackerTree8.Read(reader);

        Assert.True(tree.IsEmpty);
        Assert.Equal(1, reader.BitPosition);
        Assert.Equal(0, tree.Decode(reader));
        Assert.Equal(1, reader.BitPosition);
    }

    [Fact]
    public void SixteenBitTreeResolvesEscapeLeavesToLastValuesAndResetsPerFrame()
    {
        // Low tree {0x02, 0x10}, high tree {0x01}: leaf values 0x0102 and 0x0110. Escape code 0
        // is 0x0110, so the second leaf is a "last value 0" slot, not the value 0x0110.
        var w = new BitWriter();
        w.Write(1, 1); // present
        w.Tree8Pair(0x02, 0x10);
        w.Tree8Leaf(0x01);
        w.Write(0x0110, 16);
        w.Write(0xFFFE, 16);
        w.Write(0xFFFD, 16);
        w.Write(1, 1); // root branch
        w.Write(0, 1); // leaf: low bit 0 -> 0x02
        w.Write(0, 1);
        w.Write(0, 1); // leaf: low bit 1 -> 0x10
        w.Write(1, 1);
        w.Write(0, 1); // end
        // Symbols: 0 (0x0102), 1 (escape -> last0 = 0x0102), then after a reset 1 (escape -> 0).
        w.Write(0b1_1_0, 3);
        var bytes = w.ToArray();

        var reader = new SmackerBitReader(bytes, 0, bytes.Length);
        var tree = SmackerTree16.Read(reader);

        Assert.Equal(2, tree.LeafCount);
        Assert.Equal(1, tree.EscapeLeafCount);
        Assert.Equal([0x0110, 0xFFFE, 0xFFFD], tree.EscapeCodes);
        tree.ResetLastValues();
        Assert.Equal(0x0102, tree.Decode(reader));
        Assert.Equal(0x0102, tree.Decode(reader));
        tree.ResetLastValues();
        Assert.Equal(0, tree.Decode(reader));
    }

    [Fact]
    public void PaletteChunkExpandsSixBitColoursAndCopiesRunsFromThePreviousPalette()
    {
        // Previous palette: entry i = (i, i, i). Chunk: entry 0 explicit (63, 0, 1); keep 2 (0x81);
        // copy 3 from previous entry 10 (0x42, 10); then keep the remaining 250 (0xFF = 128, 0xF9 = 122).
        var previous = new byte[768];
        for (var i = 0; i < 256; i++)
        {
            previous[i * 3] = previous[i * 3 + 1] = previous[i * 3 + 2] = (byte)i;
        }

        byte[] chunk = [3, 63, 0, 1, 0x81, 0x42, 10, 0xFF, 0xF9, 0, 0, 0];
        var palette = (byte[])previous.Clone();

        SmackerVideoDecoder.DecodePalette(chunk, 0, chunk.Length, palette);

        Assert.Equal([0xFF, 0x00, 0x04], palette[..3]);
        Assert.Equal([1, 1, 1, 2, 2, 2], palette[3..9]);
        Assert.Equal([10, 10, 10, 11, 11, 11, 12, 12, 12], palette[9..18]);
        Assert.Equal([6, 6, 6], palette[18..21]);
        Assert.Equal([255, 255, 255], palette[765..768]);
    }

    [Fact]
    public void PaletteMapIsTheSixtyFourEntryRampFromTheExecutable()
    {
        // Dumped from RG.EXE flat 0x3accc5 and GAME.EXE flat 0xf5891: four runs of sixteen values
        // stepping by 4 from 0x00, 0x41, 0x82, 0xC3 — NOT value << 2 (which would end at 0xFC).
        var map = SmackerVideoDecoder.PaletteMap;

        Assert.Equal(64, map.Length);
        Assert.Equal(0x00, map[0]);
        Assert.Equal(0x3C, map[15]);
        Assert.Equal(0x41, map[16]);
        Assert.Equal(0x82, map[32]);
        Assert.Equal(0xC3, map[48]);
        Assert.Equal(0xFF, map[63]);
    }

    [Fact]
    public void RunLengthTableIsOneToFiftyNineThenPowersOfTwo()
    {
        var runs = SmackerVideoDecoder.RunLengths;

        Assert.Equal(64, runs.Length);
        Assert.Equal(1, runs[0]);
        Assert.Equal(59, runs[58]);
        Assert.Equal(128, runs[59]);
        Assert.Equal(2048, runs[63]);
    }

    [Fact]
    public void EightBitMonoAudioChunkIsBaseSamplePlusHuffmanDeltas()
    {
        // Tree {+1, -1 (0xFF)}; base 0x80; deltas 0, 1, 0 -> 0x80, 0x81, 0x80, 0x81.
        var w = new BitWriter();
        w.Write(1, 1); // data present
        w.Write(0, 1); // mono
        w.Write(0, 1); // 8-bit
        w.Tree8Pair(0x01, 0xFF);
        w.Write(0x80, 8);
        w.Write(0b0_1_0, 3);
        var bits = w.ToArray();
        var chunk = new byte[8 + bits.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(chunk, (uint)chunk.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4), 4);
        bits.CopyTo(chunk, 8);

        var decoded = SmackerAudioDecoder.Decode(chunk, new SmackerByteRange(0, chunk.Length));

        Assert.False(decoded.IsStereo);
        Assert.False(decoded.Is16Bit);
        Assert.Equal([0x80, 0x81, 0x80, 0x81], decoded.Pcm);
    }

    [Fact]
    public void SixteenBitStereoBaseIsRightThenLeftHighByteFirst()
    {
        // Four single-leaf trees (delta 0 everywhere); base bytes R.hi R.lo L.hi L.lo = 12 34 56 78;
        // two sample frames -> L 0x5678, R 0x1234 twice, little-endian and left first.
        var w = new BitWriter();
        w.Write(1, 1);
        w.Write(1, 1); // stereo
        w.Write(1, 1); // 16-bit
        for (var i = 0; i < 4; i++)
        {
            w.Tree8Leaf(0);
        }

        w.Write(0x12, 8);
        w.Write(0x34, 8);
        w.Write(0x56, 8);
        w.Write(0x78, 8);
        var bits = w.ToArray();
        var chunk = new byte[8 + bits.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(chunk, (uint)chunk.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4), 8);
        bits.CopyTo(chunk, 8);

        var decoded = SmackerAudioDecoder.Decode(chunk, new SmackerByteRange(0, chunk.Length));

        Assert.True(decoded.IsStereo);
        Assert.True(decoded.Is16Bit);
        Assert.Equal([0x78, 0x56, 0x34, 0x12, 0x78, 0x56, 0x34, 0x12], decoded.Pcm);
    }

    [Fact]
    public void ChunkWithoutDataDecodesToNothing()
    {
        byte[] chunk = [9, 0, 0, 0, 4, 0, 0, 0, 0x00];

        var decoded = SmackerAudioDecoder.Decode(chunk, new SmackerByteRange(0, chunk.Length));

        Assert.Empty(decoded.Pcm);
    }

    /// <summary>
    ///     An 8x4 SMK2 with two 4x4 blocks: block 0 solid colour 5, block 1 mono with MCLR 0x0201
    ///     and MMAP 0x5A3C. MMAP and MCLR trees are single-leaf (they read no bits), FULL is absent,
    ///     TYPE has two leaves selected by one bit each.
    /// </summary>
    private static byte[] BuildTwoBlockMovie(out byte[] treeBytes, out byte[] videoBytes)
    {
        var trees = new BitWriter();
        // MMAP: value 0x5A3C.
        trees.Write(1, 1);
        trees.Tree8Leaf(0x3C);
        trees.Tree8Leaf(0x5A);
        trees.Write(0xFFFF, 16);
        trees.Write(0xFFFE, 16);
        trees.Write(0xFFFD, 16);
        trees.Write(0, 1); // single leaf, reads nothing from either 8-bit tree
        trees.Write(0, 1); // end
        // MCLR: value 0x0201 (colour 1 for a 0 bit, colour 2 for a 1 bit).
        trees.Write(1, 1);
        trees.Tree8Leaf(0x01);
        trees.Tree8Leaf(0x02);
        trees.Write(0xFFFF, 16);
        trees.Write(0xFFFE, 16);
        trees.Write(0xFFFD, 16);
        trees.Write(0, 1);
        trees.Write(0, 1);
        // FULL: absent.
        trees.Write(0, 1);
        // TYPE: low {0x03, 0x00}, high {0x05}: values 0x0503 (solid, colour 5, run 1) and
        // 0x0500 (mono, run 1) selected by the big tree's one branch bit.
        trees.Write(1, 1);
        trees.Tree8Pair(0x03, 0x00);
        trees.Tree8Leaf(0x05);
        trees.Write(0xFFFF, 16);
        trees.Write(0xFFFE, 16);
        trees.Write(0xFFFD, 16);
        trees.Write(1, 1); // branch
        trees.Write(0, 1); // leaf; low tree bit:
        trees.Write(0, 1); //   0 -> 0x03
        trees.Write(0, 1); // leaf; low tree bit:
        trees.Write(1, 1); //   1 -> 0x00
        trees.Write(0, 1); // end
        treeBytes = trees.ToArray();

        // Six explicit entries include the solid block's blue index 5. Keep the remaining
        // 128 + 122 = 250 entries from the previous palette; 21 bytes pad to six units of four.
        byte[] palette =
        [
            6,
            0, 0, 0, // index 0: black
            63, 0, 0, // index 1: red
            0, 63, 0, // index 2: green
            0, 0, 0, // index 3: black
            0, 0, 0, // index 4: black
            0, 0, 63, // index 5: blue
            0xFF, 0xF9,
            0, 0, 0
        ];
        Assert.Equal(24, palette.Length);

        var video = new BitWriter();
        video.Write(0, 1); // TYPE -> 0x0503: solid colour 5
        video.Write(1, 1); // TYPE -> 0x0500: mono (MCLR, MMAP read no bits)
        videoBytes = video.ToArray(4);

        var frame = palette.Concat(videoBytes).ToArray();
        var header = new byte[104];
        "SMK2"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 4);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), -6666);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(52), (uint)treeBytes.Length);
        var table = new byte[5];
        BinaryPrimitives.WriteUInt32LittleEndian(table, (uint)frame.Length);
        table[4] = 1; // palette chunk present
        return header.Concat(table).Concat(treeBytes).Concat(frame).ToArray();
    }

    [Fact]
    public void HandBuiltTwoBlockMovieDecodesToItsPinnedPixels()
    {
        var bytes = BuildTwoBlockMovie(out var treeBytes, out var videoBytes);

        Assert.True(SmackerFile.IsSmacker(bytes));
        var file = SmackerFile.Parse(bytes, "two-block.smk");

        Assert.Equal("SMK2", file.Magic);
        Assert.Equal(8, file.Width);
        Assert.Equal(4, file.Height);
        Assert.Equal(4, file.DisplayHeight);
        Assert.Equal(1, file.FrameCount);
        Assert.Equal(100000.0 / 6666, file.FramesPerSecond, 9);
        Assert.Equal((uint)treeBytes.Length, file.TreesSize);
        Assert.True(file.FullTree.IsEmpty);
        Assert.Equal(2, file.TypeTree.LeafCount);
        Assert.True(file.HasPalette(0));
        Assert.False(file.HasAudio(0, 0));

        var decoder = new SmackerVideoDecoder(file);
        decoder.DecodeFrame(0);

        byte[] expected =
        [
            5, 5, 5, 5, 1, 1, 2, 2, // MMAP nibble 0xC = 1100 -> bits 2,3 set
            5, 5, 5, 5, 2, 2, 1, 1, //             0x3 = 0011
            5, 5, 5, 5, 1, 2, 1, 2, //             0xA = 1010
            5, 5, 5, 5, 2, 1, 2, 1 //              0x5 = 0101
        ];
        Assert.Equal(expected, decoder.GetFrameIndices());
        Assert.Equal(2, decoder.LastVideoBitsUsed);
        Assert.Equal([1, 0, 0, 1], decoder.LastBlockCounts);

        var palette = decoder.PaletteRgb;
        Assert.Equal([0, 0, 0], palette[..3].ToArray());
        Assert.Equal([0xFF, 0, 0], palette[3..6].ToArray());
        Assert.Equal([0, 0xFF, 0], palette[6..9].ToArray());
        Assert.Equal([0, 0, 0xFF], palette[15..18].ToArray());
        Assert.Equal(0, palette[18]);

        var clip = SmackerVideoClip.Open(file);
        var texture = clip.GetFrame(0);
        Assert.Equal(8, texture.Width);
        Assert.Equal(4, texture.Height);
        // Pixel (4, 0) is index 1 = red; pixel (0, 0) is index 5 = blue.
        Assert.Equal([0, 0, 0xFF, 0xFF], texture.Pixels[..4]);
        Assert.Equal([0xFF, 0, 0, 0xFF], texture.Pixels[16..20]);
        Assert.Equal(4, videoBytes.Length);
    }

    [Fact]
    public void ProbeRejectsAFileWhoseFramesDoNotTile()
    {
        var bytes = BuildTwoBlockMovie(out _, out _);
        Assert.True(SmackerFile.IsSmacker(bytes));

        var tooShort = bytes[..^4];
        Assert.False(SmackerFile.IsSmacker(tooShort));

        var wrongMagic = (byte[])bytes.Clone();
        wrongMagic[3] = (byte)'3';
        Assert.False(SmackerFile.IsSmacker(wrongMagic));

        var biggerFrame = (byte[])bytes.Clone();
        var frameLength = BinaryPrimitives.ReadUInt32LittleEndian(biggerFrame.AsSpan(104));
        BinaryPrimitives.WriteUInt32LittleEndian(biggerFrame.AsSpan(104), frameLength + 4);
        Assert.False(SmackerFile.IsSmacker(biggerFrame));
        Assert.Throws<InvalidDataException>(() => SmackerFile.Parse(biggerFrame, "bad.smk"));
    }

    [Fact]
    public void YDoubledMovieReportsDoubleDisplayHeightAndRepeatsRows()
    {
        var bytes = BuildTwoBlockMovie(out _, out _);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), SmackerFile.FlagYDoubled);

        var file = SmackerFile.Parse(bytes, "doubled.smk");
        Assert.True(file.IsYDoubled);
        Assert.Equal(4, file.Height);
        Assert.Equal(8, file.DisplayHeight);

        var clip = SmackerVideoClip.Open(file);
        Assert.Equal(8, clip.Height);
        var texture = clip.GetFrame(0);
        Assert.Equal(8, texture.Height);
        // Rows 0 and 1 of the display are both stored row 0.
        Assert.Equal(texture.Pixels[..32], texture.Pixels[32..64]);
    }

    [Fact]
    public void AudioTrackDescriptorDecodesItsBits()
    {
        // Redguard's A_ruins1.smk track 0: compressed, present, stereo, 8-bit, 11,025 Hz.
        var track = new SmackerAudioTrack(0, 0xD0002B11, 23520);

        Assert.True(track.IsPresent);
        Assert.True(track.IsCompressed);
        Assert.True(track.IsStereo);
        Assert.False(track.Is16Bit);
        Assert.Equal(11025, track.SampleRate);
        Assert.Equal(2, track.Channels);
        Assert.Equal(8, track.BitsPerSample);

        Assert.False(new SmackerAudioTrack(1, 0, 0).IsPresent);
    }
}
