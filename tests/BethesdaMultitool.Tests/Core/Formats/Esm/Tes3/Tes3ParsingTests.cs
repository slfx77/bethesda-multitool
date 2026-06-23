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