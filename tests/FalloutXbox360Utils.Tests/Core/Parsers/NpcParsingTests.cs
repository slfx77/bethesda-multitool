using System.IO.MemoryMappedFiles;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using Xunit;
using static FalloutXbox360Utils.Tests.Helpers.BinaryTestWriter;
using static FalloutXbox360Utils.Tests.Helpers.EsmTestRecordBuilder;

namespace FalloutXbox360Utils.Tests.Core.Parsers;

public class NpcParsingTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParseSubrecords_NpcRecord_ExtractsEdidAndFull(bool bigEndian)
    {
        // Arrange - Create a minimal NPC_ record with EDID and FULL subrecords
        var editorId = "TestNPC";
        var fullName = "Test Character";
        var recordData = CreateNpcRecordData(editorId, fullName, bigEndian);

        _output.WriteLine($"Record data length: {recordData.Length}");
        _output.WriteLine($"First 20 bytes: {BitConverter.ToString(recordData.Take(20).ToArray())}");

        // Act - Parse with EsmParser
        var subrecords = EsmParser.ParseSubrecords(recordData, bigEndian);

        // Assert
        _output.WriteLine($"Found {subrecords.Count} subrecords:");
        foreach (var sub in subrecords)
        {
            _output.WriteLine($"  {sub.Signature}: {sub.Data.Length} bytes");
        }

        Assert.True(subrecords.Count >= 2, $"Expected at least 2 subrecords, got {subrecords.Count}");

        var edidSub = subrecords.FirstOrDefault(s => s.Signature == "EDID");
        Assert.NotNull(edidSub);
        Assert.Equal(editorId, edidSub.DataAsString);

        var fullSub = subrecords.FirstOrDefault(s => s.Signature == "FULL");
        Assert.NotNull(fullSub);
        Assert.Equal(fullName, fullSub.DataAsString);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnumerateRecords_EsmWithNpc_ExtractsNpcData(bool bigEndian)
    {
        // Arrange - Create a minimal ESM file with TES4 header and one NPC_ record
        var editorId = "TestNPC";
        var fullName = "Test Character";
        var esmData = CreateMinimalEsmWithNpc(editorId, fullName, bigEndian);

        _output.WriteLine($"ESM data length: {esmData.Length}");
        _output.WriteLine($"First 30 bytes: {BitConverter.ToString(esmData.Take(30).ToArray())}");

        // Verify endianness detection
        var detectedBigEndian = EsmParser.IsBigEndian(esmData);
        _output.WriteLine($"Detected as BigEndian: {detectedBigEndian}");
        Assert.Equal(bigEndian, detectedBigEndian);

        // Act
        var records = EsmParser.EnumerateRecords(esmData);

        // Assert
        _output.WriteLine($"Found {records.Count} records:");
        foreach (var rec in records)
        {
            _output.WriteLine(
                $"  {rec.Header.Signature} at offset {rec.Offset}: FormId=0x{rec.Header.FormId:X8}, {rec.Subrecords.Count} subrecords");
            foreach (var sub in rec.Subrecords.Take(3))
            {
                _output.WriteLine($"    {sub.Signature}: {sub.Data.Length} bytes");
            }
        }

        var npcRecord = records.FirstOrDefault(r => r.Header.Signature == "NPC_");
        Assert.NotNull(npcRecord);
        Assert.True(npcRecord.Subrecords.Count >= 2, $"Expected >= 2 subrecords, got {npcRecord.Subrecords.Count}");

        var edidSub = npcRecord.Subrecords.FirstOrDefault(s => s.Signature == "EDID");
        Assert.NotNull(edidSub);
        Assert.Equal(editorId, edidSub.DataAsString);

        var fullSub = npcRecord.Subrecords.FirstOrDefault(s => s.Signature == "FULL");
        Assert.NotNull(fullSub);
        Assert.Equal(fullName, fullSub.DataAsString);
    }

    /// <summary>
    ///     Creates NPC record data (just the subrecords portion, not the header).
    /// </summary>
    private static byte[] CreateNpcRecordData(string editorId, string fullName, bool bigEndian)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // EDID subrecord
        var edidBytes = Encoding.UTF8.GetBytes(editorId + "\0");
        WriteSubrecordHeader(bw, "EDID", (ushort)edidBytes.Length, bigEndian);
        bw.Write(edidBytes);

        // FULL subrecord
        var fullBytes = Encoding.UTF8.GetBytes(fullName + "\0");
        WriteSubrecordHeader(bw, "FULL", (ushort)fullBytes.Length, bigEndian);
        bw.Write(fullBytes);

        return ms.ToArray();
    }

    /// <summary>
    ///     Creates a minimal ESM file with TES4 header, NPC_ GRUP, and one NPC_ record.
    /// </summary>
    private static byte[] CreateMinimalEsmWithNpc(string editorId, string fullName, bool bigEndian)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // TES4 header (minimal, 0 data size)
        WriteRecordHeader(bw, "TES4", 0, 0, 0, bigEndian);

        // NPC_ subrecord data
        var npcData = CreateNpcRecordData(editorId, fullName, bigEndian);

        // GRUP header for NPC_ records
        var grupSize = 24 + 24 + npcData.Length; // GRUP header + NPC_ header + NPC_ data
        WriteGroupHeader(bw, grupSize, "NPC_", bigEndian);

        // NPC_ record header
        WriteRecordHeader(bw, "NPC_", (uint)npcData.Length, 0, 0x00000001, bigEndian);

        // NPC_ record data
        bw.Write(npcData);

        return ms.ToArray();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecordParser_EsmWithNpc_ParsesNpcData(bool bigEndian)
    {
        // Arrange - Create an ESM file and write to temp file
        var editorId = "TestNPC";
        var fullName = "Test Character";
        var esmData = CreateMinimalEsmWithNpc(editorId, fullName, bigEndian);

        var tempFile = Path.Combine(Path.GetTempPath(), $"test_esm_{Guid.NewGuid()}.esm");
        try
        {
            File.WriteAllBytes(tempFile, esmData);

            // Parse the ESM to get records
            var parsedRecords = EsmParser.EnumerateRecords(esmData);
            _output.WriteLine($"Parsed {parsedRecords.Count} records");

            // Verify endianness detection
            var detectedBigEndian = EsmParser.IsBigEndian(esmData);
            _output.WriteLine($"Detected as BigEndian: {detectedBigEndian}");
            Assert.Equal(bigEndian, detectedBigEndian);

            // Convert to EsmRecordScanResult (like EsmFileAnalyzer does)
            var mainRecords = new List<DetectedMainRecord>();
            var editorIds = new List<EdidRecord>();
            var fullNames = new List<TextSubrecord>();

            foreach (var record in parsedRecords)
            {
                mainRecords.Add(new DetectedMainRecord(
                    record.Header.Signature,
                    record.Header.DataSize,
                    record.Header.Flags,
                    record.Header.FormId,
                    record.Offset,
                    bigEndian));

                foreach (var sub in record.Subrecords)
                {
                    if (sub.Signature == "EDID")
                        editorIds.Add(new EdidRecord(sub.DataAsString ?? "", record.Offset));
                    if (sub.Signature == "FULL")
                        fullNames.Add(new TextSubrecord("FULL", sub.DataAsString ?? "", record.Offset));
                }
            }

            var scanResult = new EsmRecordScanResult
            {
                MainRecords = mainRecords,
                EditorIds = editorIds,
                FullNames = fullNames
            };

            _output.WriteLine(
                $"MainRecords: {mainRecords.Count}, EditorIds: {editorIds.Count}, FullNames: {fullNames.Count}");

            // Build FormID map (like EsmFileAnalyzer does -- EDID only)
            var formIdMap = new Dictionary<uint, string>();
            foreach (var record in parsedRecords)
            {
                var edid = record.Subrecords.FirstOrDefault(s => s.Signature == "EDID")?.DataAsString;
                if (!string.IsNullOrEmpty(edid) && record.Header.FormId != 0)
                    formIdMap[record.Header.FormId] = edid;
            }

            _output.WriteLine($"FormIdMap: {formIdMap.Count} entries");

            // Create RecordParser with memory-mapped accessor (like SingleFileTab does)
            using var mmf =
                MemoryMappedFile.CreateFromFile(tempFile, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            using var accessor = mmf.CreateViewAccessor(0, esmData.Length, MemoryMappedFileAccess.Read);

            var parser = new RecordParser(
                scanResult,
                formIdMap,
                accessor,
                esmData.Length);

            // Act - Parse NPCs
            var npcs = parser.ParseNpcs();

            // Assert
            _output.WriteLine($"Parsed {npcs.Count} NPCs:");
            foreach (var npc in npcs)
            {
                _output.WriteLine(
                    $"  FormId=0x{npc.FormId:X8}, EditorId={npc.EditorId ?? "(null)"}, FullName={npc.FullName ?? "(null)"}, Stats={npc.Stats != null}");
            }

            Assert.Single(npcs);
            Assert.Equal(editorId, npcs[0].EditorId);
            Assert.Equal(fullName, npcs[0].FullName);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }
}