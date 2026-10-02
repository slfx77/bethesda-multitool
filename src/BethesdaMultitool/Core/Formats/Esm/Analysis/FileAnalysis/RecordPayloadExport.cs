using System.Security.Cryptography;
using System.Text.Json;
using BethesdaMultitool.Core.Formats.Esm.Conversion;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Models;

namespace BethesdaMultitool.Core.Formats.Esm.Analysis.FileAnalysis;

internal static class RecordPayloadExport
{
    internal static byte[] Read(byte[] source, AnalyzerRecordInfo record, bool bigEndian, bool decoded)
    {
        var start = (long)record.Offset + record.RecordHeaderSize;
        if (record.Offset < 0 || record.RecordHeaderSize < 0 || start < 0 || start > source.LongLength ||
            record.DataSize > int.MaxValue || record.DataSize > source.LongLength - start)
        {
            throw new InvalidDataException($"{record.Signature} 0x{record.FormId:X8}: offset=0x{record.Offset:X}, " +
                $"declared={record.DataSize}, available={Math.Max(0, source.LongLength - start)}, " +
                $"byte order={(bigEndian ? "big-endian" : "little-endian")}");
        }
        return decoded ? EsmHelpers.GetRecordData(source, record, bigEndian)
            : source.AsSpan((int)start, (int)record.DataSize).ToArray();
    }

    internal static void Write(string sourcePath, byte[] source, AnalyzerRecordInfo record,
        bool bigEndian, bool decoded, string outputPath)
    {
        var path = Path.GetFullPath(outputPath);
        var metadataPath = path + ".json";
        if (File.Exists(path) || File.Exists(metadataPath))
        {
            throw new IOException("Payload output or its metadata already exists.");
        }
        var bytes = Read(source, record, bigEndian, decoded);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            file.Write(bytes);
        }
        using var metadata = new FileStream(metadataPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new Utf8JsonWriter(metadata, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", 1);
        writer.WriteString("source", Path.GetFullPath(sourcePath));
        writer.WriteString("sourceSha256", Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant());
        writer.WriteString("signature", record.Signature);
        writer.WriteString("formId", $"{record.FormId:X8}");
        writer.WriteNumber("recordOffset", record.Offset);
        writer.WriteNumber("payloadOffset", (long)record.Offset + record.RecordHeaderSize);
        writer.WriteNumber("recordHeaderLength", record.RecordHeaderSize);
        writer.WriteNumber("storedLength", record.DataSize);
        writer.WriteString("representation", decoded ? "decoded-payload" : "stored-payload");
        writer.WriteBoolean("compressed", record.IsCompressed);
        writer.WriteString("byteOrder", bigEndian ? "big-endian" : "little-endian");
        writer.WriteNumber("length", bytes.Length);
        writer.WriteString("sha256", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        writer.WriteEndObject();
    }
}
