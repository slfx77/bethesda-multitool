using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Analysis;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm;

public sealed class EsmDescriptorScannerTests
{
    private const uint CompressedFlag = 0x00040000u;

    [Fact]
    public void Scan_MatchesParsedPipelineForSyntheticRecordsAndGrups()
    {
        var fileData = BuildSyntheticEsm();
        var isBigEndian = EsmParser.IsBigEndian(fileData);
        var (parsedRecords, parsedGrups) = EsmParser.EnumerateRecordsWithGrups(fileData);
        var (cellToWorldspace, landToWorldspace, cellToRefr, topicToInfo, landToCell) =
            EsmFileAnalyzer.BuildAllMaps(parsedRecords, parsedGrups);
        var parsedScan = EsmDataExtractor.ConvertToScanResult(
            parsedRecords,
            isBigEndian,
            cellToWorldspace,
            landToWorldspace,
            cellToRefr,
            topicToInfo,
            landToCell);
        EsmDataExtractor.ExtractRefrRecordsFromParsed(parsedScan, parsedRecords, isBigEndian);

        var descriptorScan = EsmDescriptorScanner.Scan(fileData);
        var scannedRecords = descriptorScan.ScanResult.MainRecords;

        Assert.Equal(
            parsedRecords.Select(r => (r.Header.Signature, r.Header.FormId, r.Offset)),
            scannedRecords.Select(r => (r.RecordType, r.FormId, r.Offset)));

        Assert.Equal(parsedGrups.Count, descriptorScan.GrupHeaders.Count);
        Assert.Equal(
            parsedGrups.Select(g => (g.Offset, g.GroupSize, g.GroupType, Label: Convert.ToHexString(g.Label))),
            descriptorScan.GrupHeaders.Select(g =>
                (g.Offset, g.GroupSize, g.GroupType, Label: Convert.ToHexString(g.Label))));

        Assert.Equal(
            parsedScan.EditorIds.Select(e => e.Name).Order(StringComparer.Ordinal),
            descriptorScan.ScanResult.EditorIds.Select(e => e.Name).Order(StringComparer.Ordinal));
        Assert.Equal(
            parsedScan.FullNames.Select(f => f.Text).Order(StringComparer.Ordinal),
            descriptorScan.ScanResult.FullNames.Select(f => f.Text).Order(StringComparer.Ordinal));
        Assert.Equal(
            parsedScan.NameReferences.Select(n => n.BaseFormId).Order(),
            descriptorScan.ScanResult.NameReferences.Select(n => n.BaseFormId).Order());
        Assert.Equal(parsedScan.Conditions.Count, descriptorScan.ScanResult.Conditions.Count);

        var parsedRefr = Assert.Single(parsedScan.RefrRecords);
        var descriptorRefr = Assert.Single(descriptorScan.ScanResult.RefrRecords);
        Assert.Equal(parsedRefr.Header.FormId, descriptorRefr.Header.FormId);
        Assert.Equal(parsedRefr.BaseFormId, descriptorRefr.BaseFormId);
        Assert.NotNull(descriptorRefr.Position);
        Assert.Equal(-500f, parsedRefr.Radius);
        Assert.Equal(-500f, descriptorRefr.Radius);
        Assert.Equal(0.84f, parsedRefr.Scale);
        Assert.Equal(0.84f, descriptorRefr.Scale);

        Assert.Contains(scannedRecords, r => r.RecordType == "LAND");
        Assert.Contains(scannedRecords, r => r.RecordType == "INFO");
        Assert.Contains(descriptorScan.FormIdMap, kvp => kvp.Key == 0x00001001u && kvp.Value == "WeaponEditorId");
        Assert.Contains(descriptorScan.FormIdMap, kvp => kvp.Key == 0x00001002u && kvp.Value == "CompressedBook");
    }

    [Fact]
    public void WorldExtractor_PreservesSignedReferenceRadius()
    {
        var fileData = BuildSyntheticEsm();
        var scan = EsmDescriptorScanner.Scan(fileData).ScanResult;
        scan.RefrRecords.Clear();
        using var mmf = MemoryMappedFile.CreateNew(null, fileData.Length);
        using var accessor = mmf.CreateViewAccessor(0, fileData.Length);
        accessor.WriteArray(0, fileData, 0, fileData.Length);

        EsmWorldExtractor.ExtractRefrRecords(accessor, fileData.Length, scan);

        var refr = Assert.Single(scan.RefrRecords);
        Assert.Equal(-500f, refr.Radius);
        Assert.Equal(0.84f, refr.Scale);
    }

    private static byte[] BuildSyntheticEsm()
    {
        var weapon = EsmTestFileBuilder.BuildRecord(
            "WEAP",
            0x00001001,
            0,
            ("EDID", NullTerm("WeaponEditorId")),
            ("FULL", NullTerm("Weapon Display")));

        var compressedBook = BuildCompressedRecordLe(
            "BOOK",
            0x00001002,
            ("EDID", NullTerm("CompressedBook")),
            ("FULL", NullTerm("Compressed Book")));

        var land = EsmTestFileBuilder.BuildRecord(
            "LAND",
            0x00002001,
            0,
            ("EDID", NullTerm("LandRecord")),
            ("FULL", NullTerm("Land Display")));

        var dial = EsmTestFileBuilder.BuildRecord(
            "DIAL",
            0x00003001,
            0,
            ("EDID", NullTerm("TopicRecord")),
            ("FULL", NullTerm("Topic Display")));

        var info = EsmTestFileBuilder.BuildRecord(
            "INFO",
            0x00003002,
            0,
            ("EDID", NullTerm("InfoRecord")),
            ("FULL", NullTerm("Info Display")),
            ("CTDA", new byte[24]));

        var dialChildren = BuildFormIdGrup(0x00003001, 7, info);
        var dialGrup = BuildTopLevelGrup("DIAL", dial, dialChildren);

        var worldspace = new EsmTestFileBuilder.WorldspaceData
        {
            FormId = 0x00004001,
            EditorId = "World",
            FullName = "World Display",
            ExteriorCells =
            [
                new EsmTestFileBuilder.CellData
                {
                    FormId = 0x00005001,
                    EditorId = "Cell",
                    GridX = 1,
                    GridY = 2,
                    TemporaryRefs =
                    [
                        new EsmTestFileBuilder.PlacedRefData
                        {
                            RecordType = "REFR",
                            FormId = 0x00006001,
                            BaseFormId = 0x00001001,
                            EditorId = "PlacedWeapon",
                            Scale = 0.84f,
                            Radius = -500f,
                            X = 1,
                            Y = 2,
                            Z = 3
                        }
                    ]
                }
            ]
        };

        return new EsmTestFileBuilder()
            .AddTopLevelGrup("WEAP", weapon)
            .AddTopLevelGrup("BOOK", compressedBook)
            .AddTopLevelGrup("LAND", land)
            .AddRawChunk(dialGrup)
            .AddWorldspace(worldspace)
            .Build();
    }

    private static byte[] BuildCompressedRecordLe(
        string signature,
        uint formId,
        params (string Signature, byte[] Data)[] subrecords)
    {
        var payload = BuildSubrecordPayload(subrecords);
        using var compressedStream = new MemoryStream();
        using (var zlib = new ZLibStream(compressedStream, CompressionLevel.Optimal, true))
        {
            zlib.Write(payload);
        }

        var compressed = compressedStream.ToArray();
        var dataSize = 4 + compressed.Length;
        var record = new byte[EsmParser.MainRecordHeaderSize + dataSize];
        Encoding.ASCII.GetBytes(signature, record);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(4), (uint)dataSize);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(8), CompressedFlag);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(12), formId);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(EsmParser.MainRecordHeaderSize), (uint)payload.Length);
        compressed.CopyTo(record.AsSpan(EsmParser.MainRecordHeaderSize + 4));
        return record;
    }

    private static byte[] BuildSubrecordPayload(params (string Signature, byte[] Data)[] subrecords)
    {
        var payload = new byte[subrecords.Sum(s => EsmParser.SubrecordHeaderSize + s.Data.Length)];
        var offset = 0;
        foreach (var (signature, data) in subrecords)
        {
            Encoding.ASCII.GetBytes(signature, payload.AsSpan(offset));
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(offset + 4), (ushort)data.Length);
            data.CopyTo(payload.AsSpan(offset + EsmParser.SubrecordHeaderSize));
            offset += EsmParser.SubrecordHeaderSize + data.Length;
        }

        return payload;
    }

    private static byte[] BuildTopLevelGrup(string label, params byte[][] chunks)
    {
        var contentLength = chunks.Sum(c => c.Length);
        var grup = new byte[24 + contentLength];
        WriteGrupHeader(grup, (uint)grup.Length, label, 0);
        var offset = 24;
        foreach (var chunk in chunks)
        {
            chunk.CopyTo(grup.AsSpan(offset));
            offset += chunk.Length;
        }

        return grup;
    }

    private static byte[] BuildFormIdGrup(uint label, int groupType, params byte[][] chunks)
    {
        var contentLength = chunks.Sum(c => c.Length);
        var grup = new byte[24 + contentLength];
        Encoding.ASCII.GetBytes("GRUP", grup);
        BinaryPrimitives.WriteUInt32LittleEndian(grup.AsSpan(4), (uint)grup.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(grup.AsSpan(8), label);
        BinaryPrimitives.WriteInt32LittleEndian(grup.AsSpan(12), groupType);
        var offset = 24;
        foreach (var chunk in chunks)
        {
            chunk.CopyTo(grup.AsSpan(offset));
            offset += chunk.Length;
        }

        return grup;
    }

    private static void WriteGrupHeader(byte[] grup, uint size, string label, int groupType)
    {
        Encoding.ASCII.GetBytes("GRUP", grup);
        BinaryPrimitives.WriteUInt32LittleEndian(grup.AsSpan(4), size);
        Encoding.ASCII.GetBytes(label, grup.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(grup.AsSpan(12), groupType);
    }

    private static byte[] NullTerm(string value)
    {
        var bytes = new byte[value.Length + 1];
        Encoding.ASCII.GetBytes(value, bytes);
        return bytes;
    }
}