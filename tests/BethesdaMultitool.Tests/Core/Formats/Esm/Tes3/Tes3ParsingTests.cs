using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Tes3;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Tes3;

/// <summary>
///     Fixture-free tests for the Morrowind (TES3) subrecord framing + decoder. Byte buffers are
///     built in-memory to the documented layouts, so these run without the real Morrowind.esm.
/// </summary>
public class Tes3ParsingTests
{
    [Fact]
    public void IterateSubrecords_UsesFourByteSizeField()
    {
        // Two subrecords: NAME "id" (3 bytes incl. NUL) and FNAM "Name" (5 bytes incl. NUL).
        var stream = new List<byte>();
        AppendSub(stream, "NAME", "id\0"u8.ToArray());
        AppendSub(stream, "FNAM", "Name\0"u8.ToArray());
        var data = stream.ToArray();

        var subs = Tes3SubrecordUtils.IterateSubrecords(data, data.Length).ToList();

        Assert.Equal(2, subs.Count);
        Assert.Equal("NAME", subs[0].Signature);
        Assert.Equal(3, subs[0].DataLength);
        Assert.Equal("FNAM", subs[1].Signature);
        Assert.Equal(5, subs[1].DataLength);
    }

    [Fact]
    public void Decode_Weapon_Wpdt_MatchesLayout()
    {
        var wpdt = new byte[32];
        var s = wpdt.AsSpan();
        BinaryPrimitives.WriteSingleLittleEndian(s[..], 12.0f); // Weight
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], 60); // Value
        BinaryPrimitives.WriteInt16LittleEndian(s[8..], 1); // Type
        BinaryPrimitives.WriteUInt16LittleEndian(s[10..], 900); // Health
        BinaryPrimitives.WriteSingleLittleEndian(s[12..], 1.25f); // Speed
        BinaryPrimitives.WriteSingleLittleEndian(s[16..], 1.0f); // Reach
        BinaryPrimitives.WriteUInt16LittleEndian(s[20..], 50); // EnchantPts
        s[22] = 4; // ChopMin
        s[23] = 14; // ChopMax

        var fields = Tes3SubrecordDecoder.Decode("WEAP", "WPDT", wpdt);

        Assert.Equal(12.0f, Field(fields, "Weight"));
        Assert.Equal(60, Field(fields, "Value"));
        Assert.Equal((ushort)900, Field(fields, "Health")); // Health is a ushort in WPDT
        Assert.Equal(1.25f, Field(fields, "Speed"));
        Assert.Equal((byte)4, Field(fields, "ChopMin")); // damage bytes are decoded as byte
        Assert.Equal((byte)14, Field(fields, "ChopMax"));
    }

    [Fact]
    public void Decode_NpcAutocalc_Npdt12_MatchesLayout()
    {
        var npdt = new byte[12];
        var s = npdt.AsSpan();
        BinaryPrimitives.WriteInt16LittleEndian(s[..], 5); // Level
        s[2] = 50; // Disposition
        s[3] = 0; // Reputation
        s[4] = 2; // Rank
        BinaryPrimitives.WriteInt32LittleEndian(s[8..], 250); // Gold

        var fields = Tes3SubrecordDecoder.Decode("NPC_", "NPDT", npdt);

        Assert.Equal(5, Field(fields, "Level"));
        Assert.Equal(50, Field(fields, "Disposition"));
        Assert.Equal(2, Field(fields, "Rank"));
        Assert.Equal(250, Field(fields, "Gold"));
    }

    [Fact]
    public void Decode_SpellEffect_Enam_MatchesLayout()
    {
        var enam = new byte[24];
        var s = enam.AsSpan();
        BinaryPrimitives.WriteInt16LittleEndian(s[..], 53); // Effect
        s[2] = unchecked((byte)-1); // Skill
        s[3] = unchecked((byte)-1); // Attribute
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], 2); // Range
        BinaryPrimitives.WriteInt32LittleEndian(s[12..], 30); // Duration
        BinaryPrimitives.WriteInt32LittleEndian(s[16..], 100); // MagMin
        BinaryPrimitives.WriteInt32LittleEndian(s[20..], 100); // MagMax

        var fields = Tes3SubrecordDecoder.Decode("SPEL", "ENAM", enam);

        Assert.Equal((short)53, Field(fields, "Effect"));
        Assert.Equal(2, Field(fields, "Range"));
        Assert.Equal(30, Field(fields, "Duration"));
        Assert.Equal(100, Field(fields, "MagMax"));
    }

    [Fact]
    public void Decode_Reference_IsStringNotFormId()
    {
        // TES3 references are by editor-id string, not a numeric FormID.
        var script = "MyScript\0"u8.ToArray();
        var fields = Tes3SubrecordDecoder.Decode("NPC_", "SCRI", script);
        Assert.Equal("MyScript", Field(fields, "Script"));
    }

    [Fact]
    public void BuildWorldspaces_SeedsSeaLevelZeroWaterForMorrowindExterior()
    {
        // TES3 has no DNAM and its exterior cells carry no XCLW, so without a worldspace default every
        // coastal cell falls through to "no water" and Vvardenfell's ocean renders dry. The synthetic
        // exterior worldspace must seed DefaultWaterHeight = 0 (engine sea-level convention). Interior
        // cells are excluded from the exterior worldspace.
        var cells = new List<CellRecord>
        {
            new() { FormId = 0x1000, GridX = 0, GridY = 0, Flags = 0x00 },  // exterior
            new() { FormId = 0x1001, GridX = 1, GridY = 0, Flags = 0x00 },  // exterior
            new() { FormId = 0x2000, Flags = 0x01 },                        // interior — excluded
        };

        var ws = Assert.Single(Tes3RecordParser.BuildWorldspaces(cells, 0x0000BCA9));

        Assert.Equal(0f, ws.DefaultWaterHeight);
        Assert.Equal(2, ws.Cells.Count);
    }

    [Fact]
    public void BuildWorldspaces_NoExteriorCells_ReturnsEmpty()
    {
        // An all-interior plugin has no exterior worldspace to seed water on.
        var cells = new List<CellRecord> { new() { FormId = 0x2000, Flags = 0x01 } };

        Assert.Empty(Tes3RecordParser.BuildWorldspaces(cells, 0x0000BCA9));
    }

    [Fact]
    public void Decode_NpcFull_Npdt52_ReputationAtByte45_NotByte37()
    {
        // 52-byte NPDT: byte 37 is an unknown padding byte (NOT reputation). Reputation is byte 45, after
        // Health/Magicka/Fatigue/Disposition, and there is no per-NPC FactionID. The old layout read byte
        // 37 as Reputation (always 0); OpenMW esmtool reads the real value at byte 45 (tier-2 harness).
        var npdt = new byte[52];
        var s = npdt.AsSpan();
        BinaryPrimitives.WriteInt16LittleEndian(s[..], 7); // Level
        s[2] = 40; // Strength
        s[9] = 33; // Luck (last attribute byte)
        s[37] = 99; // Unknown1 — must NOT surface as Reputation
        BinaryPrimitives.WriteInt16LittleEndian(s[38..], 120); // Health
        BinaryPrimitives.WriteInt16LittleEndian(s[40..], 80); // Magicka
        BinaryPrimitives.WriteInt16LittleEndian(s[42..], 110); // Fatigue
        s[44] = 50; // Disposition
        s[45] = 18; // Reputation
        s[46] = 3; // Rank
        BinaryPrimitives.WriteInt32LittleEndian(s[48..], 500); // Gold

        var fields = Tes3SubrecordDecoder.Decode("NPC_", "NPDT", npdt);

        Assert.Equal(7, Field(fields, "Level"));
        Assert.Equal(40, Field(fields, "Strength"));
        Assert.Equal(33, Field(fields, "Luck"));
        Assert.Equal(120, Field(fields, "Health"));
        Assert.Equal(50, Field(fields, "Disposition"));
        Assert.Equal(18, Field(fields, "Reputation")); // byte 45, not the byte-37 unknown (99)
        Assert.Equal(3, Field(fields, "Rank"));
        Assert.Equal(500, Field(fields, "Gold"));
        Assert.DoesNotContain(fields, f => f.Name == "FactionId"); // the phantom field is gone
    }

    [Fact]
    public void Decode_RegionWeather_Weat_NamesChancesInOrder()
    {
        // 10 chance bytes: Clear, Cloudy, Fog, Overcast, Rain, Thunder, Ash, Blight, Snow, Blizzard.
        var weat = new byte[] { 10, 60, 5, 0, 10, 10, 0, 0, 2, 1 };

        var fields = Tes3SubrecordDecoder.Decode("REGN", "WEAT", weat);

        Assert.Equal(10, Field(fields, "Clear"));
        Assert.Equal(60, Field(fields, "Cloudy"));
        Assert.Equal(5, Field(fields, "Fog"));
        Assert.Equal(10, Field(fields, "Rain"));
        Assert.Equal(2, Field(fields, "Snow"));
        Assert.Equal(1, Field(fields, "Blizzard"));
    }

    [Fact]
    public void Decode_RegionMapColor_Cnam_ReadsPackedColor()
    {
        var cnam = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(cnam, 16721698u);

        var fields = Tes3SubrecordDecoder.Decode("REGN", "CNAM", cnam);

        Assert.Equal(16721698u, Field(fields, "MapColor"));
    }

    [Fact]
    public void Decode_RegionSleep_Bnam_ReadsCreatureId()
    {
        var bnam = "ex_bittercoast_sleep\0"u8.ToArray();

        var fields = Tes3SubrecordDecoder.Decode("REGN", "BNAM", bnam);

        Assert.Equal("ex_bittercoast_sleep", Field(fields, "SleepCreature"));
    }

    [Fact]
    public void CellParser_ReadsDoorTeleportDestination()
    {
        // A door reference carrying DODT (destination pos+rot) and DNAM (interior cell name) —
        // the inputs for the map viewer's "Links to" line, previously dropped by the TES3 parser.
        var stream = new List<byte>();
        AppendSub(stream, "NAME", "Seyda Neen\0"u8.ToArray());
        AppendSub(stream, "DATA", CellHeader(flags: 0, gridX: -2, gridY: -9));
        AppendSub(stream, "FRMR", [1, 0, 0, 0]);
        AppendSub(stream, "NAME", "ex_nord_door_01\0"u8.ToArray());
        AppendSub(stream, "DODT", DoorDestination(x: 100f, y: 200f));
        AppendSub(stream, "DNAM", "Seyda Neen, Arrille's Tradehouse\0"u8.ToArray());
        AppendSub(stream, "DATA", new byte[24]);
        var data = stream.ToArray();

        var draft = Tes3CellParser.Parse(data, data.Length, formId: 0x10, offset: 0);

        var reference = Assert.Single(draft.References);
        Assert.True(reference.HasTeleportDestination);
        Assert.Equal(100f, reference.DestX);
        Assert.Equal(200f, reference.DestY);
        Assert.Equal("Seyda Neen, Arrille's Tradehouse", reference.DestinationCellName);
    }

    [Fact]
    public void ResolveTeleportDestination_InteriorByName_ExteriorByGrid()
    {
        var byName = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
        {
            ["Seyda Neen, Arrille's Tradehouse"] = 0x2000,
        };
        var byGrid = new Dictionary<(int GridX, int GridY), uint> { [(-2, -10)] = 0x3000 };

        // DNAM wins, case-insensitive like the engine's cell lookup.
        Assert.Equal(0x2000u, Tes3RecordParser.ResolveTeleportDestination(
            new Tes3RefDraft { DestinationCellName = "seyda neen, ARRILLE'S tradehouse" },
            byName, byGrid));

        // No DNAM (exterior destination): the DODT position implies the cell via the 8192 grid —
        // x=-12000 → floor(-12000/8192) = -2; y=-76000 → floor(-76000/8192) = -10.
        Assert.Equal(0x3000u, Tes3RecordParser.ResolveTeleportDestination(
            new Tes3RefDraft { HasTeleportDestination = true, DestX = -12000f, DestY = -76000f },
            byName, byGrid));

        // Unknown DNAM (e.g. the cell lives in a master this file doesn't contain) falls back to
        // the DODT grid when present; a plain non-door reference resolves to nothing.
        Assert.Equal(0x3000u, Tes3RecordParser.ResolveTeleportDestination(
            new Tes3RefDraft
            {
                DestinationCellName = "Not In This File",
                HasTeleportDestination = true, DestX = -12000f, DestY = -76000f,
            },
            byName, byGrid));
        Assert.Null(Tes3RecordParser.ResolveTeleportDestination(new Tes3RefDraft(), byName, byGrid));
    }

    private static byte[] CellHeader(int flags, int gridX, int gridY)
    {
        var data = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(data, flags);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), gridX);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), gridY);
        return data;
    }

    private static byte[] DoorDestination(float x, float y)
    {
        var data = new byte[24]; // pos xyz + rot xyz
        BinaryPrimitives.WriteSingleLittleEndian(data, x);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(4), y);
        return data;
    }

    private static object? Field(IReadOnlyList<Tes3SubrecordDecoder.Field> fields, string name)
    {
        return fields.First(f => f.Name == name).Value;
    }

    private static void AppendSub(List<byte> stream, string sig, byte[] payload)
    {
        stream.AddRange(Encoding.ASCII.GetBytes(sig));
        var size = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)payload.Length);
        stream.AddRange(size);
        stream.AddRange(payload);
    }
}