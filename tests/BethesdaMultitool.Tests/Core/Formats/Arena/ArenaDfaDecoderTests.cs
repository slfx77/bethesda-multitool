using BethesdaMultitool.Core.Formats.Arena;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Arena;

/// <summary>
///     Hand-built .DFA vectors for <see cref="ArenaDfaDecoder" />: an RLE first frame plus
///     pinned delta chunks, verifying that every later frame patches its own copy of frame 1
///     (deltas are per-frame, not cumulative, and frame 1 is never aliased or mutated).
/// </summary>
public class ArenaDfaDecoderTests
{
    [Fact]
    public void Decode_ThreeFrames_DeltasApplyPerFrameAgainstFrameOne()
    {
        // 3x2 frames. Frame 1 RLE: literal packet control 0x05 -> 6 verbatim bytes (7
        // compressed bytes total). Frame 2 patches pixels 2..3; frame 3 patches pixels 0 and
        // 5 - and must show frame 1's values at pixels 2..3, proving deltas never chain off
        // frame 2. Both chunk-group size fields are deliberately bogus: the reference reads
        // and ignores them.
        var bytes = DfaHeader(imageCount: 3, width: 3, height: 2, firstFrameCompressedLength: 7);
        bytes.AddRange(new byte[] { 0x05, 1, 2, 3, 4, 5, 6 });

        // Frame 2 chunk group: one update.
        AddUInt16(bytes, 0x7777); // chunk-group size, unused
        AddUInt16(bytes, 1); //      chunk count
        AddUInt16(bytes, 2); //      update pixel offset
        AddUInt16(bytes, 2); //      update byte count
        bytes.AddRange(new byte[] { 0xAA, 0xBB });

        // Frame 3 chunk group: two updates.
        AddUInt16(bytes, 0x1234); // chunk-group size, unused
        AddUInt16(bytes, 2);
        AddUInt16(bytes, 0);
        AddUInt16(bytes, 1);
        bytes.Add(0xCC);
        AddUInt16(bytes, 5);
        AddUInt16(bytes, 1);
        bytes.Add(0xDD);

        var frames = ArenaDfaDecoder.Decode(bytes.ToArray());

        Assert.Equal(3, frames.Count);
        Assert.All(frames, frame =>
        {
            Assert.Equal(3, frame.Width);
            Assert.Equal(2, frame.Height);
            Assert.Equal(0, frame.XOffset);
            Assert.Equal(0, frame.YOffset);
        });

        // Frame 1 must be unchanged after the later frames' deltas were applied.
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, frames[0].Indices);
        Assert.Equal(new byte[] { 1, 2, 0xAA, 0xBB, 5, 6 }, frames[1].Indices);
        Assert.Equal(new byte[] { 0xCC, 2, 3, 4, 5, 0xDD }, frames[2].Indices);

        // No aliasing: every frame owns its pixel buffer.
        Assert.NotSame(frames[0].Indices, frames[1].Indices);
        Assert.NotSame(frames[0].Indices, frames[2].Indices);
        Assert.NotSame(frames[1].Indices, frames[2].Indices);
    }

    [Fact]
    public void Decode_SingleFrame_NoChunkDataNeeded()
    {
        var bytes = DfaHeader(imageCount: 1, width: 2, height: 2, firstFrameCompressedLength: 5);
        bytes.AddRange(new byte[] { 0x03, 9, 8, 7, 6 }); // literal packet: 4 verbatim bytes

        var frames = ArenaDfaDecoder.Decode(bytes.ToArray());

        Assert.Single(frames);
        Assert.Equal(new byte[] { 9, 8, 7, 6 }, frames[0].Indices);
    }

    [Fact]
    public void Decode_ZeroImages_Throws()
    {
        var bytes = DfaHeader(imageCount: 0, width: 1, height: 1, firstFrameCompressedLength: 0);

        Assert.Throws<InvalidDataException>(() => ArenaDfaDecoder.Decode(bytes.ToArray()));
    }

    [Fact]
    public void Decode_HeaderTooSmall_Throws()
    {
        Assert.Throws<InvalidDataException>(() => ArenaDfaDecoder.Decode(new byte[5]));
    }

    [Fact]
    public void Decode_TruncatedFirstFrameRle_Throws()
    {
        // Literal packet declares 6 bytes but only 1 follows.
        var bytes = DfaHeader(imageCount: 1, width: 3, height: 2, firstFrameCompressedLength: 7);
        bytes.AddRange(new byte[] { 0x05, 1 });

        Assert.Throws<InvalidDataException>(() => ArenaDfaDecoder.Decode(bytes.ToArray()));
    }

    [Fact]
    public void Decode_TruncatedChunkGroupHeader_Throws()
    {
        var bytes = DfaHeader(imageCount: 2, width: 3, height: 2, firstFrameCompressedLength: 7);
        bytes.AddRange(new byte[] { 0x05, 1, 2, 3, 4, 5, 6 });
        bytes.AddRange(new byte[] { 0x01, 0x00 }); // 4-byte chunk-group header owed

        Assert.Throws<InvalidDataException>(() => ArenaDfaDecoder.Decode(bytes.ToArray()));
    }

    [Fact]
    public void Decode_TruncatedUpdateData_Throws()
    {
        var bytes = DfaHeader(imageCount: 2, width: 3, height: 2, firstFrameCompressedLength: 7);
        bytes.AddRange(new byte[] { 0x05, 1, 2, 3, 4, 5, 6 });
        AddUInt16(bytes, 0);
        AddUInt16(bytes, 1); // one update
        AddUInt16(bytes, 0); // pixel offset
        AddUInt16(bytes, 4); // 4 bytes declared...
        bytes.Add(0xAA); //     ...1 present

        Assert.Throws<InvalidDataException>(() => ArenaDfaDecoder.Decode(bytes.ToArray()));
    }

    [Fact]
    public void Decode_UpdateWritesPastFrame_Throws()
    {
        // Update at pixel offset 5, count 3, in a 6-pixel frame: pixels 5..7 overrun.
        var bytes = DfaHeader(imageCount: 2, width: 3, height: 2, firstFrameCompressedLength: 7);
        bytes.AddRange(new byte[] { 0x05, 1, 2, 3, 4, 5, 6 });
        AddUInt16(bytes, 0);
        AddUInt16(bytes, 1);
        AddUInt16(bytes, 5);
        AddUInt16(bytes, 3);
        bytes.AddRange(new byte[] { 0xAA, 0xBB, 0xCC });

        Assert.Throws<InvalidDataException>(() => ArenaDfaDecoder.Decode(bytes.ToArray()));
    }

    private static List<byte> DfaHeader(ushort imageCount, ushort width, ushort height, ushort firstFrameCompressedLength)
    {
        var bytes = new List<byte>();
        AddUInt16(bytes, imageCount);
        AddUInt16(bytes, 0); // unknown1
        AddUInt16(bytes, 0); // unknown2
        AddUInt16(bytes, width);
        AddUInt16(bytes, height);
        AddUInt16(bytes, firstFrameCompressedLength);
        return bytes;
    }

    private static void AddUInt16(List<byte> bytes, ushort value)
    {
        bytes.Add((byte)(value & 0xFF));
        bytes.Add((byte)(value >> 8));
    }
}
