using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using Xunit;
using static BethesdaMultitool.Tests.Helpers.EsmTestRecordBuilder;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

public sealed class BendableSplineParserTests
{
    private const uint SplineFormId = 0x00106F19;
    private const uint TextureSetFormId = 0x000F6D8A;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParseBendableSplines_DecodesBndsDefinition_InBothEndiannesses(bool bigEndian)
    {
        var bytes = BuildRecordBytes(SplineFormId, "BNDS", bigEndian,
            ("EDID", NullTermString("WorkshopWire")),
            ("OBND", BuildBounds(bigEndian)),
            ("DNAM", BuildDefinition(bigEndian)),
            ("TNAM", BuildUInt32(TextureSetFormId, bigEndian)));

        var spline = ParseSingle(bytes, bigEndian);

        Assert.Equal(SplineFormId, spline.FormId);
        Assert.Equal("WorkshopWire", spline.EditorId);
        Assert.Equal(TextureSetFormId, spline.TextureSetFormId);
        Assert.Equal(bigEndian, spline.IsBigEndian);
        Assert.NotNull(spline.Bounds);
        Assert.Equal(-12, spline.Bounds!.X1);
        Assert.Equal(28, spline.Bounds.Z2);
        Assert.NotNull(spline.Data);
        Assert.Equal(3.5f, spline.Data!.DefaultTileCount);
        Assert.Equal((ushort)12, spline.Data.DefaultSliceCount);
        Assert.Equal((ushort)2, spline.Data.TilesRelativeToLengthRaw);
        Assert.True(spline.Data.TilesRelativeToLength);
        Assert.Equal(new Vector4(0.2f, 0.4f, 0.6f, 0.8f), spline.Data.DefaultColor);
        Assert.Equal(1.25f, spline.Data.WindSensibility);
        Assert.Equal(0.75f, spline.Data.WindFlexibility);
    }

    [Fact]
    public void ParseBendableSplines_RejectsTruncatedDnamWithoutDroppingIdentityOrTexture()
    {
        var bytes = BuildRecordBytes(SplineFormId, "BNDS", false,
            ("EDID", NullTermString("WorkshopWire")),
            ("DNAM", new byte[31]),
            ("TNAM", BuildUInt32(TextureSetFormId, false)));

        var spline = ParseSingle(bytes, false);

        Assert.Equal("WorkshopWire", spline.EditorId);
        Assert.Equal(TextureSetFormId, spline.TextureSetFormId);
        Assert.Null(spline.Data);
    }

    private static BendableSplineRecord ParseSingle(
        byte[] recordBytes,
        bool bigEndian)
    {
        var mainRecord = new DetectedMainRecord(
            "BNDS", (uint)(recordBytes.Length - 24), 0, SplineFormId, 0, bigEndian);
        using var mmf = MemoryMappedFile.CreateNew(null, recordBytes.Length);
        using var accessor = mmf.CreateViewAccessor(0, recordBytes.Length);
        accessor.WriteArray(0, recordBytes, 0, recordBytes.Length);

        var parser = new RecordParser(
            MakeScanResult([mainRecord]), accessor: accessor, fileSize: recordBytes.Length);
        return Assert.Single(parser.ParseBendableSplines());
    }

    private static byte[] BuildBounds(bool bigEndian)
    {
        var values = new short[] { -12, -8, -4, 16, 24, 28 };
        var bytes = new byte[12];
        for (var i = 0; i < values.Length; i++)
        {
            if (bigEndian)
            {
                BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(i * 2, 2), values[i]);
            }
            else
            {
                BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2, 2), values[i]);
            }
        }

        return bytes;
    }

    private static byte[] BuildDefinition(bool bigEndian)
    {
        var bytes = new byte[32];
        WriteFloat(bytes, 0, 3.5f, bigEndian);
        WriteUInt16(bytes, 4, 12, bigEndian);
        WriteUInt16(bytes, 6, 2, bigEndian);
        WriteFloat(bytes, 8, 0.2f, bigEndian);
        WriteFloat(bytes, 12, 0.4f, bigEndian);
        WriteFloat(bytes, 16, 0.6f, bigEndian);
        WriteFloat(bytes, 20, 0.8f, bigEndian);
        WriteFloat(bytes, 24, 1.25f, bigEndian);
        WriteFloat(bytes, 28, 0.75f, bigEndian);
        return bytes;
    }

    private static byte[] BuildUInt32(uint value, bool bigEndian)
    {
        var bytes = new byte[4];
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        }

        return bytes;
    }

    private static void WriteUInt16(byte[] bytes, int offset, ushort value, bool bigEndian)
    {
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset, 2), value);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), value);
        }
    }

    private static void WriteFloat(byte[] bytes, int offset, float value, bool bigEndian)
    {
        if (bigEndian)
        {
            BinaryPrimitives.WriteSingleBigEndian(bytes.AsSpan(offset, 4), value);
        }
        else
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset, 4), value);
        }
    }
}
