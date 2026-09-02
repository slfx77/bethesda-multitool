using System;
using System.Collections.Generic;
using System.IO;
using BethesdaMultitool.Core.Formats.Daggerfall;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Daggerfall;

/// <summary>
///     Hand-built vectors for <see cref="DaggerfallCifRciFile" />, one per layout. Shapes follow
///     the retail corpus (surveyed 2026-09-02): 50 plain CIFs of headered IMG records tiling to
///     EOF, 7 headerless RCI-style files (FACES.CIF included) sized only by the filename table,
///     and 19 weapon CIFs whose animation records advance by their total-size word.
/// </summary>
public class DaggerfallCifRciFileTests
{
    private const int ImgHeaderLength = 12;
    private const int AnimationHeaderLength = 76;

    private static void WriteI16(List<byte> to, int value)
    {
        to.Add((byte)(value & 0xFF));
        to.Add((byte)((value >> 8) & 0xFF));
    }

    /// <summary>Appends one IMG-style record: 12-byte header then the data bytes verbatim.</summary>
    private static void AddImgRecord(
        List<byte> file,
        int offsetX,
        int offsetY,
        int width,
        int height,
        int compression,
        byte[] data,
        int? declaredDataLength = null)
    {
        WriteI16(file, offsetX);
        WriteI16(file, offsetY);
        WriteI16(file, width);
        WriteI16(file, height);
        WriteI16(file, compression);
        WriteI16(file, declaredDataLength ?? data.Length);
        file.AddRange(data);
    }

    /// <summary>
    ///     Appends one weapon animation record: the 12 fixed header bytes, 31 frame offsets
    ///     (nonzero slots first, measured from the record start so the first is always 76),
    ///     the total-size word, then the RLE streams.
    /// </summary>
    private static void AddWeaponAnimation(
        List<byte> file,
        int width,
        int height,
        byte[][] frameStreams,
        int? totalSizeOverride = null)
    {
        WriteI16(file, width);
        WriteI16(file, height);
        WriteI16(file, width); // last-frame width (draw metadata, unused by the decoder)
        WriteI16(file, 10); // x offset
        WriteI16(file, 0); // last-frame y offset
        WriteI16(file, 0); // data length

        var offset = AnimationHeaderLength;
        for (var slot = 0; slot < 31; slot++)
        {
            if (slot < frameStreams.Length)
            {
                WriteI16(file, offset);
                offset += frameStreams[slot].Length;
            }
            else
            {
                WriteI16(file, 0);
            }
        }

        WriteI16(file, totalSizeOverride ?? offset);
        foreach (var stream in frameStreams)
        {
            file.AddRange(stream);
        }
    }

    [Fact]
    public void Parse_PlainCif_WalksHeaderedRecordsToEndOfFile()
    {
        // Two uncompressed records back to back; the second is found only by advancing
        // 12 + PixelDataLength past the first.
        var file = new List<byte>();
        AddImgRecord(file, 3, 4, 2, 2, 0, [1, 2, 3, 4]);
        AddImgRecord(file, -1, 2, 3, 1, 0, [9, 8, 7]);

        var parsed = DaggerfallCifRciFile.Parse([.. file], "KIDS00I0.CIF");

        Assert.Equal(2, parsed.Records.Count);

        var first = Assert.Single(parsed.Records[0].Frames);
        Assert.Equal(2, first.Width);
        Assert.Equal(2, first.Height);
        Assert.Equal([1, 2, 3, 4], first.Indices);
        Assert.Equal(3, first.XOffset);
        Assert.Equal(4, first.YOffset);
        Assert.False(parsed.Records[0].IsWeaponAnimation);

        var second = Assert.Single(parsed.Records[1].Frames);
        Assert.Equal([9, 8, 7], second.Indices);
        Assert.Equal(-1, second.XOffset);
        Assert.Equal(2, second.YOffset);
        Assert.Equal(1, parsed.Records[1].Index);
    }

    [Fact]
    public void Parse_PlainCif_RleRecord_DecodesRunsAndAdvancesByCompressedLength()
    {
        // 4x2 RLE record. Stream hand-decoded: 130 > 127 repeats 5 three times (130-127);
        // 2 copies three literals 6,7,8 (2+1); 129 repeats 9 twice (129-127). 3+3+2 = 8 pixels.
        // The stream is 8 bytes, so the next record sits at 12 + 8 — reachable only if the walk
        // advances by the COMPRESSED length, not the decoded pixel count.
        var file = new List<byte>();
        AddImgRecord(file, 0, 0, 4, 2, 2, [130, 5, 2, 6, 7, 8, 129, 9]);
        AddImgRecord(file, 0, 0, 1, 1, 0, [42]);

        var parsed = DaggerfallCifRciFile.Parse([.. file], "FIRE00C6.CIF");

        Assert.Equal(2, parsed.Records.Count);
        Assert.Equal([5, 5, 5, 6, 7, 8, 9, 9], parsed.Records[0].Frames[0].Indices);
        Assert.Equal([42], parsed.Records[1].Frames[0].Indices);
    }

    [Theory]
    [InlineData("FACES.CIF", 64, 64)]
    [InlineData("CHLD00I0.RCI", 64, 64)]
    [InlineData("TFAC00I0.RCI", 64, 64)]
    [InlineData("BUTTONS.RCI", 32, 16)]
    [InlineData("MPOP.RCI", 17, 17)]
    [InlineData("NOTE.RCI", 44, 9)]
    [InlineData("SPOP.RCI", 22, 22)]
    public void Parse_RciTable_SlicesHeaderlessRecordsByFilename(string name, int width, int height)
    {
        // Two raw frames, no header bytes anywhere. Pixel p of record r is (r * 7 + p) % 256,
        // so record boundaries are provable: record 1's first pixel is 7, not frame-size-dependent.
        var frameLength = width * height;
        var file = new byte[frameLength * 2];
        for (var r = 0; r < 2; r++)
        {
            for (var p = 0; p < frameLength; p++)
            {
                file[(r * frameLength) + p] = (byte)(((r * 7) + p) % 256);
            }
        }

        var parsed = DaggerfallCifRciFile.Parse(file, name);

        Assert.Equal(2, parsed.Records.Count);
        for (var r = 0; r < 2; r++)
        {
            var frame = Assert.Single(parsed.Records[r].Frames);
            Assert.Equal(width, frame.Width);
            Assert.Equal(height, frame.Height);
            Assert.Equal((byte)(r * 7), frame.Indices[0]);
            Assert.Equal((byte)(((r * 7) + frameLength - 1) % 256), frame.Indices[frameLength - 1]);
            Assert.Equal(0, frame.XOffset);
            Assert.Equal(0, frame.YOffset);
        }
    }

    [Fact]
    public void Parse_Rci_TrailingRemainderBytesAreIgnored()
    {
        // Retail TFAC00I0.RCI is 503 * 4096 + 7 bytes; the 7-byte tail is junk. One 64x64 frame
        // plus 7 extra bytes must yield exactly one record.
        var file = new byte[(64 * 64) + 7];
        file[0] = 0xAB;

        var parsed = DaggerfallCifRciFile.Parse(file, "TFAC00I0.RCI");

        var record = Assert.Single(parsed.Records);
        Assert.Equal(0xAB, Assert.Single(record.Frames).Indices[0]);
    }

    [Fact]
    public void Parse_RciNotInTheDimensionTable_Throws()
    {
        // RCI files are headerless; without a table entry there is no geometry to decode with.
        Assert.Throws<NotSupportedException>(
            () => DaggerfallCifRciFile.Parse(new byte[4096], "KAMIRA.RCI"));
    }

    [Fact]
    public void Parse_RciSmallerThanOneRecord_Throws()
    {
        Assert.Throws<InvalidDataException>(
            () => DaggerfallCifRciFile.Parse(new byte[100], "BUTTONS.RCI"));
    }

    [Fact]
    public void Parse_WeaponCif_ReadsWieldImageThenAnimationRecords()
    {
        // A wield IMG record, then one animation with two 3x2 frames.
        // Frame 0 stream hand-decoded: 132 repeats 1 five times (132-127); 0 copies one
        // literal 2 -> [1,1,1,1,1,2]. Frame 1: 5 copies six literals -> [3,4,5,6,7,8].
        // Offsets are 76 and 80 from the record start; total size 76 + 4 + 7 = 87.
        var file = new List<byte>();
        AddImgRecord(file, 260, 114, 2, 2, 0, [11, 12, 13, 14]);
        AddWeaponAnimation(file, 3, 2, [[132, 1, 0, 2], [5, 3, 4, 5, 6, 7, 8]]);

        var parsed = DaggerfallCifRciFile.Parse([.. file], "WEAPON04.CIF");

        Assert.Equal(2, parsed.Records.Count);

        var wield = parsed.Records[0];
        Assert.False(wield.IsWeaponAnimation);
        Assert.Equal(260, wield.OffsetX);
        Assert.Equal(114, wield.OffsetY);
        Assert.Equal([11, 12, 13, 14], Assert.Single(wield.Frames).Indices);

        var animation = parsed.Records[1];
        Assert.True(animation.IsWeaponAnimation);
        Assert.Equal(2, animation.Frames.Count);
        Assert.Equal(3, animation.Frames[0].Width);
        Assert.Equal(2, animation.Frames[0].Height);
        Assert.Equal([1, 1, 1, 1, 1, 2], animation.Frames[0].Indices);
        Assert.Equal([3, 4, 5, 6, 7, 8], animation.Frames[1].Indices);
        Assert.Equal(0, animation.OffsetX);
        Assert.Equal(0, animation.OffsetY);
    }

    [Fact]
    public void Parse_Weapon09_TheBow_HasNoWieldImage()
    {
        // WEAPON09.CIF starts directly with an animation header (retail: a single 7-frame
        // record). Stream hand-decoded: 128 > 127 repeats 200 once (128 - 127); 0 copies one
        // literal 33 -> [200, 33].
        var file = new List<byte>();
        AddWeaponAnimation(file, 2, 1, [[128, 200, 0, 33]]);

        var parsed = DaggerfallCifRciFile.Parse([.. file], "WEAPON09.CIF");

        var record = Assert.Single(parsed.Records);
        Assert.True(record.IsWeaponAnimation);
        Assert.Equal([200, 33], Assert.Single(record.Frames).Indices);
    }

    [Fact]
    public void Parse_WeaponAnimationTotalSizeSmallerThanHeader_ThrowsInsteadOfSpinning()
    {
        // Total size 0 would restart the walk at the same record forever.
        var file = new List<byte>();
        AddWeaponAnimation(file, 2, 1, [[128, 200, 0, 33]], totalSizeOverride: 0);

        Assert.Throws<InvalidDataException>(
            () => DaggerfallCifRciFile.Parse([.. file], "WEAPON00.CIF"));
    }

    [Fact]
    public void Parse_WeaponFrameOffsetPastEndOfFile_Throws()
    {
        // A lone 76-byte animation header whose only frame offset points at byte 200.
        var file = new List<byte>();
        WriteI16(file, 2); // width
        WriteI16(file, 1); // height
        WriteI16(file, 2); // last-frame width
        WriteI16(file, 0); // x offset
        WriteI16(file, 0); // last-frame y offset
        WriteI16(file, 0); // data length
        WriteI16(file, 200); // frame 0 offset: outside the file
        for (var slot = 1; slot < 31; slot++)
        {
            WriteI16(file, 0);
        }

        WriteI16(file, AnimationHeaderLength);

        Assert.Throws<InvalidDataException>(
            () => DaggerfallCifRciFile.Parse([.. file], "WEAPON00.CIF"));
    }

    [Fact]
    public void Parse_RleRunOverflowingTheFrame_Throws()
    {
        // 2x1 frame, but 130 repeats 3 pixels (130-127): one more than the frame holds.
        var file = new List<byte>();
        AddImgRecord(file, 0, 0, 2, 1, 2, [130, 5]);

        Assert.Throws<InvalidDataException>(
            () => DaggerfallCifRciFile.Parse([.. file], "FIRE00C6.CIF"));
    }

    [Fact]
    public void Parse_TruncatedRleStream_Throws()
    {
        // Declares a 2x2 RLE record whose stream ends after one literal pixel.
        var file = new List<byte>();
        AddImgRecord(file, 0, 0, 2, 2, 2, [0, 9]);

        Assert.Throws<InvalidDataException>(
            () => DaggerfallCifRciFile.Parse([.. file], "FIRE00C6.CIF"));
    }

    [Fact]
    public void Parse_TruncatedImgHeader_Throws()
    {
        Assert.Throws<InvalidDataException>(
            () => DaggerfallCifRciFile.Parse(new byte[8], "KIDS00I0.CIF"));
    }

    [Fact]
    public void Parse_TruncatedUncompressedPixels_Throws()
    {
        // Header promises 2x2 = 4 pixels but only 2 data bytes exist.
        var file = new List<byte>();
        AddImgRecord(file, 0, 0, 2, 2, 0, [1, 2], declaredDataLength: 4);

        Assert.Throws<InvalidDataException>(
            () => DaggerfallCifRciFile.Parse([.. file], "KIDS00I0.CIF"));
    }

    [Fact]
    public void Parse_UncompressedRecordDeclaringFewerBytesThanPixels_Throws()
    {
        // 2x2 = 4 pixels but PixelDataLength says 2: the walk would read the next record's
        // header bytes as pixels.
        var file = new List<byte>();
        AddImgRecord(file, 0, 0, 2, 2, 0, [1, 2], declaredDataLength: 2);

        Assert.Throws<InvalidDataException>(
            () => DaggerfallCifRciFile.Parse([.. file], "KIDS00I0.CIF"));
    }

    [Fact]
    public void Parse_UnknownCompressionWord_Throws()
    {
        // 0x0108 (ImageRle) belongs to TEXTURE files, never CIF records.
        var file = new List<byte>();
        AddImgRecord(file, 0, 0, 2, 1, 0x0108, [1, 2]);

        Assert.Throws<InvalidDataException>(
            () => DaggerfallCifRciFile.Parse([.. file], "KIDS00I0.CIF"));
    }

    [Theory]
    [InlineData("KIDS00I0.CIF")]
    [InlineData("WEAPON00.CIF")]
    public void Parse_EmptyFile_Throws(string name)
    {
        Assert.Throws<InvalidDataException>(
            () => DaggerfallCifRciFile.Parse(ReadOnlySpan<byte>.Empty, name));
    }

    [Fact]
    public void IsCifRciFileName_MatchesBothExtensionsCaseInsensitively()
    {
        Assert.True(DaggerfallCifRciFile.IsCifRciFileName("FACES.CIF"));
        Assert.True(DaggerfallCifRciFile.IsCifRciFileName("buttons.rci"));
        Assert.False(DaggerfallCifRciFile.IsCifRciFileName("TEXTURE.010"));
    }
}
