using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Parsing;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using Xunit;
using static FalloutXbox360Utils.Tests.Helpers.EsmTestRecordBuilder;

namespace FalloutXbox360Utils.Tests.Core.Parsers;

/// <summary>
///     Regression tests for RecordParser parsing methods.
///     Uses synthetic scan results to test without requiring sample files.
///     These tests anchor behavior before the partial-class-to-handler refactoring.
/// </summary>
public class RecordParserHandlerTests
{
    #region Big-Endian Tests

    [Fact]
    public void ParseBooks_BigEndian_ParsesCorrectly()
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

        var scanResult = MakeScanResult([mainRecord]);

        using var mmf = MemoryMappedFile.CreateNew(null, recordBytes.Length);
        using var accessor = mmf.CreateViewAccessor(0, recordBytes.Length);
        accessor.WriteArray(0, recordBytes, 0, recordBytes.Length);

        var parser = new RecordParser(scanResult, accessor: accessor, fileSize: recordBytes.Length);
        var books = parser.ParseBooks();

        Assert.Single(books);
        var book = books[0];
        Assert.Equal("BEBook", book.EditorId);
        Assert.Equal("Big Endian Book", book.FullName);
        Assert.Equal(100, book.Value);
        Assert.Equal(3.0f, book.Weight);
        Assert.True(book.IsBigEndian);
    }

    #endregion

    #region Constructor / Lookup Tests

    [Fact]
    public void Constructor_BuildsFormIdToEditorIdMap_FromEdidSubrecords()
    {
        var mainRecords = new List<DetectedMainRecord>
        {
            MakeRecord("NPC_", 0x00001234, 100, 50),
            MakeRecord("WEAP", 0x00005678, 200, 50)
        };

        var editorIds = new List<EdidRecord>
        {
            new("TestNpc", 110), // Within NPC_ record bounds (offset 100, size 50+24=174)
            new("TestWeapon", 210) // Within WEAP record bounds
        };

        var scanResult = MakeScanResult(mainRecords, editorIds);
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

        var scanResult = MakeScanResult([MakeRecord("NPC_", 0x00001111, 0)]);
        var parser = new RecordParser(scanResult, correlations);

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
        var parser = new RecordParser(scanResult, correlations);

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

        var scanResult = MakeScanResult(records);
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

    #region Parsing Without Accessor Tests

    public static TheoryData<string, uint, string?, string?> ParseWithoutAccessorCases => new()
    {
        // recordType, formId, editorId, expectedFullName
        { "NPC_", 0x00001234u, "TestNpc", null },
        { "CREA", 0x000AAAA1u, "TestCreature", null },
        { "TERM", 0x000B0001u, "TestTerminal", "Terminal Alpha" },
        { "PACK", 0x000C0001u, null, null }
    };

    [Theory]
    [MemberData(nameof(ParseWithoutAccessorCases))]
    public void Parse_WithoutAccessor_ReturnsBasicRecords(
        string recordType, uint formId, string? editorId, string? expectedFullName)
    {
        var records = new List<DetectedMainRecord> { MakeRecord(recordType, formId, 100) };

        var correlations = editorId is not null
            ? new Dictionary<uint, string> { { formId, editorId } }
            : new Dictionary<uint, string>();

        var fullNames = expectedFullName is not null
            ? new List<TextSubrecord> { new("FULL", expectedFullName, 120) }
            : null;

        var scanResult = MakeScanResult(records, fullNames: fullNames);
        var parser = new RecordParser(scanResult, correlations);

        var (resultFormId, resultEditorId, resultFullName) = recordType switch
        {
            "NPC_" => ExtractFields(parser.ParseNpcs(), r => (r.FormId, r.EditorId, null)),
            "CREA" => ExtractFields(parser.ParseCreatures(), r => (r.FormId, r.EditorId, null)),
            "TERM" => ExtractFields(parser.ParseTerminals(), r => (r.FormId, r.EditorId, r.FullName)),
            "PACK" => ExtractFields(parser.ParsePackages(), r => (r.FormId, r.EditorId, null)),
            _ => throw new ArgumentException($"Unknown record type: {recordType}")
        };

        static (uint, string?, string?) ExtractFields<T>(
            List<T> results, Func<T, (uint, string?, string?)> selector)
        {
            Assert.NotEmpty(results);
            return selector(results[0]);
        }

        Assert.Equal(formId, resultFormId);

        if (editorId is not null)
        {
            Assert.Equal(editorId, resultEditorId);
        }

        if (expectedFullName is not null)
        {
            Assert.Equal(expectedFullName, resultFullName);
        }
    }

    [Fact]
    public void ParseGlobals_WithoutAccessor_ReturnsEmpty()
    {
        // Globals require accessor to read data - should return empty without one
        var records = new List<DetectedMainRecord>
        {
            MakeRecord("GLOB", 0x000D0001, 100)
        };

        var scanResult = MakeScanResult(records);
        var parser = new RecordParser(scanResult);
        var globals = parser.ParseGlobals();

        Assert.Empty(globals);
    }

    #endregion

    #region Parsing With Accessor Tests

    [Fact]
    public void ParseBooks_WithAccessor_ParsesSubrecords()
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

        var scanResult = MakeScanResult([mainRecord]);

        using var mmf = MemoryMappedFile.CreateNew(null, recordBytes.Length);
        using var accessor = mmf.CreateViewAccessor(0, recordBytes.Length);
        accessor.WriteArray(0, recordBytes, 0, recordBytes.Length);

        var parser = new RecordParser(scanResult, accessor: accessor, fileSize: recordBytes.Length);
        var books = parser.ParseBooks();

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
    public void ParseNotes_WithAccessor_ParsesSubrecords()
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

        var scanResult = MakeScanResult([mainRecord]);

        using var mmf = MemoryMappedFile.CreateNew(null, recordBytes.Length);
        using var accessor = mmf.CreateViewAccessor(0, recordBytes.Length);
        accessor.WriteArray(0, recordBytes, 0, recordBytes.Length);

        var parser = new RecordParser(scanResult, accessor: accessor, fileSize: recordBytes.Length);
        var notes = parser.ParseNotes();

        Assert.Single(notes);
        var note = notes[0];
        Assert.Equal("TestNote01", note.EditorId);
        Assert.Equal("Test Note", note.FullName);
        Assert.Equal("Note contents here", note.Text);
        Assert.Equal(3, note.NoteType);
    }

    [Fact]
    public void ParseNotes_WithAccessor_ParsesAssetsAndReferences()
    {
        var soundData = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(soundData, 0x00001234);
        var objectData = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(objectData, 0x00005678);

        var recordBytes = BuildRecordBytes(0x00020002, "NOTE", false,
            ("EDID", NullTermString("TestHolotape")),
            ("FULL", NullTermString("Test Holotape")),
            ("MODL", NullTermString(@"Clutter\Holodisk\Holodisk01.NIF")),
            ("ICON", NullTermString(@"Interface\Icons\PipboyImages\Items\item_holotap.dds")),
            ("MICO", NullTermString(@"Interface\Icons\MessageIcon.dds")),
            ("SNAM", soundData),
            ("ONAM", objectData),
            ("DATA", [0x01]),
            ("TNAM", NullTermString("Holotape transcript")));

        var mainRecord = new DetectedMainRecord("NOTE",
            (uint)(recordBytes.Length - 24), 0, 0x00020002, 0, false);

        var scanResult = MakeScanResult([mainRecord]);

        using var mmf = MemoryMappedFile.CreateNew(null, recordBytes.Length);
        using var accessor = mmf.CreateViewAccessor(0, recordBytes.Length);
        accessor.WriteArray(0, recordBytes, 0, recordBytes.Length);

        var parser = new RecordParser(scanResult, accessor: accessor, fileSize: recordBytes.Length);
        var note = Assert.Single(parser.ParseNotes());

        Assert.Equal(@"Clutter\Holodisk\Holodisk01.NIF", note.ModelPath);
        Assert.Equal(@"Interface\Icons\PipboyImages\Items\item_holotap.dds", note.IconPath);
        Assert.Equal(@"Interface\Icons\MessageIcon.dds", note.TexturePath);
        Assert.Equal(0x00001234u, note.SoundFormId);
        Assert.Equal(0x00005678u, note.ObjectFormId);
        Assert.Equal("Holotape transcript", note.Text);
    }

    [Fact]
    public void ParseNotes_WithAccessor_ParsesFourByteTnamAsTopicReference()
    {
        var topicData = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(topicData, 0x0000CAFE);

        var recordBytes = BuildRecordBytes(0x00020003, "NOTE", false,
            ("EDID", NullTermString("TestTopicNote")),
            ("TNAM", topicData));

        var mainRecord = new DetectedMainRecord("NOTE",
            (uint)(recordBytes.Length - 24), 0, 0x00020003, 0, false);

        var scanResult = MakeScanResult([mainRecord]);

        using var mmf = MemoryMappedFile.CreateNew(null, recordBytes.Length);
        using var accessor = mmf.CreateViewAccessor(0, recordBytes.Length);
        accessor.WriteArray(0, recordBytes, 0, recordBytes.Length);

        var parser = new RecordParser(scanResult, accessor: accessor, fileSize: recordBytes.Length);
        var note = Assert.Single(parser.ParseNotes());

        Assert.Equal(0x0000CAFEu, note.TopicFormId);
        Assert.Null(note.Text);
    }

    [Fact]
    public void ParseAll_EsmAmmoWithoutDat2Projectile_UsesUniqueWeaponProjectileFallback()
    {
        const uint ammoFormId = 0x00001000;
        const uint weaponFormId = 0x00002000;
        const uint projectileFormId = 0x00003000;

        var ammoData = new byte[13];
        BinaryPrimitives.WriteSingleLittleEndian(ammoData, 1.0f);
        BinaryPrimitives.WriteUInt32LittleEndian(ammoData.AsSpan(8), 2);
        ammoData[12] = 30;

        var enamData = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(enamData, ammoFormId);

        var dnamData = new byte[64];
        BinaryPrimitives.WriteSingleLittleEndian(dnamData.AsSpan(4), 1.0f);
        BinaryPrimitives.WriteSingleLittleEndian(dnamData.AsSpan(8), 1.0f);
        BinaryPrimitives.WriteUInt32LittleEndian(dnamData.AsSpan(36), projectileFormId);
        dnamData[42] = 1;

        var ammoBytes = BuildRecordBytes(ammoFormId, "AMMO", false,
            ("EDID", NullTermString("AmmoTest")),
            ("FULL", NullTermString("Test Ammo")),
            ("DATA", ammoData));
        var weaponBytes = BuildRecordBytes(weaponFormId, "WEAP", false,
            ("EDID", NullTermString("WeapTest")),
            ("ENAM", enamData),
            ("DNAM", dnamData));
        var projectileBytes = BuildRecordBytes(projectileFormId, "PROJ", false,
            ("EDID", NullTermString("ProjectileTest")));

        var data = new byte[ammoBytes.Length + weaponBytes.Length + projectileBytes.Length];
        Array.Copy(ammoBytes, 0, data, 0, ammoBytes.Length);
        Array.Copy(weaponBytes, 0, data, ammoBytes.Length, weaponBytes.Length);
        Array.Copy(projectileBytes, 0, data, ammoBytes.Length + weaponBytes.Length, projectileBytes.Length);

        var mainRecords = new List<DetectedMainRecord>
        {
            new("AMMO", (uint)(ammoBytes.Length - 24), 0, ammoFormId, 0, false),
            new("WEAP", (uint)(weaponBytes.Length - 24), 0, weaponFormId, ammoBytes.Length, false),
            new("PROJ", (uint)(projectileBytes.Length - 24), 0, projectileFormId,
                ammoBytes.Length + weaponBytes.Length, false)
        };

        var scanResult = MakeScanResult(mainRecords);
        using var mmf = MemoryMappedFile.CreateNew(null, data.Length);
        using var accessor = mmf.CreateViewAccessor(0, data.Length);
        accessor.WriteArray(0, data, 0, data.Length);

        var parser = new RecordParser(scanResult, accessor: accessor, fileSize: data.Length);
        var result = parser.ParseAll();

        var ammo = Assert.Single(result.Ammo);
        Assert.Equal(projectileFormId, ammo.ProjectileFormId);
        Assert.Equal([projectileFormId], ammo.ProjectileFormIds);
    }

    [Fact]
    public void ParseAll_EsmAmmoWithMultipleWeaponProjectiles_PreservesAllProjectileCandidates()
    {
        const uint ammoFormId = 0x00001000;
        const uint weaponOneFormId = 0x00002000;
        const uint weaponTwoFormId = 0x00002001;
        const uint projectileOneFormId = 0x00003000;
        const uint projectileTwoFormId = 0x00003001;

        var ammoData = new byte[13];
        BinaryPrimitives.WriteSingleLittleEndian(ammoData, 1.0f);
        BinaryPrimitives.WriteUInt32LittleEndian(ammoData.AsSpan(8), 2);
        ammoData[12] = 30;

        static byte[] BuildEnam(uint ammoFormId)
        {
            var enamData = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(enamData, ammoFormId);
            return enamData;
        }

        static byte[] BuildWeaponDnam(uint projectileFormId)
        {
            var dnamData = new byte[64];
            BinaryPrimitives.WriteSingleLittleEndian(dnamData.AsSpan(4), 1.0f);
            BinaryPrimitives.WriteSingleLittleEndian(dnamData.AsSpan(8), 1.0f);
            BinaryPrimitives.WriteUInt32LittleEndian(dnamData.AsSpan(36), projectileFormId);
            dnamData[42] = 1;
            return dnamData;
        }

        var ammoBytes = BuildRecordBytes(ammoFormId, "AMMO", false,
            ("EDID", NullTermString("AmmoTest")),
            ("FULL", NullTermString("Test Ammo")),
            ("DATA", ammoData));
        var weaponOneBytes = BuildRecordBytes(weaponOneFormId, "WEAP", false,
            ("EDID", NullTermString("WeapOneTest")),
            ("ENAM", BuildEnam(ammoFormId)),
            ("DNAM", BuildWeaponDnam(projectileOneFormId)));
        var weaponTwoBytes = BuildRecordBytes(weaponTwoFormId, "WEAP", false,
            ("EDID", NullTermString("WeapTwoTest")),
            ("ENAM", BuildEnam(ammoFormId)),
            ("DNAM", BuildWeaponDnam(projectileTwoFormId)));
        var projectileOneBytes = BuildRecordBytes(projectileOneFormId, "PROJ", false,
            ("EDID", NullTermString("ProjectileOneTest")));
        var projectileTwoBytes = BuildRecordBytes(projectileTwoFormId, "PROJ", false,
            ("EDID", NullTermString("ProjectileTwoTest")));

        var chunks = new[] { ammoBytes, weaponOneBytes, weaponTwoBytes, projectileOneBytes, projectileTwoBytes };
        var data = new byte[chunks.Sum(chunk => chunk.Length)];
        var offset = 0;
        foreach (var chunk in chunks)
        {
            Array.Copy(chunk, 0, data, offset, chunk.Length);
            offset += chunk.Length;
        }

        var mainRecords = new List<DetectedMainRecord>();
        offset = 0;
        foreach (var (recordType, formId, bytes) in new[]
                 {
                     ("AMMO", ammoFormId, ammoBytes),
                     ("WEAP", weaponOneFormId, weaponOneBytes),
                     ("WEAP", weaponTwoFormId, weaponTwoBytes),
                     ("PROJ", projectileOneFormId, projectileOneBytes),
                     ("PROJ", projectileTwoFormId, projectileTwoBytes)
                 })
        {
            mainRecords.Add(new DetectedMainRecord(recordType, (uint)(bytes.Length - 24), 0, formId, offset, false));
            offset += bytes.Length;
        }

        var scanResult = MakeScanResult(mainRecords);
        using var mmf = MemoryMappedFile.CreateNew(null, data.Length);
        using var accessor = mmf.CreateViewAccessor(0, data.Length);
        accessor.WriteArray(0, data, 0, data.Length);

        var parser = new RecordParser(scanResult, accessor: accessor, fileSize: data.Length);
        var result = parser.ParseAll();

        var ammo = Assert.Single(result.Ammo);
        Assert.Null(ammo.ProjectileFormId);
        Assert.Equal([projectileOneFormId, projectileTwoFormId], ammo.ProjectileFormIds.OrderBy(id => id));
    }

    [Fact]
    public void ParsePerks_WithAccessor_ParsesEsmConditionsAndEntryEffects()
    {
        var dataField = new byte[] { 0x00, 0x08, 0x01, 0x01, 0x00 };

        var topCondition = new byte[28];
        topCondition[0] = 0x20; // !=
        BinaryPrimitives.WriteSingleLittleEndian(topCondition.AsSpan(4), 0.0f);
        BinaryPrimitives.WriteUInt16LittleEndian(topCondition.AsSpan(8), 0x46); // GetIsSex
        BinaryPrimitives.WriteUInt32LittleEndian(topCondition.AsSpan(12), 0x00000000); // Male

        var entryData = new byte[] { 0x03, 0x03, 0x00 };
        var entryCondition = new byte[28];
        entryCondition[0] = 0x00; // ==
        BinaryPrimitives.WriteSingleLittleEndian(entryCondition.AsSpan(4), 1.0f);
        BinaryPrimitives.WriteUInt16LittleEndian(entryCondition.AsSpan(8), 0x1C1); // HasPerk
        BinaryPrimitives.WriteUInt32LittleEndian(entryCondition.AsSpan(12), 0x0000ABCD);

        var epfd = new byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(epfd, 1.1f);

        var recordBytes = BuildRecordBytes(0x00060001, "PERK", false,
            ("EDID", NullTermString("TestPerk")),
            ("FULL", NullTermString("Test Perk")),
            ("DATA", dataField),
            ("CTDA", topCondition),
            ("PRKE", [0x02, 0x00, 0x05]),
            ("DATA", entryData),
            ("PRKC", [0x02]),
            ("CTDA", entryCondition),
            ("EPFT", [0x01]),
            ("EPFD", epfd),
            ("PRKF", []));

        var mainRecord = new DetectedMainRecord("PERK",
            (uint)(recordBytes.Length - 24), 0, 0x00060001, 0, false);

        var scanResult = MakeScanResult([mainRecord]);

        using var mmf = MemoryMappedFile.CreateNew(null, recordBytes.Length);
        using var accessor = mmf.CreateViewAccessor(0, recordBytes.Length);
        accessor.WriteArray(0, recordBytes, 0, recordBytes.Length);

        var parser = new RecordParser(scanResult, accessor: accessor, fileSize: recordBytes.Length);
        var perk = Assert.Single(parser.ParsePerks());

        Assert.Equal("TestPerk", perk.EditorId);
        Assert.Equal("GetIsSex", perk.Conditions[0].FunctionName);
        Assert.Equal("Male", perk.Conditions[0].Parameter1Display);
        Assert.Equal("HasPerk", perk.Conditions[1].FunctionName);
        Assert.Equal(0x0000ABCDu, perk.Conditions[1].Parameter1FormId);

        var entry = Assert.Single(perk.Entries);
        Assert.Equal(5, entry.Priority);
        Assert.Equal((byte)3, entry.EntryPoint);
        Assert.Equal((byte)1, entry.FunctionType);
        Assert.Equal("Add Value", entry.FunctionTypeName);
        Assert.Equal(1.1f, entry.EffectValue.GetValueOrDefault(), 3);
        Assert.Equal((byte)2, entry.ConditionTabCount);
        Assert.Single(entry.Conditions);
    }

    [Fact]
    public void ParsePerks_WithAccessor_ResolvesPermanentActorValueConditionParameter()
    {
        var dataField = new byte[] { 0x00, 0x08, 0x01, 0x01, 0x00 };

        var condition = new byte[28];
        condition[0] = 0x60; // >=
        BinaryPrimitives.WriteSingleLittleEndian(condition.AsSpan(4), 4.0f);
        BinaryPrimitives.WriteUInt16LittleEndian(condition.AsSpan(8), 0x01EF); // GetPermanentActorValue
        BinaryPrimitives.WriteUInt32LittleEndian(condition.AsSpan(12), 0x00000009); // Intelligence

        var recordBytes = BuildRecordBytes(0x00060002, "PERK", false,
            ("EDID", NullTermString("TestPermanentActorValuePerk")),
            ("DATA", dataField),
            ("CTDA", condition));

        var mainRecord = new DetectedMainRecord("PERK",
            (uint)(recordBytes.Length - 24), 0, 0x00060002, 0, false);

        var scanResult = MakeScanResult([mainRecord]);

        using var mmf = MemoryMappedFile.CreateNew(null, recordBytes.Length);
        using var accessor = mmf.CreateViewAccessor(0, recordBytes.Length);
        accessor.WriteArray(0, recordBytes, 0, recordBytes.Length);

        var parser = new RecordParser(scanResult, accessor: accessor, fileSize: recordBytes.Length);
        var perk = Assert.Single(parser.ParsePerks());
        var parsedCondition = Assert.Single(perk.Conditions);

        Assert.Equal("GetPermanentActorValue", parsedCondition.FunctionName);
        Assert.Equal(0x00000009u, parsedCondition.Parameter1);
        Assert.Equal("Intelligence", parsedCondition.Parameter1Display);
        Assert.Null(parsedCondition.Parameter1FormId);
    }

    [Fact]
    public void ParseMessages_WithAccessor_ParsesSubrecords()
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

        var scanResult = MakeScanResult([mainRecord]);

        using var mmf = MemoryMappedFile.CreateNew(null, recordBytes.Length);
        using var accessor = mmf.CreateViewAccessor(0, recordBytes.Length);
        accessor.WriteArray(0, recordBytes, 0, recordBytes.Length);

        var parser = new RecordParser(scanResult, accessor: accessor, fileSize: recordBytes.Length);
        var messages = parser.ParseMessages();

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
    public void ParseDialogue_WithSplitInfoRecords_MergesConditionsAndResultScripts()
    {
        var conditionData = new byte[28];
        conditionData[0] = 0x00; // Equal
        BinaryPrimitives.WriteSingleLittleEndian(conditionData.AsSpan(4), 1.0f);
        BinaryPrimitives.WriteUInt16LittleEndian(conditionData.AsSpan(8), 0x48); // GetIsID
        BinaryPrimitives.WriteUInt32LittleEndian(conditionData.AsSpan(12), 0x00001234);
        BinaryPrimitives.WriteUInt32LittleEndian(conditionData.AsSpan(20), 0); // Subject

        var sourceText = NullTermString("StartCombat Player");

        var responseData = new byte[24];
        responseData[12] = 1;

        var scriptHeader = new byte[20];
        scriptHeader[18] = 1; // compiled

        var scriptReference = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(scriptReference, 0x00004321);

        var baseRecordBytes = BuildRecordBytes(0x00070001, "INFO", false,
            ("EDID", NullTermString("InfoTestSplit")),
            ("CTDA", conditionData),
            ("SCTX", sourceText));

        var responseRecordBytes = BuildRecordBytes(0x00070001, "INFO", false,
            ("TRDT", responseData),
            ("NAM1", NullTermString("Hello there")),
            ("SCHR", scriptHeader),
            ("SCRO", scriptReference));

        var recordBytes = new byte[baseRecordBytes.Length + responseRecordBytes.Length];
        Array.Copy(baseRecordBytes, 0, recordBytes, 0, baseRecordBytes.Length);
        Array.Copy(responseRecordBytes, 0, recordBytes, baseRecordBytes.Length, responseRecordBytes.Length);

        var mainRecords = new List<DetectedMainRecord>
        {
            new("INFO", (uint)(baseRecordBytes.Length - 24), 0, 0x00070001, 0, false),
            new("INFO", (uint)(responseRecordBytes.Length - 24), 0, 0x00070001, baseRecordBytes.Length, false)
        };
        var correlations = new Dictionary<uint, string>
        {
            [0x00001234] = "Cooke",
            [0x00004321] = "MarkerGoodspringsPatrol"
        };
        var scanResult = MakeScanResult(mainRecords);

        using var mmf = MemoryMappedFile.CreateNew(null, recordBytes.Length);
        using var accessor = mmf.CreateViewAccessor(0, recordBytes.Length);
        accessor.WriteArray(0, recordBytes, 0, recordBytes.Length);

        var parser = new RecordParser(scanResult, correlations, accessor, recordBytes.Length);
        var dialogues = parser.ParseDialogue();

        var info = Assert.Single(dialogues);
        Assert.Equal("InfoTestSplit", info.EditorId);
        Assert.Equal(0x00001234u, info.SpeakerFormId);
        Assert.Single(info.Conditions);
        Assert.Equal(0x48, info.Conditions[0].FunctionIndex);
        Assert.Equal(0x00001234u, info.Conditions[0].Parameter1);
        Assert.True(info.HasResultScript);
        Assert.Single(info.ResultScripts);
        Assert.Equal("StartCombat Player", info.ResultScripts[0].SourceText);
        Assert.Contains(0x00004321u, info.ResultScripts[0].ReferencedObjects);
        Assert.Single(info.Responses);
        Assert.Equal("Hello there", info.Responses[0].Text);
    }

    [Fact]
    public void ParseGlobals_WithAccessor_ParsesSubrecords()
    {
        var edidData = NullTermString("TimeScale");

        // FNAM: value type 'f' (float)
        var fnamData = new[] { (byte)'f' };

        // FLTV: float value 30.0
        var fltvData = new byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(fltvData, 30.0f);

        var recordBytes = BuildRecordBytes(0x00040001, "GLOB", false,
            ("EDID", edidData),
            ("FNAM", fnamData),
            ("FLTV", fltvData));

        var mainRecord = new DetectedMainRecord("GLOB",
            (uint)(recordBytes.Length - 24), 0, 0x00040001, 0, false);

        var scanResult = MakeScanResult([mainRecord]);

        using var mmf = MemoryMappedFile.CreateNew(null, recordBytes.Length);
        using var accessor = mmf.CreateViewAccessor(0, recordBytes.Length);
        accessor.WriteArray(0, recordBytes, 0, recordBytes.Length);

        var parser = new RecordParser(scanResult, accessor: accessor, fileSize: recordBytes.Length);
        var globals = parser.ParseGlobals();

        Assert.Single(globals);
        var global = globals[0];
        Assert.Equal("TimeScale", global.EditorId);
        Assert.Equal('f', global.ValueType);
        Assert.Equal(30.0f, global.Value);
    }

    #endregion

    #region ParseAll Tests

    [Fact]
    public void ParseAll_EmptyScanResult_ReturnsEmptyCollection()
    {
        var scanResult = MakeScanResult();
        var parser = new RecordParser(scanResult);
        var result = parser.ParseAll();

        Assert.NotNull(result);
        Assert.Empty(result.Npcs);
        Assert.Empty(result.Weapons);
        Assert.Empty(result.Dialogues);
        Assert.Equal(0, result.TotalRecordsParsed);
    }

    [Fact]
    public void ParseAll_WithMultipleTypes_ReturnsPopulatedCollection()
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

        var scanResult = MakeScanResult(records);
        var parser = new RecordParser(scanResult, correlations);
        var result = parser.ParseAll();

        Assert.Single(result.Npcs);
        Assert.Single(result.Creatures);
        Assert.Single(result.Packages);
        Assert.Single(result.Terminals);
        Assert.True(result.TotalRecordsParsed >= 4);

        // Verify EditorID maps are populated in the result
        Assert.True(result.FormIdToEditorId.ContainsKey(0x00001001));
    }

    [Fact]
    public void ParseAll_PreservesMethodOrdering_EditorIdEnrichment()
    {
        // Parsing methods enrich _formIdToEditorId as they go.
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

        var scanResult = MakeScanResult(records);
        var parser = new RecordParser(scanResult, correlations);
        var result = parser.ParseAll();

        // The FormIdToEditorId should include the well-known PlayerRef/Player even
        // though they weren't in the main records
        Assert.Equal("PlayerRef", result.FormIdToEditorId[0x00000007]);
        Assert.Equal("Player", result.FormIdToEditorId[0x00000014]);
    }

    #endregion
}
