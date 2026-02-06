using System.IO.MemoryMappedFiles;
using System.Text;
using FalloutXbox360Utils.Core.Formats.EsmRecord;
using FalloutXbox360Utils.Core.Formats.EsmRecord.Models;
using FalloutXbox360Utils.Core.Formats.EsmRecord.Subrecords;
using Xunit;
using Xunit.Abstractions;

namespace FalloutXbox360Utils.Tests.Core.Parsers;

public class NpcParsingTests
{
    private readonly ITestOutputHelper _output;

    public NpcParsingTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void ParseSubrecords_ValidNpcRecord_ExtractsEdidAndFull()
    {
        // Arrange - Create a minimal NPC_ record with EDID and FULL subrecords
        var editorId = "TestNPC";
        var fullName = "Test Character";
        var recordData = CreateNpcRecordData(editorId, fullName, false);

        _output.WriteLine($"Record data length: {recordData.Length}");
        _output.WriteLine($"First 20 bytes: {BitConverter.ToString(recordData.Take(20).ToArray())}");

        // Act - Parse with EsmParser
        var subrecords = EsmParser.ParseSubrecords(recordData);

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

    [Fact]
    public void ParseSubrecords_BigEndianNpcRecord_ExtractsEdidAndFull()
    {
        // Arrange - Create a minimal BE NPC_ record with EDID and FULL subrecords
        var editorId = "TestNPC";
        var fullName = "Test Character";
        var recordData = CreateNpcRecordData(editorId, fullName, true);

        _output.WriteLine($"Record data length: {recordData.Length}");
        _output.WriteLine($"First 20 bytes: {BitConverter.ToString(recordData.Take(20).ToArray())}");

        // Act - Parse with EsmParser in BE mode
        var subrecords = EsmParser.ParseSubrecords(recordData, true);

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

    [Fact]
    public void EnumerateRecords_FullEsmWithNpc_ExtractsNpcData()
    {
        // Arrange - Create a minimal ESM file with TES4 header and one NPC_ record
        var editorId = "TestNPC";
        var fullName = "Test Character";
        var esmData = CreateMinimalEsmWithNpc(editorId, fullName, false);

        _output.WriteLine($"ESM data length: {esmData.Length}");
        _output.WriteLine($"First 30 bytes: {BitConverter.ToString(esmData.Take(30).ToArray())}");

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
        Assert.True(npcRecord.Subrecords.Count >= 2);

        var edidSub = npcRecord.Subrecords.FirstOrDefault(s => s.Signature == "EDID");
        Assert.NotNull(edidSub);
        Assert.Equal(editorId, edidSub.DataAsString);

        var fullSub = npcRecord.Subrecords.FirstOrDefault(s => s.Signature == "FULL");
        Assert.NotNull(fullSub);
        Assert.Equal(fullName, fullSub.DataAsString);
    }

    [Fact]
    public void EnumerateRecords_BigEndianEsmWithNpc_ExtractsNpcData()
    {
        // Arrange - Create a BE ESM file with TES4 header and one NPC_ record
        var editorId = "TestNPC";
        var fullName = "Test Character";
        var esmData = CreateMinimalEsmWithNpc(editorId, fullName, true);

        _output.WriteLine($"ESM data length: {esmData.Length}");
        _output.WriteLine($"First 30 bytes: {BitConverter.ToString(esmData.Take(30).ToArray())}");

        // Verify BE detection
        var isBigEndian = EsmParser.IsBigEndian(esmData);
        _output.WriteLine($"Detected as BigEndian: {isBigEndian}");
        Assert.True(isBigEndian);

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

    private static void WriteSubrecordHeader(BinaryWriter bw, string signature, ushort dataLength, bool bigEndian)
    {
        if (bigEndian)
        {
            // Write signature reversed for BE
            bw.Write((byte)signature[3]);
            bw.Write((byte)signature[2]);
            bw.Write((byte)signature[1]);
            bw.Write((byte)signature[0]);
            // Write length as BE
            bw.Write((byte)(dataLength >> 8));
            bw.Write((byte)(dataLength & 0xFF));
        }
        else
        {
            // Write signature normally
            bw.Write(Encoding.ASCII.GetBytes(signature));
            // Write length as LE
            bw.Write(dataLength);
        }
    }

    private static void WriteRecordHeader(BinaryWriter bw, string signature, uint dataSize, uint flags, uint formId,
        bool bigEndian)
    {
        if (bigEndian)
        {
            // Signature reversed
            bw.Write((byte)signature[3]);
            bw.Write((byte)signature[2]);
            bw.Write((byte)signature[1]);
            bw.Write((byte)signature[0]);
            // Data size as BE
            WriteBigEndianUInt32(bw, dataSize);
            // Flags as BE
            WriteBigEndianUInt32(bw, flags);
            // FormId as BE
            WriteBigEndianUInt32(bw, formId);
            // Version info (8 bytes) - zeros
            bw.Write(0L);
        }
        else
        {
            bw.Write(Encoding.ASCII.GetBytes(signature));
            bw.Write(dataSize);
            bw.Write(flags);
            bw.Write(formId);
            bw.Write(0L); // Version info
        }
    }

    private static void WriteGroupHeader(BinaryWriter bw, int grupSize, string label, bool bigEndian)
    {
        // GRUP header is 24 bytes:
        // - 4 bytes: "GRUP" signature
        // - 4 bytes: total group size (including this header)
        // - 4 bytes: label (record type for type 0)
        // - 4 bytes: group type
        // - 4 bytes: timestamp
        // - 4 bytes: unknown/padding
        if (bigEndian)
        {
            // "GRUP" reversed
            bw.Write((byte)'P');
            bw.Write((byte)'U');
            bw.Write((byte)'R');
            bw.Write((byte)'G');
            // Size as BE
            WriteBigEndianUInt32(bw, (uint)grupSize);
            // Label - record type signature reversed
            bw.Write((byte)label[3]);
            bw.Write((byte)label[2]);
            bw.Write((byte)label[1]);
            bw.Write((byte)label[0]);
            // Group type (4 bytes)
            WriteBigEndianUInt32(bw, 0);
            // Timestamp (4 bytes)
            WriteBigEndianUInt32(bw, 0);
            // Unknown (4 bytes)
            WriteBigEndianUInt32(bw, 0);
        }
        else
        {
            bw.Write(Encoding.ASCII.GetBytes("GRUP"));
            bw.Write(grupSize);
            bw.Write(Encoding.ASCII.GetBytes(label));
            bw.Write(0); // Group type
            bw.Write(0); // Timestamp
            bw.Write(0); // Unknown
        }
    }

    private static void WriteBigEndianUInt32(BinaryWriter bw, uint value)
    {
        bw.Write((byte)((value >> 24) & 0xFF));
        bw.Write((byte)((value >> 16) & 0xFF));
        bw.Write((byte)((value >> 8) & 0xFF));
        bw.Write((byte)(value & 0xFF));
    }

    [Fact]
    public void SemanticReconstructor_FullEsmWithNpc_ReconstructsNpcData()
    {
        // Arrange - Create a minimal ESM file and write to temp file
        var editorId = "TestNPC";
        var fullName = "Test Character";
        var esmData = CreateMinimalEsmWithNpc(editorId, fullName, false);

        var tempFile = Path.Combine(Path.GetTempPath(), $"test_esm_{Guid.NewGuid()}.esm");
        try
        {
            File.WriteAllBytes(tempFile, esmData);

            // Parse the ESM to get records
            var parsedRecords = EsmParser.EnumerateRecords(esmData);
            _output.WriteLine($"Parsed {parsedRecords.Count} records");

            // Convert to EsmRecordScanResult (like EsmFileAnalyzer does)
            var mainRecords = new List<DetectedMainRecord>();
            var editorIds = new List<EdidRecord>();
            var fullNames = new List<TextSubrecord>();

            foreach (var record in parsedRecords)
            {
                var isBigEndian = false;
                mainRecords.Add(new DetectedMainRecord(
                    record.Header.Signature,
                    record.Header.DataSize,
                    record.Header.Flags,
                    record.Header.FormId,
                    record.Offset,
                    isBigEndian));

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

            // Build FormID map (like EsmFileAnalyzer does)
            var formIdMap = new Dictionary<uint, string>();
            foreach (var record in parsedRecords)
            {
                var full = record.Subrecords.FirstOrDefault(s => s.Signature == "FULL")?.DataAsString;
                var edid = record.Subrecords.FirstOrDefault(s => s.Signature == "EDID")?.DataAsString;
                var displayName = !string.IsNullOrEmpty(full) ? full : edid;
                if (!string.IsNullOrEmpty(displayName) && record.Header.FormId != 0)
                    formIdMap[record.Header.FormId] = displayName;
            }

            _output.WriteLine($"FormIdMap: {formIdMap.Count} entries");

            // Create SemanticReconstructor with memory-mapped accessor (like SingleFileTab does)
            using var mmf =
                MemoryMappedFile.CreateFromFile(tempFile, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            using var accessor = mmf.CreateViewAccessor(0, esmData.Length, MemoryMappedFileAccess.Read);

            var reconstructor = new SemanticReconstructor(
                scanResult,
                formIdMap,
                accessor,
                esmData.Length);

            // Act - Reconstruct NPCs
            var npcs = reconstructor.ReconstructNpcs();

            // Assert
            _output.WriteLine($"Reconstructed {npcs.Count} NPCs:");
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

    [Fact]
    public void SemanticReconstructor_BigEndianEsmWithNpc_ReconstructsNpcData()
    {
        // Arrange - Create a BE ESM file and write to temp file
        var editorId = "TestNPC";
        var fullName = "Test Character";
        var esmData = CreateMinimalEsmWithNpc(editorId, fullName, true);

        var tempFile = Path.Combine(Path.GetTempPath(), $"test_esm_be_{Guid.NewGuid()}.esm");
        try
        {
            File.WriteAllBytes(tempFile, esmData);

            // Parse the ESM to get records
            var parsedRecords = EsmParser.EnumerateRecords(esmData);
            _output.WriteLine($"Parsed {parsedRecords.Count} records");

            // Detect endianness
            var isBigEndian = EsmParser.IsBigEndian(esmData);
            _output.WriteLine($"Detected as BigEndian: {isBigEndian}");
            Assert.True(isBigEndian);

            // Convert to EsmRecordScanResult
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
                    isBigEndian)); // Pass correct endianness

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

            // Build FormID map
            var formIdMap = new Dictionary<uint, string>();
            foreach (var record in parsedRecords)
            {
                var full = record.Subrecords.FirstOrDefault(s => s.Signature == "FULL")?.DataAsString;
                var edid = record.Subrecords.FirstOrDefault(s => s.Signature == "EDID")?.DataAsString;
                var displayName = !string.IsNullOrEmpty(full) ? full : edid;
                if (!string.IsNullOrEmpty(displayName) && record.Header.FormId != 0)
                    formIdMap[record.Header.FormId] = displayName;
            }

            _output.WriteLine($"FormIdMap: {formIdMap.Count} entries");

            // Create SemanticReconstructor with memory-mapped accessor
            using var mmf =
                MemoryMappedFile.CreateFromFile(tempFile, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            using var accessor = mmf.CreateViewAccessor(0, esmData.Length, MemoryMappedFileAccess.Read);

            var reconstructor = new SemanticReconstructor(
                scanResult,
                formIdMap,
                accessor,
                esmData.Length);

            // Act - Reconstruct NPCs
            var npcs = reconstructor.ReconstructNpcs();

            // Assert
            _output.WriteLine($"Reconstructed {npcs.Count} NPCs:");
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