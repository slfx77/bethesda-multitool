using System.Buffers.Binary;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

public class PluginRecordByteBuilderTests
{
    private const uint CompressedFlag = 0x00040000u;

    [Fact]
    public void BuildNewRecordBytes_WithCompressedFlag_WritesInflatableBody()
    {
        var rawSubrecords = BuildSubrecords(("DATA", new byte[4]), ("VHGT", new byte[1096]));

        var bytes = PluginRecordByteBuilder.BuildNewRecordBytes(
            "LAND",
            0x00150FC0,
            CompressedFlag,
            rawSubrecords);

        var flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4));
        var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4));
        var body = bytes.AsSpan(EsmParser.MainRecordHeaderSize, (int)dataSize);
        var decompressed = EsmParser.DecompressRecordData(body, bigEndian: false);

        Assert.NotNull(decompressed);
        Assert.Equal(CompressedFlag, flags & CompressedFlag);
        Assert.Equal(BuildSubrecordBytes(rawSubrecords), decompressed);
    }

    [Fact]
    public void BuildOverrideRecordBytes_WithCompression_WritesInflatableBody()
    {
        var esmRecord = new ParsedMainRecord
        {
            Header = new MainRecordHeader
            {
                Signature = "LAND",
                FormId = 0x00150FC0,
                Flags = 0,
                Version = 0x000F
            }
        };
        var rawSubrecords = BuildSubrecordBytes(("DATA", new byte[4]), ("VHGT", new byte[1096]));

        var bytes = PluginRecordByteBuilder.BuildOverrideRecordBytes(
            esmRecord,
            rawSubrecords,
            new PluginBuildOptions { CompressRecords = true });

        var flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4));
        var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4));
        var body = bytes.AsSpan(EsmParser.MainRecordHeaderSize, (int)dataSize);
        var decompressed = EsmParser.DecompressRecordData(body, bigEndian: false);

        Assert.NotNull(decompressed);
        Assert.Equal(CompressedFlag, flags & CompressedFlag);
        Assert.Equal(rawSubrecords, decompressed);
    }

    [Fact]
    public void BuildOverrideRecordBytes_WithoutCompression_ClearsCompressedFlagAndWritesRawBody()
    {
        var esmRecord = new ParsedMainRecord
        {
            Header = new MainRecordHeader
            {
                Signature = "NPC_",
                FormId = 0x00133FDD,
                Flags = CompressedFlag,
                Version = 0x000F
            }
        };
        var rawSubrecords = BuildSubrecordBytes(("ACBS", new byte[24]));

        var bytes = PluginRecordByteBuilder.BuildOverrideRecordBytes(
            esmRecord,
            rawSubrecords,
            new PluginBuildOptions { CompressRecords = false });

        var flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4));
        var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4));
        var body = bytes.AsSpan(EsmParser.MainRecordHeaderSize, (int)dataSize).ToArray();

        Assert.Equal(0u, flags & CompressedFlag);
        Assert.Equal(rawSubrecords, body);
    }

    private static byte[] BuildSubrecordBytes(params (string Signature, byte[] Bytes)[] subrecords)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.Latin1, true);
        WriteSubrecords(writer, subrecords);
        return stream.ToArray();
    }

    private static byte[] BuildSubrecordBytes(IEnumerable<EncodedSubrecord> subrecords)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.Latin1, true);
        WriteSubrecords(writer, subrecords.Select(subrecord => (subrecord.Signature, subrecord.Bytes)));
        return stream.ToArray();
    }

    private static List<EncodedSubrecord> BuildSubrecords(params (string Signature, byte[] Bytes)[] subrecords) =>
        subrecords.Select(subrecord => new EncodedSubrecord(subrecord.Signature, subrecord.Bytes)).ToList();

    private static void WriteSubrecords(
        BinaryWriter writer,
        IEnumerable<(string Signature, byte[] Bytes)> subrecords)
    {
        foreach (var (signature, bytes) in subrecords)
        {
            SubrecordEncoder.WriteSubrecord(writer, signature, bytes);
        }
    }
}
