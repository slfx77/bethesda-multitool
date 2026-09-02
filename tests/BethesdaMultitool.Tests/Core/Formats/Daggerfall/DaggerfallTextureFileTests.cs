using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BethesdaMultitool.Core.Formats.Daggerfall;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Daggerfall;

/// <summary>
///     Hand-built vectors for <see cref="DaggerfallTextureFile" />, one per storage form. Shapes
///     follow the retail corpus (469 decodable files, 6,713 records, surveyed 2026-09-02) —
///     notably the fixed 256-byte row stride of single-frame records, which is the format's most
///     famous trap.
/// </summary>
public class DaggerfallTextureFileTests
{
    private const int HeaderLength = 26;
    private const int RecordHeaderLength = 20;

    private static void WriteI16(List<byte> to, int value)
    {
        to.Add((byte)(value & 0xFF));
        to.Add((byte)((value >> 8) & 0xFF));
    }

    private static void WriteI32(List<byte> to, int value)
    {
        WriteI16(to, value & 0xFFFF);
        WriteI16(to, (value >> 16) & 0xFFFF);
    }

    /// <summary>
    ///     Builds a one-record TEXTURE file. The record descriptor sits right after the record
    ///     header; its pixel data follows at <paramref name="dataOffset" /> from the descriptor.
    /// </summary>
    private static byte[] BuildSingleRecord(
        int width,
        int height,
        ushort compression,
        int frameCount,
        byte[] data,
        int dataOffset = 28,
        int offsetX = 5,
        int offsetY = 7)
    {
        var file = new List<byte>();
        WriteI16(file, 1);
        file.AddRange(Encoding.ASCII.GetBytes("Test Set".PadRight(24, '\0')));

        var recordPosition = HeaderLength + RecordHeaderLength;
        WriteI16(file, 0); // type1
        WriteI32(file, recordPosition);
        WriteI16(file, 0); // type2
        WriteI32(file, 0); // unknown
        file.AddRange(new byte[8]); // null

        WriteI16(file, offsetX);
        WriteI16(file, offsetY);
        WriteI16(file, width);
        WriteI16(file, height);
        WriteI16(file, (short)compression);
        WriteI32(file, data.Length); // record size (unused by the decoder)
        WriteI32(file, dataOffset);
        WriteI16(file, 0); // isNormal
        WriteI16(file, frameCount);
        WriteI16(file, 0); // unknown
        WriteI16(file, 0); // scaleX
        WriteI16(file, 0); // scaleY

        while (file.Count < recordPosition + dataOffset)
        {
            file.Add(0);
        }

        file.AddRange(data);
        return [.. file];
    }

    [Fact]
    public void Parse_ReadsTheSetNameFromTheHeader()
    {
        var file = BuildSingleRecord(2, 1, 0, 1, BuildStridedRows(2, [[1, 2]]));

        Assert.Equal("Test Set", DaggerfallTextureFile.Parse(file, "TEXTURE.010").SetName);
    }

    /// <summary>Rows padded to the fixed 256-byte stride, as single-frame records store them.</summary>
    private static byte[] BuildStridedRows(int width, byte[][] rows)
    {
        var data = new List<byte>();
        foreach (var row in rows)
        {
            data.AddRange(row);
            data.AddRange(new byte[DaggerfallTextureFile.UncompressedRowStride - width]);
        }

        return [.. data];
    }

    [Fact]
    public void Parse_SingleFrame_ReadsRowsOnTheFixed256ByteStride()
    {
        // Two 3-pixel rows, each padded to 256 bytes. A naive width-stride read would take the
        // second row's pixels from the padding and produce zeros.
        var file = BuildSingleRecord(3, 2, 0, 1, BuildStridedRows(3, [[10, 20, 30], [40, 50, 60]]));

        var record = Assert.Single(DaggerfallTextureFile.Parse(file, "TEXTURE.010").Records);
        var frame = Assert.Single(record.Frames);

        Assert.Equal([10, 20, 30, 40, 50, 60], frame.Indices);
        Assert.Equal(5, frame.XOffset);
        Assert.Equal(7, frame.YOffset);
    }

    [Fact]
    public void Parse_MultiFrame_DecodesTransparentAndLiteralRuns()
    {
        // Two 4x1 frames behind the i32 offset table. Frame 0's row: skip 1 (writes index 0),
        // 2 literals, then skip 1 + 0 literals to close the row — the sprite shape with a
        // transparent margin on both sides.
        byte[] RunFrame(params byte[] runs)
        {
            var f = new List<byte>();
            WriteI16(f, 4); // cx
            WriteI16(f, 1); // cy
            f.AddRange(runs);
            return [.. f];
        }

        var first = RunFrame(1, 2, 77, 88, 1, 0);
        var second = RunFrame(0, 4, 5, 6, 7, 8);
        var data = new List<byte>();
        WriteI32(data, 8);
        WriteI32(data, 8 + first.Length);
        data.AddRange(first);
        data.AddRange(second);

        var record = Assert.Single(
            DaggerfallTextureFile.Parse(BuildSingleRecord(4, 1, 0, 2, [.. data]), "TEXTURE.357").Records);

        Assert.Equal([0, 77, 88, 0], record.Frames[0].Indices);
        Assert.Equal([5, 6, 7, 8], record.Frames[1].Indices);
    }

    [Fact]
    public void Parse_MultiFrameWithTwoFrames_UsesTheOffsetTable()
    {
        byte[] FrameBytes(byte a, byte b)
        {
            var f = new List<byte>();
            WriteI16(f, 2);
            WriteI16(f, 1);
            f.AddRange([0, 2, a, b]);
            return [.. f];
        }

        var first = FrameBytes(1, 2);
        var second = FrameBytes(3, 4);
        var data = new List<byte>();
        WriteI32(data, 8); // frame 0 at +8 (after the two table entries)
        WriteI32(data, 8 + first.Length);
        data.AddRange(first);
        data.AddRange(second);

        var file = BuildSingleRecord(2, 1, 0, 2, [.. data]);

        var record = Assert.Single(DaggerfallTextureFile.Parse(file, "TEXTURE.357").Records);
        Assert.Equal(2, record.Frames.Count);
        Assert.Equal([1, 2], record.Frames[0].Indices);
        Assert.Equal([3, 4], record.Frames[1].Indices);
    }

    [Fact]
    public void Parse_RleRecord_DecodesRunAndRawRows()
    {
        // Two rows. Row 0 is RLE (flag 0x8000): rowWidth 4, probe -3 repeats 9, probe +1 copies 5.
        // Row 1 is raw: width bytes copied straight.
        const ushort compression = 0x1108;
        const int width = 4;
        const int height = 2;
        const int dataOffset = 28;

        var rowHeaders = new List<byte>();
        var rowData = new List<byte>();

        var rleRow = new List<byte>();
        WriteI16(rleRow, width); // rowWidth
        WriteI16(rleRow, -3);
        rleRow.Add(9);
        WriteI16(rleRow, 1);
        rleRow.Add(5);

        var rawRow = new byte[] { 6, 7, 8, 9 };

        // Row offsets are measured from the RECORD position, not the data offset.
        var rowsStart = dataOffset + (height * 4);
        WriteI16(rowHeaders, rowsStart);
        WriteI16(rowHeaders, unchecked((short)0x8000));
        WriteI16(rowHeaders, rowsStart + rleRow.Count);
        WriteI16(rowHeaders, 0);

        rowData.AddRange(rleRow);
        rowData.AddRange(rawRow);

        var file = BuildSingleRecord(width, height, compression, 1, [.. rowHeaders, .. rowData]);

        var frame = Assert.Single(Assert.Single(DaggerfallTextureFile.Parse(file, "TEXTURE.010").Records).Frames);
        Assert.Equal([9, 9, 9, 5, 6, 7, 8, 9], frame.Indices);
    }

    [Theory]
    [InlineData("TEXTURE.000", 0)]
    [InlineData("TEXTURE.001", 128)]
    public void Parse_SolidFiles_GenerateSwatchesInsteadOfReadingPixels(string name, int baseIndex)
    {
        // The descriptor's geometry is ignored for solids; every record is a 32x32 fill whose
        // colour is the record index (offset by 128 for .001).
        var file = BuildSingleRecord(999, 999, 0, 1, []);

        var record = Assert.Single(DaggerfallTextureFile.Parse(file, name).Records);
        var frame = Assert.Single(record.Frames);

        Assert.Equal(DaggerfallTextureFile.SolidSize, frame.Width);
        Assert.Equal(DaggerfallTextureFile.SolidSize, frame.Height);
        Assert.All(frame.Indices, i => Assert.Equal((byte)baseIndex, i));
    }

    [Theory]
    [InlineData("TEXTURE.215")]
    [InlineData("TEXTURE.217")]
    [InlineData("TEXTURE.436")]
    public void Parse_TheThreeMalformedRetailFiles_AreRefusedByName(string name)
    {
        Assert.True(DaggerfallTextureFile.IsUnsupported(name));
        Assert.Throws<NotSupportedException>(
            () => DaggerfallTextureFile.Parse(new byte[64], name));
    }

    [Fact]
    public void IsTextureFileName_MatchesTheConventionCaseInsensitively()
    {
        Assert.True(DaggerfallTextureFile.IsTextureFileName("TEXTURE.010"));
        Assert.True(DaggerfallTextureFile.IsTextureFileName("texture.357"));
        Assert.False(DaggerfallTextureFile.IsTextureFileName("KAMIRA.RCI"));
    }

    [Fact]
    public void Parse_TruncatedHeader_Throws()
    {
        Assert.Throws<InvalidDataException>(() => DaggerfallTextureFile.Parse(new byte[10], "TEXTURE.010"));
    }

    [Fact]
    public void Parse_RecordPointingOutsideTheFile_Throws()
    {
        var file = BuildSingleRecord(2, 1, 0, 1, BuildStridedRows(2, [[1, 2]]));

        // Record position field lives at header + 2.
        file[HeaderLength + 2] = 0xFF;
        file[HeaderLength + 3] = 0xFF;

        Assert.Throws<InvalidDataException>(() => DaggerfallTextureFile.Parse(file, "TEXTURE.010"));
    }

    [Fact]
    public void Parse_ZeroRleProbe_ThrowsInsteadOfSpinning()
    {
        const ushort compression = 0x1108;
        var rowHeaders = new List<byte>();
        WriteI16(rowHeaders, 28 + 4);
        WriteI16(rowHeaders, unchecked((short)0x8000));

        var row = new List<byte>();
        WriteI16(row, 4); // rowWidth
        WriteI16(row, 0); // zero probe: would loop forever if allowed

        var file = BuildSingleRecord(4, 1, compression, 1, [.. rowHeaders, .. row]);

        Assert.Throws<InvalidDataException>(() => DaggerfallTextureFile.Parse(file, "TEXTURE.010"));
    }
}
