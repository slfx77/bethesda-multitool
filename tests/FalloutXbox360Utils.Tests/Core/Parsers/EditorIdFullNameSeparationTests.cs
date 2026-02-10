using System.IO.MemoryMappedFiles;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using Xunit;
using Xunit.Abstractions;

namespace FalloutXbox360Utils.Tests.Core.Parsers;

/// <summary>
///     Tests that the RecordParser keeps EditorId and FullName as separate fields.
///     Regression test for a bug where BuildFormIdMap preferred FULL over EDID, causing
///     the formIdCorrelations passed to RecordParser to contain display names
///     instead of editor IDs, which then leaked into _formIdToEditorId.
/// </summary>
public class EditorIdFullNameSeparationTests
{
    private readonly ITestOutputHelper _output;

    public EditorIdFullNameSeparationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Reconstruction_WithEdidOnlyCorrelations_EditorIdNotEqualFullName()
    {
        // Arrange: Create an ESM with records where EDID != FULL
        var records = new[]
        {
            ("NPC_", "CraigBoone", "Craig Boone", 0x00000001u),
            ("KEYM", "KeyVault13", "Vault 13 Keycard", 0x00000005u)
        };

        var esmData = CreateMinimalEsmWithRecords(records, false);
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_edid_full_{Guid.NewGuid()}.esm");
        try
        {
            File.WriteAllBytes(tempFile, esmData);

            var (scanResult, parsedRecords) = BuildScanResult(esmData);

            // Build EDID-only FormIdMap (the fixed behavior)
            var edidOnlyMap = new Dictionary<uint, string>();
            foreach (var record in parsedRecords)
            {
                var edid = record.Subrecords.FirstOrDefault(s => s.Signature == "EDID")?.DataAsString;
                if (!string.IsNullOrEmpty(edid) && record.Header.FormId != 0)
                {
                    edidOnlyMap[record.Header.FormId] = edid;
                }
            }

            _output.WriteLine("EDID-only FormIdMap entries:");
            foreach (var (formId, name) in edidOnlyMap)
            {
                _output.WriteLine($"  0x{formId:X8} -> {name}");
            }

            // Act: Create reconstructor with EDID-only correlations
            using var mmf = MemoryMappedFile.CreateFromFile(tempFile, FileMode.Open, null, 0,
                MemoryMappedFileAccess.Read);
            using var accessor = mmf.CreateViewAccessor(0, esmData.Length, MemoryMappedFileAccess.Read);

            var reconstructor = new RecordParser(
                scanResult, edidOnlyMap, accessor, esmData.Length);

            // Assert: GetEditorId returns EDID values
            foreach (var (_, expectedEditorId, expectedFullName, formId) in records)
            {
                var actualEditorId = reconstructor.GetEditorId(formId);
                _output.WriteLine(
                    $"  FormId=0x{formId:X8}: EditorId={actualEditorId ?? "(null)"}, Expected={expectedEditorId}");

                Assert.NotNull(actualEditorId);
                Assert.Equal(expectedEditorId, actualEditorId);
                Assert.NotEqual(expectedFullName, actualEditorId);
            }

            // Verify through reconstruction that EditorId != FullName
            var npcs = reconstructor.ReconstructNpcs();
            Assert.Single(npcs);
            Assert.Equal("CraigBoone", npcs[0].EditorId);
            Assert.Equal("Craig Boone", npcs[0].FullName);
            Assert.NotEqual(npcs[0].EditorId, npcs[0].FullName);
            _output.WriteLine(
                $"NPC: EditorId={npcs[0].EditorId}, FullName={npcs[0].FullName} - correctly separated");

            var keys = reconstructor.ReconstructKeys();
            Assert.Single(keys);
            Assert.Equal("KeyVault13", keys[0].EditorId);
            Assert.Equal("Vault 13 Keycard", keys[0].FullName);
            Assert.NotEqual(keys[0].EditorId, keys[0].FullName);
            _output.WriteLine(
                $"Key: EditorId={keys[0].EditorId}, FullName={keys[0].FullName} - correctly separated");
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void Reconstruction_WithFullNamePreferredCorrelations_CausesEditorIdSwap()
    {
        // This test documents the bug: when formIdCorrelations contains FULL (display names)
        // instead of EDID, the reconstructor's _formIdToEditorId is contaminated.
        var records = new[]
        {
            ("KEYM", "KeyVault13", "Vault 13 Keycard", 0x00000005u)
        };

        var esmData = CreateMinimalEsmWithRecords(records, false);
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_buggy_{Guid.NewGuid()}.esm");
        try
        {
            File.WriteAllBytes(tempFile, esmData);

            var (scanResult, parsedRecords) = BuildScanResult(esmData);

            // Build the OLD buggy map that prefers FULL over EDID
            var buggyMap = new Dictionary<uint, string>();
            foreach (var record in parsedRecords)
            {
                var full = record.Subrecords.FirstOrDefault(s => s.Signature == "FULL")?.DataAsString;
                var edid = record.Subrecords.FirstOrDefault(s => s.Signature == "EDID")?.DataAsString;
                var displayName = !string.IsNullOrEmpty(full) ? full : edid;
                if (!string.IsNullOrEmpty(displayName) && record.Header.FormId != 0)
                {
                    buggyMap[record.Header.FormId] = displayName;
                }
            }

            _output.WriteLine("Buggy FormIdMap entries (FULL preferred):");
            foreach (var (formId, name) in buggyMap)
            {
                _output.WriteLine($"  0x{formId:X8} -> {name}");
            }

            // Act: Create reconstructor with buggy correlations
            using var mmf = MemoryMappedFile.CreateFromFile(tempFile, FileMode.Open, null, 0,
                MemoryMappedFileAccess.Read);
            using var accessor = mmf.CreateViewAccessor(0, esmData.Length, MemoryMappedFileAccess.Read);

            var reconstructor = new RecordParser(
                scanResult, buggyMap, accessor, esmData.Length);

            // With the buggy map, GetEditorId returns the FullName instead of EditorId
            var editorId = reconstructor.GetEditorId(0x00000005);
            _output.WriteLine($"GetEditorId(0x00000005) = {editorId ?? "(null)"}");

            // This demonstrates the bug: GetEditorId returns FullName text
            Assert.Equal("Vault 13 Keycard", editorId);

            // For simple record types (KEYM), EditorId comes from GetEditorId() — NOT subrecord
            // parsing. So the buggy map poisons the reconstructed record's EditorId too.
            var keys = reconstructor.ReconstructKeys();
            Assert.Single(keys);
            // Bug: EditorId == FullName because GetEditorId returned the display name
            Assert.Equal("Vault 13 Keycard", keys[0].EditorId);
            Assert.Equal(keys[0].EditorId, keys[0].FullName);
            _output.WriteLine(
                $"Key: EditorId={keys[0].EditorId}, FullName={keys[0].FullName} " +
                "- BUG: both are the same because GetEditorId() returns FullName");
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    #region Test Helpers

    private static (EsmRecordScanResult ScanResult, List<ParsedMainRecord> ParsedRecords) BuildScanResult(
        byte[] esmData)
    {
        var parsedRecords = EsmParser.EnumerateRecords(esmData);
        var mainRecords = new List<DetectedMainRecord>();
        var editorIds = new List<EdidRecord>();
        var fullNames = new List<TextSubrecord>();

        foreach (var record in parsedRecords)
        {
            mainRecords.Add(new DetectedMainRecord(
                record.Header.Signature, record.Header.DataSize,
                record.Header.Flags, record.Header.FormId,
                record.Offset, false));

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

        return (scanResult, parsedRecords);
    }

    private static byte[] CreateMinimalEsmWithRecords(
        (string Signature, string EditorId, string FullName, uint FormId)[] records,
        bool bigEndian)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // TES4 header
        WriteRecordHeader(bw, "TES4", 0, 0, 0, bigEndian);

        // One GRUP per record type
        foreach (var group in records.GroupBy(r => r.Signature))
        {
            var recordsData = new List<byte[]>();
            foreach (var rec in group)
            {
                var subData = CreateRecordData(rec.EditorId, rec.FullName, bigEndian);
                recordsData.Add(subData);
            }

            var grupSize = 24; // GRUP header
            foreach (var (_, data) in group.Zip(recordsData))
            {
                grupSize += 24 + data.Length; // Record header + data
            }

            WriteGroupHeader(bw, grupSize, group.Key, bigEndian);

            foreach (var (rec, data) in group.Zip(recordsData))
            {
                WriteRecordHeader(bw, rec.Signature, (uint)data.Length, 0, rec.FormId, bigEndian);
                bw.Write(data);
            }
        }

        return ms.ToArray();
    }

    private static byte[] CreateRecordData(string editorId, string fullName, bool bigEndian)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        var edidBytes = Encoding.UTF8.GetBytes(editorId + "\0");
        WriteSubrecordHeader(bw, "EDID", (ushort)edidBytes.Length, bigEndian);
        bw.Write(edidBytes);

        var fullBytes = Encoding.UTF8.GetBytes(fullName + "\0");
        WriteSubrecordHeader(bw, "FULL", (ushort)fullBytes.Length, bigEndian);
        bw.Write(fullBytes);

        return ms.ToArray();
    }

    private static void WriteSubrecordHeader(BinaryWriter bw, string signature, ushort dataLength, bool bigEndian)
    {
        if (bigEndian)
        {
            bw.Write((byte)signature[3]);
            bw.Write((byte)signature[2]);
            bw.Write((byte)signature[1]);
            bw.Write((byte)signature[0]);
            bw.Write((byte)(dataLength >> 8));
            bw.Write((byte)(dataLength & 0xFF));
        }
        else
        {
            bw.Write(Encoding.ASCII.GetBytes(signature));
            bw.Write(dataLength);
        }
    }

    private static void WriteRecordHeader(BinaryWriter bw, string signature, uint dataSize, uint flags, uint formId,
        bool bigEndian)
    {
        if (bigEndian)
        {
            bw.Write((byte)signature[3]);
            bw.Write((byte)signature[2]);
            bw.Write((byte)signature[1]);
            bw.Write((byte)signature[0]);
            WriteBE(bw, dataSize);
            WriteBE(bw, flags);
            WriteBE(bw, formId);
            bw.Write(0L);
        }
        else
        {
            bw.Write(Encoding.ASCII.GetBytes(signature));
            bw.Write(dataSize);
            bw.Write(flags);
            bw.Write(formId);
            bw.Write(0L);
        }
    }

    private static void WriteGroupHeader(BinaryWriter bw, int grupSize, string label, bool bigEndian)
    {
        if (bigEndian)
        {
            bw.Write((byte)'P');
            bw.Write((byte)'U');
            bw.Write((byte)'R');
            bw.Write((byte)'G');
            WriteBE(bw, (uint)grupSize);
            bw.Write((byte)label[3]);
            bw.Write((byte)label[2]);
            bw.Write((byte)label[1]);
            bw.Write((byte)label[0]);
            WriteBE(bw, 0u);
            WriteBE(bw, 0u);
            WriteBE(bw, 0u);
        }
        else
        {
            bw.Write(Encoding.ASCII.GetBytes("GRUP"));
            bw.Write(grupSize);
            bw.Write(Encoding.ASCII.GetBytes(label));
            bw.Write(0);
            bw.Write(0);
            bw.Write(0);
        }
    }

    private static void WriteBE(BinaryWriter bw, uint value)
    {
        bw.Write((byte)((value >> 24) & 0xFF));
        bw.Write((byte)((value >> 16) & 0xFF));
        bw.Write((byte)((value >> 8) & 0xFF));
        bw.Write((byte)(value & 0xFF));
    }

    #endregion
}
