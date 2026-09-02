using BethesdaMultitool.Core.Formats.Arena;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Arena;

/// <summary>
///     Hand-built .CIF vectors for <see cref="ArenaCifDecoder" />: per-frame headers plus
///     independently pinned pixel bytes, including the reference's dispatch-by-first-frame
///     rule and the headerless tile table.
/// </summary>
public class ArenaCifDecoderTests
{
    [Fact]
    public void Decode_TwoUncompressedFrames_DistinctSizesAndOffsets()
    {
        var bytes = FrameHeader(xOffset: 5, yOffset: 6, width: 2, height: 1, flags: 0x0000, dataLength: 2);
        bytes.AddRange(new byte[] { 1, 2 });
        bytes.AddRange(FrameHeader(xOffset: 9, yOffset: 4, width: 1, height: 3, flags: 0x0000, dataLength: 3));
        bytes.AddRange(new byte[] { 7, 8, 9 });

        var frames = ArenaCifDecoder.Decode(bytes.ToArray(), "TEST.CIF");

        Assert.Equal(2, frames.Count);

        Assert.Equal(2, frames[0].Width);
        Assert.Equal(1, frames[0].Height);
        Assert.Equal(5, frames[0].XOffset);
        Assert.Equal(6, frames[0].YOffset);
        Assert.Equal(new byte[] { 1, 2 }, frames[0].Indices);

        Assert.Equal(1, frames[1].Width);
        Assert.Equal(3, frames[1].Height);
        Assert.Equal(9, frames[1].XOffset);
        Assert.Equal(4, frames[1].YOffset);
        Assert.Equal(new byte[] { 7, 8, 9 }, frames[1].Indices);
    }

    [Fact]
    public void Decode_RleFrames_FirstFrameFlagsDispatchEveryFrame()
    {
        // Frame 1: run packet 0x83 -> 4 repeats of 0xAA. Frame 2's header deliberately says
        // flags 0x0004, but the reference dispatches on the FIRST frame's flags only, so its
        // payload (an RLE literal packet) must still decode via RLE - fed to LZSS instead it
        // would throw on a truncated back-reference, so this vector discriminates.
        var bytes = FrameHeader(0, 0, width: 2, height: 2, flags: 0x0002, dataLength: 2);
        bytes.AddRange(new byte[] { 0x83, 0xAA });
        bytes.AddRange(FrameHeader(xOffset: 3, yOffset: 1, width: 2, height: 1, flags: 0x0004, dataLength: 3));
        bytes.AddRange(new byte[] { 0x01, 0x11, 0x22 }); // literal packet: 2 verbatim bytes

        var frames = ArenaCifDecoder.Decode(bytes.ToArray(), "WEAPON.CIF");

        Assert.Equal(2, frames.Count);
        Assert.Equal(new byte[] { 0xAA, 0xAA, 0xAA, 0xAA }, frames[0].Indices);
        Assert.Equal(new byte[] { 0x11, 0x22 }, frames[1].Indices);
        Assert.Equal(3, frames[1].XOffset);
        Assert.Equal(1, frames[1].YOffset);
    }

    [Fact]
    public void Decode_HeaderlessTableEntry_NineRawFrames()
    {
        // MARBLE.CIF: 9 frames of 3x3 in the hardcoded table; lookup is case-insensitive.
        var bytes = new byte[9 * 9];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)i;
        }

        var frames = ArenaCifDecoder.Decode(bytes, "marble.cif");

        Assert.Equal(9, frames.Count);
        Assert.All(frames, frame =>
        {
            Assert.Equal(3, frame.Width);
            Assert.Equal(3, frame.Height);
            Assert.Equal(0, frame.XOffset);
            Assert.Equal(0, frame.YOffset);
        });
        Assert.Equal(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 }, frames[0].Indices);
        Assert.Equal(new byte[] { 72, 73, 74, 75, 76, 77, 78, 79, 80 }, frames[8].Indices);
    }

    [Fact]
    public void Decode_TrailingBytesAfterLastFrame_Throws()
    {
        // EOF-terminated iteration: 6 stray bytes after a complete frame cannot form the
        // next 12-byte header.
        var bytes = FrameHeader(0, 0, width: 2, height: 1, flags: 0x0000, dataLength: 2);
        bytes.AddRange(new byte[] { 5, 6 });
        bytes.AddRange(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02 });

        Assert.Throws<InvalidDataException>(() => ArenaCifDecoder.Decode(bytes.ToArray(), "GARBAGE.CIF"));
    }

    [Fact]
    public void Decode_TruncatedFrameData_Throws()
    {
        var bytes = FrameHeader(0, 0, width: 4, height: 4, flags: 0x0004, dataLength: 50);
        bytes.AddRange(new byte[] { 0x0F, 0x10, 0x20, 0x30, 0x40 }); // 50 bytes declared, 5 present

        Assert.Throws<InvalidDataException>(() => ArenaCifDecoder.Decode(bytes.ToArray(), "SHORT.CIF"));
    }

    [Fact]
    public void Decode_UncompressedFrameLengthExceedsPixels_Throws()
    {
        // dataLength 5 for a 1x1 frame: the reference would overrun its pixel buffer.
        var bytes = FrameHeader(0, 0, width: 1, height: 1, flags: 0x0000, dataLength: 5);
        bytes.AddRange(new byte[] { 1, 2, 3, 4, 5 });

        Assert.Throws<InvalidDataException>(() => ArenaCifDecoder.Decode(bytes.ToArray(), "OVER.CIF"));
    }

    [Fact]
    public void Decode_UnrecognizedFlags_Throws()
    {
        var bytes = FrameHeader(0, 0, width: 1, height: 1, flags: 0x0007, dataLength: 1);
        bytes.Add(0x00);

        Assert.Throws<InvalidDataException>(() => ArenaCifDecoder.Decode(bytes.ToArray(), "BAD.CIF"));
    }

    [Fact]
    public void Decode_EmptyFile_Throws()
    {
        Assert.Throws<InvalidDataException>(() => ArenaCifDecoder.Decode([], "EMPTY.CIF"));
    }

    [Fact]
    public void Decode_TruncatedHeaderlessTableEntry_Throws()
    {
        // MARBLE.CIF needs 9 * 3 * 3 = 81 bytes.
        Assert.Throws<InvalidDataException>(() => ArenaCifDecoder.Decode(new byte[50], "MARBLE.CIF"));
    }

    private static List<byte> FrameHeader(
        ushort xOffset, ushort yOffset, ushort width, ushort height, ushort flags, ushort dataLength)
    {
        var bytes = new List<byte>();
        AddUInt16(bytes, xOffset);
        AddUInt16(bytes, yOffset);
        AddUInt16(bytes, width);
        AddUInt16(bytes, height);
        AddUInt16(bytes, flags);
        AddUInt16(bytes, dataLength);
        return bytes;
    }

    private static void AddUInt16(List<byte> bytes, ushort value)
    {
        bytes.Add((byte)(value & 0xFF));
        bytes.Add((byte)(value >> 8));
    }
}
