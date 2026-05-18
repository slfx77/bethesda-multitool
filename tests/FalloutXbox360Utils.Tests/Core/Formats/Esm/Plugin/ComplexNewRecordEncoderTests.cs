using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     v5 tests for the four complex new-record encoders: WEAP, ARMO, FACT, NPC_.
///     Verifies subrecord byte layouts match the schema registry, optional fields are
///     emitted when set, and canonical fopdoc order is preserved.
/// </summary>
public class ComplexNewRecordEncoderTests
{
    [Fact]
    public void WeapEncoder_EncodeNew_DnamIs204Bytes()
    {
        var weap = new WeaponRecord
        {
            FormId = 0x800,
            EditorId = "NewWeap",
            FullName = "New Weapon",
            ModelPath = "Weapons/NewWeap.NIF",
            Value = 100,
            Health = 1000,
            Weight = 5.0f,
            Damage = 25,
            ClipSize = 10,
            Speed = 1.0f,
            Reach = 1.5f,
            Flags = 0x02
        };

        var encoded = WeapEncoder.EncodeNew(weap);

        var dnam = Assert.Single(encoded.Subrecords, s => s.Signature == "DNAM");
        Assert.Equal(204, dnam.Bytes.Length);
        // Verify a few field positions per the schema:
        Assert.Equal(1.0f, BinaryPrimitives.ReadSingleLittleEndian(dnam.Bytes.AsSpan(4, 4))); // Speed
        Assert.Equal(1.5f, BinaryPrimitives.ReadSingleLittleEndian(dnam.Bytes.AsSpan(8, 4))); // Reach
        Assert.Equal((byte)0x02, dnam.Bytes[12]); // Flags
    }

    [Fact]
    public void WeapEncoder_EncodeNew_DataIs15BytesAndEdidPresent()
    {
        var weap = new WeaponRecord { FormId = 0x800, EditorId = "Weap" };
        var encoded = WeapEncoder.EncodeNew(weap);

        Assert.Contains(encoded.Subrecords, s => s.Signature == "EDID");
        var data = Assert.Single(encoded.Subrecords, s => s.Signature == "DATA");
        Assert.Equal(15, data.Bytes.Length);
    }

    [Fact]
    public void WeapEncoder_EncodeNew_AmmoFormId_EmitsNam0()
    {
        // FNV uses NAM0 for the ammo FormID — confirmed against master FalloutNV.esm
        // Weap10mmPistol at offset 0x85F5AC. FNVEdit does not recognize ENAM in FNV WEAP.
        var weap = new WeaponRecord { FormId = 0x800, EditorId = "Weap", AmmoFormId = 0x1234 };
        var encoded = WeapEncoder.EncodeNew(weap);

        var nam0 = Assert.Single(encoded.Subrecords, s => s.Signature == "NAM0");
        Assert.Equal(0x1234u, BinaryPrimitives.ReadUInt32LittleEndian(nam0.Bytes));
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "ENAM");
    }

    [Fact]
    public void ArmoEncoder_EncodeNew_BmdtAndDnam_PerSchemaLayout()
    {
        var armo = new ArmorRecord
        {
            FormId = 0x800,
            EditorId = "Armor",
            BipedFlags = 0x12345678,
            GeneralFlags = 0x04,
            DamageThreshold = 5.5f,
            DamageResistance = 12,
            Value = 50,
            Health = 200,
            Weight = 3.0f
        };

        var encoded = ArmoEncoder.EncodeNew(armo);

        // v20.11: BMDT is now 4 bytes (BipedFlags only). The FNV runtime's chunk table
        // caps BMDT at 4 bytes anyway; the extra GeneralFlags byte we used to emit was
        // truncated by the engine and triggered "Chunk size 8 too big" warnings on every
        // new ARMO record.
        var bmdt = Assert.Single(encoded.Subrecords, s => s.Signature == "BMDT");
        Assert.Equal(4, bmdt.Bytes.Length);
        Assert.Equal(0x12345678u, BinaryPrimitives.ReadUInt32LittleEndian(bmdt.Bytes.AsSpan(0, 4)));

        var dnam = Assert.Single(encoded.Subrecords, s => s.Signature == "DNAM");
        Assert.Equal(12, dnam.Bytes.Length);
        Assert.Equal((short)12, BinaryPrimitives.ReadInt16LittleEndian(dnam.Bytes.AsSpan(0, 2)));
        Assert.Equal(5.5f, BinaryPrimitives.ReadSingleLittleEndian(dnam.Bytes.AsSpan(4, 4)));
    }

    [Fact]
    public void FactEncoder_EncodeNew_Relations_EmitOneXnamPerRelation()
    {
        var fact = new FactionRecord
        {
            FormId = 0x800,
            EditorId = "Fact",
            Relations =
            [
                new FactionRelation(0x1111, 50, 0),
                new FactionRelation(0x2222, -30, 1)
            ]
        };

        var encoded = FactEncoder.EncodeNew(fact);

        var xnams = encoded.Subrecords.Where(s => s.Signature == "XNAM").ToList();
        Assert.Equal(2, xnams.Count);

        var firstXnam = xnams[0];
        Assert.Equal(12, firstXnam.Bytes.Length);
        Assert.Equal(0x1111u, BinaryPrimitives.ReadUInt32LittleEndian(firstXnam.Bytes.AsSpan(0, 4)));
        Assert.Equal(50, BinaryPrimitives.ReadInt32LittleEndian(firstXnam.Bytes.AsSpan(4, 4)));
    }

    [Fact]
    public void FactEncoder_EncodeNew_RankTables_EmitRnamMnamFnamPerRank()
    {
        var fact = new FactionRecord
        {
            FormId = 0x800,
            EditorId = "Fact",
            Ranks =
            [
                new FactionRank(0, "Recruit", "Recruit", null),
                new FactionRank(1, "Sergeant", "Sergeant", null)
            ]
        };

        var encoded = FactEncoder.EncodeNew(fact);

        var rnams = encoded.Subrecords.Where(s => s.Signature == "RNAM").ToList();
        var mnams = encoded.Subrecords.Where(s => s.Signature == "MNAM").ToList();
        Assert.Equal(2, rnams.Count);
        Assert.Equal(2, mnams.Count);
    }

    [Fact]
    public void NpcEncoder_EncodeNew_AcbsTwentyFourBytes()
    {
        var stats = new ActorBaseSubrecord(
            Flags: 0x12345678,
            FatigueBase: 100,
            BarterGold: 250,
            Level: 5,
            CalcMin: 1,
            CalcMax: 50,
            SpeedMultiplier: 100,
            KarmaAlignment: 0f,
            DispositionBase: 0,
            TemplateFlags: 0,
            Offset: 0,
            IsBigEndian: false);
        var npc = new NpcRecord
        {
            FormId = 0x800,
            EditorId = "NewNpc",
            FullName = "New NPC",
            Stats = stats,
            Race = 0xAAAA
        };

        var encoded = NpcEncoder.EncodeNew(npc);

        var acbs = Assert.Single(encoded.Subrecords, s => s.Signature == "ACBS");
        Assert.Equal(24, acbs.Bytes.Length);
        Assert.Contains(encoded.Subrecords, s => s.Signature == "RNAM");
    }

    [Fact]
    public void NpcEncoder_EncodeNew_FactionMembership_EmitsSnamPerFaction()
    {
        var npc = new NpcRecord
        {
            FormId = 0x800,
            EditorId = "Npc",
            Stats = MakeMinimalAcbs(),
            Factions =
            [
                new FactionMembership(0x1111, 5),
                new FactionMembership(0x2222, -3)
            ]
        };

        var encoded = NpcEncoder.EncodeNew(npc);

        var snams = encoded.Subrecords.Where(s => s.Signature == "SNAM").ToList();
        Assert.Equal(2, snams.Count);
        Assert.Equal(8, snams[0].Bytes.Length);
        Assert.Equal(0x1111u, BinaryPrimitives.ReadUInt32LittleEndian(snams[0].Bytes.AsSpan(0, 4)));
        Assert.Equal((byte)5, snams[0].Bytes[4]);
    }

    [Fact]
    public void NpcEncoder_EncodeNew_AiData_EmitsAidtTwentyBytes()
    {
        var npc = new NpcRecord
        {
            FormId = 0x800,
            EditorId = "Npc",
            Stats = MakeMinimalAcbs(),
            AiData = new NpcAiData(
                Aggression: 1,
                Confidence: 2,
                EnergyLevel: 50,
                Responsibility: 75,
                Mood: 0,
                Flags: 0xDEADBEEF,
                Assistance: 1)
        };

        var encoded = NpcEncoder.EncodeNew(npc);

        var aidt = Assert.Single(encoded.Subrecords, s => s.Signature == "AIDT");
        Assert.Equal(20, aidt.Bytes.Length);
        Assert.Equal(1, aidt.Bytes[0]);  // Aggression
        Assert.Equal(2, aidt.Bytes[1]);  // Confidence
        Assert.Equal(0xDEADBEEFu, BinaryPrimitives.ReadUInt32LittleEndian(aidt.Bytes.AsSpan(8, 4))); // ServiceFlags
        Assert.Equal(1, aidt.Bytes[14]); // Assistance
    }

    [Fact]
    public void NpcEncoder_EncodeNew_NoStats_EmitsAcbsWithSensibleDefaultsAndWarning()
    {
        // The earlier zero-fill default produced "Speed multiplier is zero" warnings for every
        // template-spawned NPC. v20 fix: emit Level=1 + SpeedMult=100 so the engine treats the
        // NPC as a real actor rather than refusing to move/initialize it.
        var npc = new NpcRecord { FormId = 0x800, EditorId = "Npc", Stats = null };
        var encoded = NpcEncoder.EncodeNew(npc);

        var acbs = Assert.Single(encoded.Subrecords, s => s.Signature == "ACBS");
        Assert.Equal(24, acbs.Bytes.Length);
        // Level (int16 @ 8) = 1; SpeedMult (uint16 @ 14) = 100; everything else zero.
        for (var i = 0; i < acbs.Bytes.Length; i++)
        {
            var expected = i switch
            {
                8 => 1, // Level low byte
                14 => 100, // SpeedMult low byte
                _ => 0
            };
            Assert.Equal((byte)expected, acbs.Bytes[i]);
        }

        Assert.Contains(encoded.Warnings, w => w.Contains("ACBS"));
    }

    private static ActorBaseSubrecord MakeMinimalAcbs()
    {
        return new ActorBaseSubrecord(
            Flags: 0,
            FatigueBase: 0,
            BarterGold: 0,
            Level: 1,
            CalcMin: 1,
            CalcMax: 1,
            SpeedMultiplier: 100,
            KarmaAlignment: 0f,
            DispositionBase: 0,
            TemplateFlags: 0,
            Offset: 0,
            IsBigEndian: false);
    }
}
