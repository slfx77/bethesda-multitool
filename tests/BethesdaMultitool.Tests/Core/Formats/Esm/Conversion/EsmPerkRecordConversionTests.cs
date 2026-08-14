using System.Buffers.Binary;
using System.IO.Compression;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Indexing;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Processing;
using Xunit;
using static BethesdaMultitool.Tests.Helpers.EsmTestRecordBuilder;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Conversion;

public class EsmPerkRecordConversionTests
{
    private static readonly (string Sig, byte[] Data)[] PerkDataChain =
    [
        ("DATA", [0x01, 0x02, 0x03, 0x04]),
        ("PRKE", [0x01, 0x00, 0x00]),
        ("DATA", [0x11, 0x22, 0x33, 0x44]),
        ("PRKF", []),
        ("DATA", [0x05, 0x06, 0x07, 0x08])
    ];

    [Fact]
    public void ConvertRecordToBuffer_PerkData4_UsesBoundedEntryScope()
    {
        var input = BuildRecordBytes(0x00123456, "PERK", true, PerkDataChain);
        var writer = new EsmRecordWriter(input, new EsmConversionStats());

        var converted = writer.ConvertRecordToBuffer(0, out _, out var signature);

        Assert.NotNull(converted);
        Assert.Equal("PERK", signature);
        AssertPerkDataScopes(converted![24..]);
    }

    [Fact]
    public void ConvertRecordToBuffer_CompressedPerkData4_UsesBoundedEntryScope()
    {
        var input = BuildCompressedRecordBE("PERK", 0x00123456, PerkDataChain);
        var writer = new EsmRecordWriter(input, new EsmConversionStats());

        var converted = writer.ConvertRecordToBuffer(0, out _, out var signature);

        Assert.NotNull(converted);
        Assert.Equal("PERK", signature);
        Assert.NotEqual(0u, BinaryPrimitives.ReadUInt32LittleEndian(converted!.AsSpan(8)) & 0x00040000u);
        AssertPerkDataScopes(DecompressConvertedBody(converted));
    }

    private static void AssertPerkDataScopes(byte[] convertedBody)
    {
        var dataSubrecords = EsmRecordParser.ParseSubrecords(convertedBody, false)
            .Where(subrecord => subrecord.Signature == "DATA")
            .Select(subrecord => subrecord.Data)
            .ToArray();

        Assert.Equal(3, dataSubrecords.Length);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04 }, dataSubrecords[0]);
        Assert.Equal(new byte[] { 0x44, 0x33, 0x22, 0x11 }, dataSubrecords[1]);
        Assert.Equal(new byte[] { 0x05, 0x06, 0x07, 0x08 }, dataSubrecords[2]);
    }

    private static byte[] DecompressConvertedBody(byte[] convertedRecord)
    {
        var compressedBodySize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(convertedRecord.AsSpan(4)));
        var expectedSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(convertedRecord.AsSpan(24)));
        using var compressed = new MemoryStream(convertedRecord, 28, compressedBodySize - 4, false);
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        using var decompressed = new MemoryStream(expectedSize);
        zlib.CopyTo(decompressed);
        var result = decompressed.ToArray();
        Assert.Equal(expectedSize, result.Length);
        return result;
    }
}
