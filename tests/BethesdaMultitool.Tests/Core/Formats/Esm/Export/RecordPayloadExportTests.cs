using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using BethesdaMultitool.Core.Formats.Esm.Analysis.FileAnalysis;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Models;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Export;

public sealed class RecordPayloadExportTests
{
    [Theory]
    [InlineData(false, false, 24)]
    [InlineData(false, true, 24)]
    [InlineData(true, false, 24)]
    [InlineData(true, true, 24)]
    [InlineData(false, false, 20)]
    public void BinaryExportPreservesTheCompleteChosenRepresentation(bool compressed, bool bigEndian, int headerSize)
    {
        var payload = Enumerable.Range(0, 513).Select(index => (byte)(index % 251)).ToArray();
        payload[^1] = 0xFE;
        byte[] stored;
        if (compressed)
        {
            using var stream = new MemoryStream();
            Span<byte> length = stackalloc byte[4];
            if (bigEndian) { BinaryPrimitives.WriteUInt32BigEndian(length, (uint)payload.Length); }
            else { BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)payload.Length); }
            stream.Write(length);
            using (var compressor = new ZLibStream(stream, CompressionLevel.SmallestSize, true)) { compressor.Write(payload); }
            stored = stream.ToArray();
        }
        else { stored = payload; }
        var source = new byte[headerSize + stored.Length];
        stored.CopyTo(source, headerSize);
        var record = new AnalyzerRecordInfo
        {
            Signature = "IMAD", FormId = 0xC1CC, Offset = 0, Flags = compressed ? 0x40000u : 0,
            DataSize = (uint)stored.Length, TotalSize = (uint)source.Length, RecordHeaderSize = headerSize
        };
        Assert.Equal(stored, RecordPayloadExport.Read(source, record, bigEndian, false));
        Assert.Equal(payload, RecordPayloadExport.Read(source, record, bigEndian, true));
        var directory = Path.Combine(Path.GetTempPath(), "bmt-payload-" + Guid.NewGuid().ToString("N"));
        try
        {
            var output = Path.Combine(directory, "payload.bin");
            RecordPayloadExport.Write("input.esm", source, record, bigEndian, true, output);
            Assert.Equal(payload, File.ReadAllBytes(output));
            using var metadata = JsonDocument.Parse(File.ReadAllText(output + ".json"));
            Assert.Equal(513, metadata.RootElement.GetProperty("length").GetInt32());
            Assert.Equal("decoded-payload", metadata.RootElement.GetProperty("representation").GetString());
            Assert.Equal(Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
                metadata.RootElement.GetProperty("sha256").GetString());
            Assert.Throws<IOException>(() => RecordPayloadExport.Write("input.esm", source, record, bigEndian, true, output));
        }
        finally
        {
            if (Directory.Exists(directory)) { Directory.Delete(directory, true); }
        }
    }
}
