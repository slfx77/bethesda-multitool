using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Parsers;

/// <summary>
///     Regression tests for RecordParser reconstruction methods.
///     Uses synthetic scan results to test without requiring sample files.
///     These tests anchor behavior before the partial-class-to-handler refactoring.
/// </summary>
public class RecordParserHandlerTests(ITestOutputHelper output, SampleFileFixture samples)
{
    private readonly ITestOutputHelper _output = output;

    #region Test Helpers

    /// <summary>
    ///     Creates a minimal EsmRecordScanResult with the given main records.
    /// </summary>
    private static EsmRecordScanResult MakeScanResult(
        List<DetectedMainRecord>? mainRecords = null,
        List<EdidRecord>? editorIds = null,
        List<TextSubrecord>? fullNames = null,
        List<ActorBaseSubrecord>? actorBases = null,
        List<RuntimeEditorIdEntry>? runtimeEditorIds = null,
        Dictionary<uint, List<uint>>? topicToInfoMap = null)
    {
        return new EsmRecordScanResult
        {
            MainRecords = mainRecords ?? [],
            EditorIds = editorIds ?? [],
            FullNames = fullNames ?? [],
            ActorBases = actorBases ?? [],
            RuntimeEditorIds = runtimeEditorIds ?? [],
            TopicToInfoMap = topicToInfoMap ?? []
        };
    }

    /// <summary>
    ///     Creates a DetectedMainRecord with sensible defaults.
    /// </summary>
    private static DetectedMainRecord MakeRecord(string type, uint formId, long offset, uint dataSize = 100,
        bool isBigEndian = false, uint flags = 0)
    {
        return new DetectedMainRecord(type, dataSize, flags, formId, offset, isBigEndian);
    }

    /// <summary>
    ///     Builds a byte buffer containing ESM record data with subrecords.
    ///     Returns the buffer suitable for memory-mapped access.
    ///     Layout: [24-byte record header] [subrecord data...]
    /// </summary>
    private static byte[] BuildRecordBytes(uint formId, string recordType, bool bigEndian,
        params (string sig, byte[] data)[] subrecords)
    {
        // Calculate total subrecord data size
        var dataSize = 0;
        foreach (var (_, data) in subrecords)
        {
            dataSize += 6 + data.Length; // 4 sig + 2 size + data
        }

        var totalSize = 24 + dataSize; // 24-byte record header + data
        var buffer = new byte[totalSize];

        // Write record header
        var sigBytes = Encoding.ASCII.GetBytes(recordType);
        if (bigEndian)
        {
            // Reverse signature for big-endian
            buffer[0] = sigBytes[3];
            buffer[1] = sigBytes[2];
            buffer[2] = sigBytes[1];
            buffer[3] = sigBytes[0];
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(4), (uint)dataSize);
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(8), 0); // flags
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(12), formId);
        }
        else
        {
            Array.Copy(sigBytes, buffer, 4);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), (uint)dataSize);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8), 0); // flags
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12), formId);
        }

        // Write subrecords
        var offset = 24;
        foreach (var (sig, data) in subrecords)
        {
            var subSigBytes = Encoding.ASCII.GetBytes(sig);
            if (bigEndian)
            {
                buffer[offset] = subSigBytes[3];
                buffer[offset + 1] = subSigBytes[2];
                buffer[offset + 2] = subSigBytes[1];
                buffer[offset + 3] = subSigBytes[0];
                BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset + 4), (ushort)data.Length);
            }
            else
            {
                Array.Copy(subSigBytes, 0, buffer, offset, 4);
                BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset + 4), (ushort)data.Length);
            }

            Array.Copy(data, 0, buffer, offset + 6, data.Length);
            offset += 6 + data.Length;
        }

        return buffer;
    }

    /// <summary>
    ///     Creates a null-terminated ASCII string as bytes.
    /// </summary>
    private static byte[] NullTermString(string s)
    {
        var bytes = new byte[s.Length + 1];
        Encoding.ASCII.GetBytes(s, bytes);
        return bytes;
    }

    #endregion

    #region Constructor / Lookup Tests

    [Fact]
    public void Constructor_BuildsFormIdToEditorIdMap_FromEdidSubrecords()
    {
        var mainRecords = new List<DetectedMainRecord>
        {
            MakeRecord("NPC_", 0x00001234, 100, dataSize: 50),
            MakeRecord("WEAP", 0x00005678, 200, dataSize: 50)
        };

        var editorIds = new List<EdidRecord>
        {
            new("TestNpc", 110), // Within NPC_ record bounds (offset 100, size 50+24=174)
            new("TestWeapon", 210) // Within WEAP record bounds
        };

        var scanResult = MakeScanResult(mainRecords: mainRecords, editorIds: editorIds);
        var parser = new RecordParser(scanResult);

        Assert.Equal("TestNpc", parser.GetEditorId(0x00001234));
        Assert.Equal("TestWeapon", parser.GetEditorId(0x00005678));
    }

    [Fact]
    public void Constructor_InjectsWellKnownFormIds()
    {
        var scanResult = MakeScanResult();
        var parser = new RecordParser(scanResult);

        Assert.Equal("PlayerRef", parser.GetEditorId(0x00000007));
        Assert.Equal("Player", parser.GetEditorId(0x00000014));
    }

    [Fact]
    public void Constructor_UsesFormIdCorrelations_WhenProvided()
    {
        var correlations = new Dictionary<uint, string>
        {
            { 0x00001111, "ProvidedEditorId" }
        };

        var scanResult = MakeScanResult(mainRecords: [MakeRecord("NPC_", 0x00001111, 0)]);
        var parser = new RecordParser(scanResult, formIdCorrelations: correlations);

        Assert.Equal("ProvidedEditorId", parser.GetEditorId(0x00001111));
    }

    [Fact]
    public void Constructor_MergesRuntimeEditorIds()
    {
        var runtimeIds = new List<RuntimeEditorIdEntry>
        {
            new() { FormId = 0x00009999, EditorId = "RuntimeNpc", FormType = 0x2A }
        };

        var scanResult = MakeScanResult(runtimeEditorIds: runtimeIds);
        var parser = new RecordParser(scanResult);

        Assert.Equal("RuntimeNpc", parser.GetEditorId(0x00009999));
    }

    [Fact]
    public void GetFormId_ReturnsCorrectReverseLookup()
    {
        var correlations = new Dictionary<uint, string>
        {
            { 0x00001234, "MyEditorId" }
        };

        var scanResult = MakeScanResult();
        var parser = new RecordParser(scanResult, formIdCorrelations: correlations);

        Assert.Equal(0x00001234u, parser.GetFormId("MyEditorId"));
    }

    [Fact]
    public void GetRecord_ReturnsCorrectRecord()
    {
        var records = new List<DetectedMainRecord>
        {
            MakeRecord("NPC_", 0x00001234, 100),
            MakeRecord("WEAP", 0x00005678, 200)
        };

        var scanResult = MakeScanResult(mainRecords: records);
        var parser = new RecordParser(scanResult);

        var record = parser.GetRecord(0x00001234);
        Assert.NotNull(record);
        Assert.Equal("NPC_", record.RecordType);
    }

    [Fact]
    public void GetRecord_ReturnsNull_ForUnknownFormId()
    {
        var scanResult = MakeScanResult();
        var parser = new RecordParser(scanResult);

        Assert.Null(parser.GetRecord(0xDEADBEEF));
    }

    #endregion

    #region Reconstruction Without Accessor Tests

    [Fact]
    public void ReconstructNpcs_WithoutAccessor_ReturnsBasicRecords()
    {
        var records = new List<DetectedMainRecord>
        {
            MakeRecord("NPC_", 0x00001234, 100),
            MakeRecord("NPC_", 0x00005678, 300)
        };

        var correlations = new Dictionary<uint, string>
        {
            { 0x00001234, "TestNpc1" },
            { 0x00005678, "TestNpc2" }
        };

        var scanResult = MakeScanResult(mainRecords: records);
        var parser = new RecordParser(scanResult, formIdCorrelations: correlations);
        var npcs = parser.ReconstructNpcs();

        Assert.Equal(2, npcs.Count);
        Assert.Equal(0x00001234u, npcs[0].FormId);
        Assert.Equal("TestNpc1", npcs[0].EditorId);
    }

    [Fact]
    public void ReconstructCreatures_WithoutAccessor_ReturnsBasicRecords()
    {
        var records = new List<DetectedMainRecord>
        {
            MakeRecord("CREA", 0x000AAAA1, 100)
        };

        var correlations = new Dictionary<uint, string>
        {
            { 0x000AAAA1, "TestCreature" }
        };

        var scanResult = MakeScanResult(mainRecords: records);
        var parser = new RecordParser(scanResult, formIdCorrelations: correlations);
        var creatures = parser.ReconstructCreatures();

        Assert.Single(creatures);
        Assert.Equal(0x000AAAA1u, creatures[0].FormId);
        Assert.Equal("TestCreature", creatures[0].EditorId);
    }

    [Fact]
    public void ReconstructTerminals_WithoutAccessor_ReturnsBasicRecords()
    {
        var records = new List<DetectedMainRecord>
        {
            MakeRecord("TERM", 0x000B0001, 100)
        };

        var fullNames = new List<TextSubrecord>
        {
            new("FULL", "Terminal Alpha", 120)
        };

        var correlations = new Dictionary<uint, string>
        {
            { 0x000B0001, "TestTerminal" }
        };

        var scanResult = MakeScanResult(mainRecords: records, fullNames: fullNames);
        var parser = new RecordParser(scanResult, formIdCorrelations: correlations);
        var terminals = parser.ReconstructTerminals();

        Assert.Single(terminals);
        Assert.Equal("TestTerminal", terminals[0].EditorId);
        Assert.Equal("Terminal Alpha", terminals[0].FullName);
    }

    [Fact]
    public void ReconstructPackages_WithoutAccessor_ReturnsBasicRecords()
    {
        var records = new List<DetectedMainRecord>
        {
            MakeRecord("PACK", 0x000C0001, 100)
        };

        var scanResult = MakeScanResult(mainRecords: records);
        var parser = new RecordParser(scanResult);
        var packages = parser.ReconstructPackages();

        Assert.Single(packages);
        Assert.Equal(0x000C0001u, packages[0].FormId);
    }

    [Fact]
    public void ReconstructGlobals_WithoutAccessor_ReturnsEmpty()
    {
        // Globals require accessor to read data - should return empty without one
        var records = new List<DetectedMainRecord>
        {
            MakeRecord("GLOB", 0x000D0001, 100)
        };

        var scanResult = MakeScanResult(mainRecords: records);
        var parser = new RecordParser(scanResult);
        var globals = parser.ReconstructGlobals();

        Assert.Empty(globals);
    }

    #endregion

    #region Reconstruction With Accessor Tests

    [Fact]
    public void ReconstructBooks_WithAccessor_ParsesSubrecords()
    {
        // Build a BOOK record with EDID, FULL, DESC subrecords
        var edidData = NullTermString("TestBook01");
        var fullData = NullTermString("Test Book Title");
        var descData = NullTermString("This is the book text.");

        // DATA subrecord: flags(1) + skill(1) + value(int32) + weight(float) = 10 bytes
        var dataField = new byte[10];
        dataField[0] = 0x01; // flags
        dataField[1] = 0x00; // skill
        BinaryPrimitives.WriteInt32LittleEndian(dataField.AsSpan(2), 25); // value
        BinaryPrimitives.WriteSingleLittleEndian(dataField.AsSpan(6), 1.5f); // weight

        var recordBytes = BuildRecordBytes(0x00010001, "BOOK", false,
            ("EDID", edidData),
            ("FULL", fullData),
            ("DESC", descData),
            ("DATA", dataField));

        var mainRecord = new DetectedMainRecord("BOOK",
            (uint)(recordBytes.Length - 24), 0, 0x00010001, 0, false);

        var scanResult = MakeScanResult(mainRecords: [mainRecord]);

        using var mmf = MemoryMappedFile.CreateNew(null, recordBytes.Length);
        using var accessor = mmf.CreateViewAccessor(0, recordBytes.Length);
        accessor.WriteArray(0, recordBytes, 0, recordBytes.Length);

        var parser = new RecordParser(scanResult, accessor: accessor, fileSize: recordBytes.Length);
        var books = parser.ReconstructBooks();

        Assert.Single(books);
        var book = books[0];
        Assert.Equal("TestBook01", book.EditorId);
        Assert.Equal("Test Book Title", book.FullName);
        Assert.Equal("This is the book text.", book.Text);
        Assert.Equal(25, book.Value);
        Assert.Equal(1.5f, book.Weight);
        Assert.Equal(0x01, book.Flags);
    }

    [Fact]
    public void ReconstructNotes_WithAccessor_ParsesSubrecords()
    {
        var edidData = NullTermString("TestNote01");
        var fullData = NullTermString("Test Note");
        var tnamData = NullTermString("Note contents here");
        var dataField = new byte[] { 0x03 }; // Note type = holotape

        var recordBytes = BuildRecordBytes(0x00020001, "NOTE", false,
            ("EDID", edidData),
            ("FULL", fullData),
            ("DATA", dataField),
            ("TNAM", tnamData));

        var mainRecord = new DetectedMainRecord("NOTE",
            (uint)(recordBytes.Length - 24), 0, 0x00020001, 0, false);

        var scanResult = MakeScanResult(mainRecords: [mainRecord]);

        using var mmf = MemoryMappedFile.CreateNew(null, recordBytes.Length);
        using var accessor = mmf.CreateViewAccessor(0, recordBytes.Length);
        accessor.WriteArray(0, recordBytes, 0, recordBytes.Length);

        var parser = new RecordParser(scanResult, accessor: accessor, fileSize: recordBytes.Length);
        var notes = parser.ReconstructNotes();

        Assert.Single(notes);
        var note = notes[0];
        Assert.Equal("TestNote01", note.EditorId);
        Assert.Equal("Test Note", note.FullName);
        Assert.Equal("Note contents here", note.Text);
        Assert.Equal(3, note.NoteType);
    }

    [Fact]
    public void ReconstructMessages_WithAccessor_ParsesSubrecords()
    {
        var edidData = NullTermString("TestMsg01");
        var fullData = NullTermString("Message Title");
        var descData = NullTermString("Message body text");
        var itxtData1 = NullTermString("OK");
        var itxtData2 = NullTermString("Cancel");

        var recordBytes = BuildRecordBytes(0x00030001, "MESG", false,
            ("EDID", edidData),
            ("FULL", fullData),
            ("DESC", descData),
            ("ITXT", itxtData1),
            ("ITXT", itxtData2));

        var mainRecord = new DetectedMainRecord("MESG",
            (uint)(recordBytes.Length - 24), 0, 0x00030001, 0, false);

        var scanResult = MakeScanResult(mainRecords: [mainRecord]);

        using var mmf = MemoryMappedFile.CreateNew(null, recordBytes.Length);
        using var accessor = mmf.CreateViewAccessor(0, recordBytes.Length);
        accessor.WriteArray(0, recordBytes, 0, recordBytes.Length);

        var parser = new RecordParser(scanResult, accessor: accessor, fileSize: recordBytes.Length);
        var messages = parser.ReconstructMessages();

        Assert.Single(messages);
        var msg = messages[0];
        Assert.Equal("TestMsg01", msg.EditorId);
        Assert.Equal("Message Title", msg.FullName);
        Assert.Equal("Message body text", msg.Description);
        Assert.Equal(2, msg.Buttons.Count);
        Assert.Contains("OK", msg.Buttons);
        Assert.Contains("Cancel", msg.Buttons);
    }

    [Fact]
    public void ReconstructGlobals_WithAccessor_ParsesSubrecords()
    {
        var edidData = NullTermString("TimeScale");

        // FNAM: value type 'f' (float)
        var fnamData = new byte[] { (byte)'f' };

        // FLTV: float value 30.0
        var fltvData = new byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(fltvData, 30.0f);

        var recordBytes = BuildRecordBytes(0x00040001, "GLOB", false,
            ("EDID", edidData),
            ("FNAM", fnamData),
            ("FLTV", fltvData));

        var mainRecord = new DetectedMainRecord("GLOB",
            (uint)(recordBytes.Length - 24), 0, 0x00040001, 0, false);

        var scanResult = MakeScanResult(mainRecords: [mainRecord]);

        using var mmf = MemoryMappedFile.CreateNew(null, recordBytes.Length);
        using var accessor = mmf.CreateViewAccessor(0, recordBytes.Length);
        accessor.WriteArray(0, recordBytes, 0, recordBytes.Length);

        var parser = new RecordParser(scanResult, accessor: accessor, fileSize: recordBytes.Length);
        var globals = parser.ReconstructGlobals();

        Assert.Single(globals);
        var global = globals[0];
        Assert.Equal("TimeScale", global.EditorId);
        Assert.Equal('f', global.ValueType);
        Assert.Equal(30.0f, global.Value);
    }

    #endregion

    #region ReconstructAll Tests

    [Fact]
    public void ReconstructAll_EmptyScanResult_ReturnsEmptyCollection()
    {
        var scanResult = MakeScanResult();
        var parser = new RecordParser(scanResult);
        var result = parser.ReconstructAll();

        Assert.NotNull(result);
        Assert.Empty(result.Npcs);
        Assert.Empty(result.Weapons);
        Assert.Empty(result.Dialogues);
        Assert.Equal(0, result.TotalRecordsReconstructed);
    }

    [Fact]
    public void ReconstructAll_WithMultipleTypes_ReturnsPopulatedCollection()
    {
        var records = new List<DetectedMainRecord>
        {
            MakeRecord("NPC_", 0x00001001, 100),
            MakeRecord("CREA", 0x00002001, 300),
            MakeRecord("WEAP", 0x00003001, 500),
            MakeRecord("PACK", 0x00004001, 700),
            MakeRecord("TERM", 0x00005001, 900)
        };

        var correlations = new Dictionary<uint, string>
        {
            { 0x00001001, "TestNpc" },
            { 0x00002001, "TestCreature" },
            { 0x00003001, "TestWeapon" },
            { 0x00004001, "TestPackage" },
            { 0x00005001, "TestTerminal" }
        };

        var scanResult = MakeScanResult(mainRecords: records);
        var parser = new RecordParser(scanResult, formIdCorrelations: correlations);
        var result = parser.ReconstructAll();

        Assert.Single(result.Npcs);
        Assert.Single(result.Creatures);
        Assert.Single(result.Packages);
        Assert.Single(result.Terminals);
        Assert.True(result.TotalRecordsReconstructed >= 4);

        // Verify EditorID maps are populated in the result
        Assert.True(result.FormIdToEditorId.ContainsKey(0x00001001));
    }

    [Fact]
    public void ReconstructAll_PreservesMethodOrdering_EditorIdEnrichment()
    {
        // Reconstruction methods enrich _formIdToEditorId as they go.
        // Verify that later methods can see EditorIDs added by earlier ones.
        var records = new List<DetectedMainRecord>
        {
            MakeRecord("NPC_", 0x00001001, 100),
            MakeRecord("WEAP", 0x00003001, 300)
        };

        var correlations = new Dictionary<uint, string>
        {
            { 0x00001001, "FirstNpc" }
        };

        var scanResult = MakeScanResult(mainRecords: records);
        var parser = new RecordParser(scanResult, formIdCorrelations: correlations);
        var result = parser.ReconstructAll();

        // The FormIdToEditorId should include the well-known PlayerRef/Player even
        // though they weren't in the main records
        Assert.True(result.FormIdToEditorId.ContainsKey(0x00000007)); // PlayerRef
        Assert.True(result.FormIdToEditorId.ContainsKey(0x00000014)); // Player
    }

    #endregion

    #region Big-Endian Tests

    [Fact]
    public void ReconstructBooks_BigEndian_ParsesCorrectly()
    {
        var edidData = NullTermString("BEBook");
        var fullData = NullTermString("Big Endian Book");

        // DATA subrecord: flags(1) + skill(1) + value(int32 BE) + weight(float BE) = 10 bytes
        var dataField = new byte[10];
        dataField[0] = 0x02;
        dataField[1] = 0x05;
        BinaryPrimitives.WriteInt32BigEndian(dataField.AsSpan(2), 100);
        BinaryPrimitives.WriteSingleBigEndian(dataField.AsSpan(6), 3.0f);

        var recordBytes = BuildRecordBytes(0x00050001, "BOOK", true,
            ("EDID", edidData),
            ("FULL", fullData),
            ("DATA", dataField));

        var mainRecord = new DetectedMainRecord("BOOK",
            (uint)(recordBytes.Length - 24), 0, 0x00050001, 0, true);

        var scanResult = MakeScanResult(mainRecords: [mainRecord]);

        using var mmf = MemoryMappedFile.CreateNew(null, recordBytes.Length);
        using var accessor = mmf.CreateViewAccessor(0, recordBytes.Length);
        accessor.WriteArray(0, recordBytes, 0, recordBytes.Length);

        var parser = new RecordParser(scanResult, accessor: accessor, fileSize: recordBytes.Length);
        var books = parser.ReconstructBooks();

        Assert.Single(books);
        var book = books[0];
        Assert.Equal("BEBook", book.EditorId);
        Assert.Equal("Big Endian Book", book.FullName);
        Assert.Equal(100, book.Value);
        Assert.Equal(3.0f, book.Weight);
        Assert.True(book.IsBigEndian);
    }

    #endregion

    #region Sample-File-Based Tests (Skipped When Unavailable)

    [Fact]
    public void ReconstructAll_WithSampleFile_ProducesNonEmptyResults()
    {
        Assert.SkipWhen(samples.Xbox360ProtoEsm is null, "Xbox 360 proto ESM not available");

        var fileData = File.ReadAllBytes(samples.Xbox360ProtoEsm!);
        var isBigEndian = EsmParser.IsBigEndian(fileData);
        var (records, _) = EsmParser.EnumerateRecordsWithGrups(fileData);

        var mainRecords = records.Select(r => new DetectedMainRecord(
            r.Header.Signature,
            r.Header.DataSize,
            r.Header.Flags,
            r.Header.FormId,
            r.Offset,
            isBigEndian)).ToList();

        var scanResult = new EsmRecordScanResult { MainRecords = mainRecords };

        using var mmf = MemoryMappedFile.CreateFromFile(samples.Xbox360ProtoEsm!, FileMode.Open, null, 0,
            MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileData.Length, MemoryMappedFileAccess.Read);

        var parser = new RecordParser(scanResult, accessor: accessor, fileSize: fileData.Length);
        var result = parser.ReconstructAll();

        _output.WriteLine($"NPCs: {result.Npcs.Count}, Weapons: {result.Weapons.Count}, " +
                          $"Quests: {result.Quests.Count}, Dialogues: {result.Dialogues.Count}, " +
                          $"Total: {result.TotalRecordsReconstructed}");

        Assert.True(result.Npcs.Count > 0, "Expected at least some NPCs");
        Assert.True(result.Weapons.Count > 0, "Expected at least some weapons");
        Assert.True(result.TotalRecordsReconstructed > 0, "Expected non-zero reconstruction count");
    }

    #endregion
}
