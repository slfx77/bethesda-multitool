using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Parsing;
using Xunit;
using static FalloutXbox360Utils.Tests.Helpers.EsmTestRecordBuilder;

namespace FalloutXbox360Utils.Tests.Core.Parsers;

/// <summary>
///     Tests for ESM semantic parsing of NPC and creature records, including compressed record
///     decompression. Uses synthetic big-endian records — no sample files required.
/// </summary>
public class EsmParsingTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    [Fact]
    public void NpcParsing_CompressedRecord_ShouldParseAllFields()
    {
        // Build a compressed big-endian NPC_ record with EDID, FULL, ACBS, RNAM subrecords.
        // ACBS = 24 bytes: flags(4) fatigue(2) barter(2) level(2) calcMin(2) calcMax(2)
        //        speedMult(2) karma(4) disposition(2) templateFlags(2)
        var acbs = new byte[24];
        BinaryPrimitives.WriteUInt32BigEndian(acbs.AsSpan(0), 0x00000001); // flags
        BinaryPrimitives.WriteUInt16BigEndian(acbs.AsSpan(4), 200); // fatigue
        BinaryPrimitives.WriteUInt16BigEndian(acbs.AsSpan(6), 100); // barter gold
        BinaryPrimitives.WriteInt16BigEndian(acbs.AsSpan(8), 20); // level
        BinaryPrimitives.WriteUInt16BigEndian(acbs.AsSpan(14), 100); // speed mult

        // RNAM = 4 bytes: race FormID (BE)
        var rnam = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(rnam, 0x000019B9); // Caucasian race

        var recordBytes = BuildCompressedRecordBE("NPC_", 0x00092BD2,
            ("EDID", NullTermString("CraigBoone")),
            ("FULL", NullTermString("Boone")),
            ("ACBS", acbs),
            ("RNAM", rnam));

        var mainRecord = new DetectedMainRecord("NPC_",
            (uint)(recordBytes.Length - 24), 0x00040000, 0x00092BD2, 0, true);

        var scanResult = MakeScanResult([mainRecord]);

        using var mmf = MemoryMappedFile.CreateNew(null, recordBytes.Length);
        using var accessor = mmf.CreateViewAccessor(0, recordBytes.Length);
        accessor.WriteArray(0, recordBytes, 0, recordBytes.Length);

        var parser = new RecordParser(scanResult, accessor: accessor, fileSize: recordBytes.Length);
        var npcs = parser.ParseNpcs();

        Assert.Single(npcs);
        var boone = npcs[0];
        Assert.Equal("CraigBoone", boone.EditorId);
        Assert.Equal("Boone", boone.FullName);
        Assert.NotNull(boone.Stats);
        Assert.Equal(0x000019B9u, boone.Race);

        _output.WriteLine($"Boone: EditorId={boone.EditorId}, FullName={boone.FullName}, " +
                          $"Race=0x{boone.Race:X8}, Level={boone.Stats!.Level}");
    }

    [Fact]
    public void CreatureParsing_ShouldParseSubrecords()
    {
        // Build 3 big-endian CREA records with EDID, FULL, ACBS, DATA subrecords.
        // ACBS = 24 bytes (same layout as NPC_)
        // DATA = 8 bytes minimum: type(1) combat(1) magic(1) stealth(1) damage(4 BE)
        var creatures = new[]
        {
            ("Gecko", "Gecko", (byte)0x01, (byte)30, (short)15, 0x00010001u),
            ("RadScorpion", "Radscorpion", (byte)0x02, (byte)50, (short)30, 0x00010002u),
            ("Deathclaw", "Deathclaw", (byte)0x03, (byte)80, (short)100, 0x00010003u)
        };

        var allRecordBytes = new List<byte[]>();
        var mainRecords = new List<DetectedMainRecord>();
        long offset = 0;

        foreach (var (edid, full, type, combat, damage, formId) in creatures)
        {
            var acbs = new byte[24];
            BinaryPrimitives.WriteUInt32BigEndian(acbs.AsSpan(0), 0); // flags
            BinaryPrimitives.WriteInt16BigEndian(acbs.AsSpan(8), 10); // level

            // DATA = 17 bytes to match schema: type(1) combat(1) magic(1) stealth(1)
            //        attackDamage(4 BE) health(2 BE) remaining(7)
            var data = new byte[17];
            data[0] = type; // creature type
            data[1] = combat; // combat skill
            data[2] = 20; // magic skill
            data[3] = 25; // stealth skill
            BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(4), damage); // attack damage
            BinaryPrimitives.WriteInt16BigEndian(data.AsSpan(8), 100); // health

            var recordBytes = BuildRecordBytes(formId, "CREA", true,
                ("EDID", NullTermString(edid)),
                ("FULL", NullTermString(full)),
                ("ACBS", acbs),
                ("DATA", data));

            allRecordBytes.Add(recordBytes);
            mainRecords.Add(new DetectedMainRecord("CREA",
                (uint)(recordBytes.Length - 24), 0, formId, offset, true));
            offset += recordBytes.Length;
        }

        // Concatenate all records into one buffer
        var totalSize = allRecordBytes.Sum(b => b.Length);
        var buffer = new byte[totalSize];
        var pos = 0;
        foreach (var rb in allRecordBytes)
        {
            Array.Copy(rb, 0, buffer, pos, rb.Length);
            pos += rb.Length;
        }

        var scanResult = MakeScanResult(mainRecords);

        using var mmf = MemoryMappedFile.CreateNew(null, buffer.Length);
        using var accessor = mmf.CreateViewAccessor(0, buffer.Length);
        accessor.WriteArray(0, buffer, 0, buffer.Length);

        var parser = new RecordParser(scanResult, accessor: accessor, fileSize: buffer.Length);
        var parsed = parser.ParseCreatures();

        _output.WriteLine($"Parsed {parsed.Count} creatures");
        Assert.Equal(3, parsed.Count);

        var withEditorId = parsed.Count(c => !string.IsNullOrEmpty(c.EditorId));
        var withFullName = parsed.Count(c => !string.IsNullOrEmpty(c.FullName));
        var withStats = parsed.Count(c => c.Stats != null);

        Assert.Equal(3, withEditorId);
        Assert.Equal(3, withFullName);
        Assert.Equal(3, withStats);

        // Verify specific creature data
        var deathclaw = parsed.First(c => c.EditorId == "Deathclaw");
        Assert.Equal("Deathclaw", deathclaw.FullName);

        _output.WriteLine($"Deathclaw: Type={deathclaw.CreatureType}, Combat={deathclaw.CombatSkill}, " +
                          $"Magic={deathclaw.MagicSkill}, Stealth={deathclaw.StealthSkill}");

        // For 8-byte DATA (no schema match for this size), the fallback path
        // reads raw bytes: type=subData[0], combat=subData[1], etc.
        Assert.Equal(0x03, deathclaw.CreatureType);
        Assert.Equal(80, deathclaw.CombatSkill);
    }
}