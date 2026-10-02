using System.Text;
using BethesdaMultitool.Core.Formats.Daggerfall;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Daggerfall;

/// <summary>
///     The cut-1c header-only path of <see cref="DaggerfallTextureFile" /> (plan D9 and section 8,
///     slice 4): <see cref="DaggerfallTextureFile.ReadHeaders" /> returns the same widths, heights and
///     frame counts the full parse decodes (control: one width byte changed), derives each record's
///     byte range from its headers alone, and decodes nothing (control: pixel data the full parse
///     rejects). <see cref="DaggerfallTextureFileTests" /> is untouched; the builder here lays several
///     records out the way retail does, each descriptor followed by its own data.
/// </summary>
public sealed class DaggerfallTextureFileHeaderTests
{
    private const int HeaderLength = 26;
    private const int RecordHeaderLength = 20;
    private const int DescriptorLength = 28;
    private const ushort Rle = 0x1108;

    /// <summary>One record to build: geometry, compression, frame count and the data bytes that follow the descriptor.</summary>
    private sealed record Spec(int Width, int Height, ushort Compression, int FrameCount, byte[] Data);

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
    ///     Builds a TEXTURE file whose records follow the record-header table in order, each a
    ///     28-byte descriptor immediately followed by its data (data offset 28). The descriptor's +10
    ///     dword is written the way retail writes it (measured 2026-09-28): 28 plus the data length for a
    ///     multi-frame or RLE record, 28 for an empty one, and 28 plus width x height for a single-frame
    ///     record, whose data is a 256-byte-stride page rather than its own pixels.
    /// </summary>
    private static byte[] Build(params Spec[] specs)
    {
        var file = new List<byte>();
        WriteI16(file, specs.Length);
        file.AddRange(Encoding.ASCII.GetBytes("Header Set".PadRight(24, '\0')));

        var position = HeaderLength + specs.Length * RecordHeaderLength;
        foreach (var spec in specs)
        {
            WriteI16(file, 0x2000); // type1
            WriteI32(file, position);
            WriteI16(file, 0); // type2
            WriteI32(file, 0); // unknown
            file.AddRange(new byte[8]);
            position += DescriptorLength + spec.Data.Length;
        }

        foreach (var spec in specs)
        {
            var size = DeclaredSizeOf(spec);
            WriteI16(file, 5);
            WriteI16(file, 7);
            WriteI16(file, spec.Width);
            WriteI16(file, spec.Height);
            WriteI16(file, (short)spec.Compression);
            WriteI32(file, size);
            WriteI32(file, DescriptorLength);
            WriteI16(file, 0); // isNormal
            WriteI16(file, spec.FrameCount);
            WriteI16(file, 0); // unknown
            WriteI16(file, 0); // scaleX
            WriteI16(file, 0); // scaleY
            file.AddRange(spec.Data);
        }

        return [.. file];
    }

    /// <summary>The +10 dword as retail writes it for the spec's storage form.</summary>
    private static int DeclaredSizeOf(Spec spec)
    {
        if (spec.FrameCount == 0)
        {
            return DescriptorLength;
        }

        if (spec.Compression == Rle || spec.FrameCount > 1)
        {
            return DescriptorLength + spec.Data.Length;
        }

        return DescriptorLength + spec.Width * spec.Height;
    }

    /// <summary>A single-frame record: rows padded to the 256-byte stride.</summary>
    private static Spec Single(int width, int height)
    {
        var data = new List<byte>();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                data.Add((byte)(10 + y * width + x));
            }

            data.AddRange(new byte[DaggerfallTextureFile.UncompressedRowStride - width]);
        }

        return new Spec(width, height, 0, 1, [.. data]);
    }

    /// <summary>
    ///     A two-frame 4x1 record behind its offset table, each frame one transparent/literal run row;
    ///     <paramref name="secondFrameRuns" /> replaces the second frame's runs (default: a literal run of
    ///     exactly four).
    /// </summary>
    private static Spec Multi(byte[]? secondFrameRuns = null)
    {
        byte[] RunFrame(params byte[] runs)
        {
            var f = new List<byte>();
            WriteI16(f, 4);
            WriteI16(f, 1);
            f.AddRange(runs);
            return [.. f];
        }

        var first = RunFrame(1, 2, 77, 88, 1, 0);
        var second = RunFrame(secondFrameRuns ?? [0, 4, 5, 6, 7, 8]);
        var data = new List<byte>();
        WriteI32(data, 8);
        WriteI32(data, 8 + first.Length);
        data.AddRange(first);
        data.AddRange(second);
        return new Spec(4, 1, 0, 2, [.. data]);
    }

    /// <summary>A 4x2 RLE record: one run-length row and one raw row, offsets measured from the descriptor.</summary>
    private static Spec RleRecord()
    {
        var rleRow = new List<byte>();
        WriteI16(rleRow, 4);
        WriteI16(rleRow, -3);
        rleRow.Add(9);
        WriteI16(rleRow, 1);
        rleRow.Add(5);
        byte[] rawRow = [6, 7, 8, 9];

        var rowsStart = DescriptorLength + 2 * 4;
        var headers = new List<byte>();
        WriteI16(headers, rowsStart);
        WriteI16(headers, unchecked((short)0x8000));
        WriteI16(headers, rowsStart + rleRow.Count);
        WriteI16(headers, 0);
        return new Spec(4, 2, Rle, 1, [.. headers, .. rleRow, .. rawRow]);
    }

    private static Spec Empty()
    {
        return new Spec(6, 0, 0, 0, []);
    }

    [Fact]
    public void ReadHeaders_ReturnsWhatTheFullParseDecodes_OnEveryStorageForm()
    {
        var bytes = Build(Single(3, 2), Multi(), RleRecord(), Empty());

        var headers = DaggerfallTextureFile.ReadHeaders(bytes, "TEXTURE.010");
        var full = DaggerfallTextureFile.Parse(bytes, "TEXTURE.010");

        Assert.Equal("Header Set", headers.SetName);
        Assert.Null(headers.SolidBase);
        Assert.Equal(bytes.Length, headers.Length);
        Assert.Equal(4, headers.Records.Count);
        Assert.Equal(
            [DaggerfallTextureRecordForm.SingleFrame, DaggerfallTextureRecordForm.MultiFrame, DaggerfallTextureRecordForm.Rle, DaggerfallTextureRecordForm.Empty],
            headers.Records.Select(r => r.Form));

        for (var r = 0; r < 4; r++)
        {
            var header = headers.Records[r];
            var record = full.Records[r];
            Assert.Equal(header, record.Header);
            Assert.Equal(header.FrameCount, record.Frames.Count);
            Assert.Equal((5, 7), ((int)header.OffsetX, (int)header.OffsetY));
            Assert.Equal(header.DescriptorOffset + 28, header.DataStart);
            foreach (var frame in record.Frames)
            {
                Assert.Equal((header.Width, header.Height), (frame.Width, frame.Height));
            }
        }

        // The decoded frames are the ones DaggerfallTextureFileTests pins, so the full parse is unchanged.
        Assert.Equal([10, 11, 12, 13, 14, 15], full.Records[0].Frames[0].Indices);
        Assert.Equal([0, 77, 88, 0], full.Records[1].Frames[0].Indices);
        Assert.Equal([9, 9, 9, 5, 6, 7, 8, 9], full.Records[2].Frames[0].Indices);
    }

    [Fact]
    public void DeclaredEnd_MatchesTheRangeTheDecodeConsumed_ForEveryDecodableForm()
    {
        var bytes = Build(Single(3, 2), Multi(), RleRecord(), Empty());

        var headers = DaggerfallTextureFile.ReadHeaders(bytes, "TEXTURE.010");
        var full = DaggerfallTextureFile.Parse(bytes, "TEXTURE.010");

        for (var r = 0; r < 3; r++)
        {
            var consumed = full.Records[r].ConsumedRange;
            Assert.NotNull(consumed);
            Assert.Equal($"record:{r}", consumed.Value.Name);
            Assert.Equal(headers.Records[r].DataStart, consumed.Value.Start);
            Assert.Equal(consumed.Value.End, headers.Records[r].DeclaredEnd);
        }

        // Single-frame: the span of the strided rows, pinned by arithmetic on the builder's layout
        // rather than by the header the accessor derives it from.
        var single = headers.Records[0];
        var dataStart = HeaderLength + 4 * RecordHeaderLength + DescriptorLength;
        Assert.Equal(dataStart, single.DataStart);
        Assert.Equal(dataStart + 256 + 3, single.DeclaredEnd);
        Assert.Equal((uint)(28 + 3 * 2), single.DeclaredSize);

        // Multi-frame and RLE: descriptor plus the +10 dword.
        Assert.Equal(headers.Records[1].DescriptorOffset + headers.Records[1].DeclaredSize, headers.Records[1].DeclaredEnd);
        Assert.Equal(headers.Records[2].DescriptorOffset + 28 + RleRecord().Data.Length, headers.Records[2].DeclaredEnd);

        // Empty: the descriptor alone, and nothing consumed.
        Assert.Null(full.Records[3].ConsumedRange);
        Assert.Equal(headers.Records[3].DescriptorOffset + 28, headers.Records[3].DeclaredEnd);
        Assert.Equal(28u, headers.Records[3].DeclaredSize);
    }

    /// <summary>
    ///     A literal run longer than the row it lands in: the legacy multi-frame decoder takes only the
    ///     pixels the row has room for and advances the source by those, so the consumed range ends two
    ///     bytes short of the run's declared length here, while the +10 dword (and the gate-1c oracle,
    ///     which advances by the whole run) reach the data's end. Retail has no such run (0 of 5,558
    ///     multi-frame frames, measured 2026-09-28), which is why the two agree on every retail record;
    ///     this pins the C# side so that an oracle change cannot disagree silently.
    /// </summary>
    [Fact]
    public void MultiFrame_LiteralRunOvershootingItsRow_IsConsumedOnlyAsFarAsTheRowTakes()
    {
        var bytes = Build(Multi([0, 6, 5, 6, 7, 8, 9, 9]));

        var header = Assert.Single(DaggerfallTextureFile.ReadHeaders(bytes, "TEXTURE.010").Records);
        var record = Assert.Single(DaggerfallTextureFile.Parse(bytes, "TEXTURE.010").Records);

        Assert.Equal(DaggerfallTextureRecordForm.MultiFrame, header.Form);
        Assert.Equal([5, 6, 7, 8], record.Frames[1].Indices);
        Assert.Equal(bytes.Length, header.DeclaredEnd);
        Assert.NotNull(record.ConsumedRange);
        Assert.Equal(bytes.Length - 2, record.ConsumedRange.Value.End);

        // Control: the run that exactly fills its row consumes to the declared end.
        var exact = Build(Multi());
        var exactConsumed = Assert.Single(DaggerfallTextureFile.Parse(exact, "TEXTURE.010").Records).ConsumedRange;
        Assert.NotNull(exactConsumed);
        Assert.Equal(exact.Length, exactConsumed.Value.End);
    }

    [Fact]
    public void ReadHeaders_SeesAChangedWidthByte_TheFullParseOfTheOriginalDoesNot()
    {
        var bytes = Build(Single(3, 2));
        var full = DaggerfallTextureFile.Parse(bytes, "TEXTURE.010");

        var altered = (byte[])bytes.Clone();
        var descriptor = HeaderLength + RecordHeaderLength;
        altered[descriptor + 4] = 9;

        var header = Assert.Single(DaggerfallTextureFile.ReadHeaders(altered, "TEXTURE.010").Records);
        Assert.Equal(9, header.Width);
        Assert.NotEqual(full.Records[0].Frames[0].Width, header.Width);
        Assert.NotEqual(full.Records[0].Header, header);
    }

    [Fact]
    public void ReadHeaders_DecodesNoPixel_SoTruncatedRowsDoNotStopIt()
    {
        var bytes = Build(Single(3, 2));
        var truncated = bytes[..^300];

        Assert.Throws<InvalidDataException>(() => DaggerfallTextureFile.Parse(truncated, "TEXTURE.010"));

        var header = Assert.Single(DaggerfallTextureFile.ReadHeaders(truncated, "TEXTURE.010").Records);
        Assert.Equal((3, 2, 1), (header.Width, header.Height, header.FrameCount));
        Assert.True(header.DeclaredEnd > truncated.Length);
    }

    [Fact]
    public void ReadHeaders_JudgesTheBytes_NotTheThreeRefusedNames()
    {
        var valid = Build(Single(2, 1));

        // The full parse refuses the name; the header read reads the bytes, which are fine.
        Assert.Throws<NotSupportedException>(() => DaggerfallTextureFile.Parse(valid, "TEXTURE.215"));
        Assert.Single(DaggerfallTextureFile.ReadHeaders(valid, "TEXTURE.215").Records);

        // Daggerfall's real 215 and 217 are 46-byte stubs whose one record points at offset 46, past
        // the end: the structural check rejects them without needing the name.
        var stub = new List<byte>();
        WriteI16(stub, 1);
        stub.AddRange(new byte[24]);
        WriteI16(stub, 0);
        WriteI32(stub, 46);
        stub.AddRange(new byte[14]);
        var stubBytes = stub.ToArray();
        Assert.Equal(46, stubBytes.Length);
        var error = Assert.Throws<InvalidDataException>(() => DaggerfallTextureFile.ReadHeaders(stubBytes, "TEXTURE.217"));
        Assert.Contains("points outside the file", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("TEXTURE.000", 0)]
    [InlineData("TEXTURE.001", 128)]
    public void ReadHeaders_MarksSolidSwatches_WhoseDescriptorsHoldNoGeometry(string name, int baseIndex)
    {
        var bytes = Build(new Spec(999, 999, 0, 0, []), new Spec(999, 999, 0, 0, []));

        var headers = DaggerfallTextureFile.ReadHeaders(bytes, name);
        var full = DaggerfallTextureFile.Parse(bytes, name);

        Assert.Equal(baseIndex, headers.SolidBase);
        Assert.Equal(2, headers.Records.Count);
        for (var r = 0; r < 2; r++)
        {
            Assert.Equal(DaggerfallTextureRecordForm.Solid, headers.Records[r].Form);
            Assert.Equal(baseIndex + r, headers.Records[r].SolidIndex);
            Assert.Null(headers.Records[r].DeclaredEnd);
            Assert.Equal(headers.Records[r], full.Records[r].Header);
            Assert.Null(full.Records[r].ConsumedRange);

            var frame = Assert.Single(full.Records[r].Frames);
            Assert.Equal(DaggerfallTextureFile.SolidSize, frame.Width);
            Assert.All(frame.Indices, i => Assert.Equal((byte)(baseIndex + r), i));
        }
    }

    [Fact]
    public void ReadHeaders_RejectsWhatTheFullParseRejects_Structurally()
    {
        Assert.Throws<InvalidDataException>(() => DaggerfallTextureFile.ReadHeaders(new byte[10], "TEXTURE.010"));

        var outside = Build(Single(2, 1));
        outside[HeaderLength + 2] = 0xFF;
        outside[HeaderLength + 3] = 0xFF;
        Assert.Throws<InvalidDataException>(() => DaggerfallTextureFile.ReadHeaders(outside, "TEXTURE.010"));

        // Frames declared over empty geometry: the same rejection, before any decode would start.
        var emptyGeometry = Build(new Spec(0, 4, 0, 1, new byte[256]));
        Assert.Throws<InvalidDataException>(() => DaggerfallTextureFile.Parse(emptyGeometry, "TEXTURE.010"));
        Assert.Throws<InvalidDataException>(() => DaggerfallTextureFile.ReadHeaders(emptyGeometry, "TEXTURE.010"));

        var noRecords = Build(Single(2, 1));
        noRecords[0] = 0;
        noRecords[1] = 0;
        Assert.Throws<InvalidDataException>(() => DaggerfallTextureFile.ReadHeaders(noRecords, "TEXTURE.010"));
    }
}
