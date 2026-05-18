using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     v5 pre-testing bugfix tests — verifies emissions added after v5 closing summary
///     identified them as known limitations: ALCH ENIT, WEAP/ARMO ETYP, NPC_ DATA/DNAM/FaceGen.
/// </summary>
public class BugfixEncoderTests
{
    [Fact]
    public void AlchEncoder_EncodeNew_WithEnitFields_EmitsEnitTwentyBytes()
    {
        var alch = new ConsumableRecord
        {
            FormId = 0x800,
            EditorId = "Stim",
            Weight = 0.5f,
            Value = 100u,
            Flags = 0x02u,
            AddictionFormId = 0xABCD,
            AddictionChance = 0.25f,
            WithdrawalEffectFormId = 0x1234
        };

        var encoded = AlchEncoder.EncodeNew(alch);

        var enit = Assert.Single(encoded.Subrecords, s => s.Signature == "ENIT");
        Assert.Equal(20, enit.Bytes.Length);
        Assert.Equal(100u, BinaryPrimitives.ReadUInt32LittleEndian(enit.Bytes.AsSpan(0, 4)));
        Assert.Equal(0x02u, BinaryPrimitives.ReadUInt32LittleEndian(enit.Bytes.AsSpan(4, 4)));
        Assert.Equal(0xABCDu, BinaryPrimitives.ReadUInt32LittleEndian(enit.Bytes.AsSpan(8, 4)));
        Assert.Equal(0.25f, BinaryPrimitives.ReadSingleLittleEndian(enit.Bytes.AsSpan(12, 4)));
        Assert.Equal(0x1234u, BinaryPrimitives.ReadUInt32LittleEndian(enit.Bytes.AsSpan(16, 4)));
    }

    [Fact]
    public void AlchEncoder_EncodeNew_NoEnitFields_OmitsEnit()
    {
        var alch = new ConsumableRecord { FormId = 0x800, EditorId = "Stim", Weight = 0.5f };
        var encoded = AlchEncoder.EncodeNew(alch);
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "ENIT");
    }

    [Fact]
    public void WeapEncoder_EncodeNew_NonNoneEquipmentType_EmitsEtypAsInt32Enum()
    {
        var weap = new WeaponRecord
        {
            FormId = 0x800,
            EditorId = "Weap",
            EquipmentType = EquipmentType.SmallGuns
        };

        var encoded = WeapEncoder.EncodeNew(weap);

        var etyp = Assert.Single(encoded.Subrecords, s => s.Signature == "ETYP");
        Assert.Equal(4, etyp.Bytes.Length);
        Assert.Equal((int)EquipmentType.SmallGuns,
            BinaryPrimitives.ReadInt32LittleEndian(etyp.Bytes));
    }

    [Fact]
    public void WeapEncoder_EncodeNew_NoneEquipmentType_OmitsEtyp()
    {
        var weap = new WeaponRecord { FormId = 0x800, EditorId = "Weap", EquipmentType = EquipmentType.None };
        var encoded = WeapEncoder.EncodeNew(weap);
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "ETYP");
    }

    [Fact]
    public void ArmoEncoder_EncodeNew_BodyWearEquipment_EmitsEtyp()
    {
        var armo = new ArmorRecord
        {
            FormId = 0x800,
            EditorId = "Armor",
            EquipmentType = EquipmentType.BodyWear,
            BipedFlags = 0
        };

        var encoded = ArmoEncoder.EncodeNew(armo);

        var etyp = Assert.Single(encoded.Subrecords, s => s.Signature == "ETYP");
        Assert.Equal(4, etyp.Bytes.Length);
        Assert.Equal(7, BinaryPrimitives.ReadInt32LittleEndian(etyp.Bytes)); // BodyWear = 7
    }

    [Fact]
    public void NpcEncoder_EncodeNew_WithSpecialStats_EmitsDataElevenBytes()
    {
        var npc = new NpcRecord
        {
            FormId = 0x800,
            EditorId = "Npc",
            Stats = MakeMinimalAcbs(),
            SpecialStats = [5, 6, 7, 8, 9, 4, 3] // ST, PE, EN, CH, IN, AG, LK
        };

        var encoded = NpcEncoder.EncodeNew(npc);

        var data = Assert.Single(encoded.Subrecords, s => s.Signature == "DATA");
        Assert.Equal(11, data.Bytes.Length);
        // bytes 0-3 = BaseHealth synthesized from Endurance × 5 + 50 + Level × 10.
        // Endurance (SPECIAL[2]) = 7, Level (from MakeMinimalAcbs) = 1 → 7*5 + 50 + 10 = 95.
        Assert.Equal(95, BinaryPrimitives.ReadInt32LittleEndian(data.Bytes.AsSpan(0, 4)));
        // bytes 4-10 = SPECIAL
        Assert.Equal((byte)5, data.Bytes[4]);
        Assert.Equal((byte)6, data.Bytes[5]);
        Assert.Equal((byte)7, data.Bytes[6]);
        Assert.Equal((byte)8, data.Bytes[7]);
        Assert.Equal((byte)9, data.Bytes[8]);
        Assert.Equal((byte)4, data.Bytes[9]);
        Assert.Equal((byte)3, data.Bytes[10]);
    }

    [Fact]
    public void NpcEncoder_EncodeNew_WithExplicitBaseHealth_EmitsThatValue()
    {
        var npc = new NpcRecord
        {
            FormId = 0x801,
            EditorId = "Npc",
            Stats = MakeMinimalAcbs(),
            SpecialStats = [5, 6, 7, 8, 9, 4, 3],
            BaseHealth = 250
        };

        var encoded = NpcEncoder.EncodeNew(npc);

        var data = Assert.Single(encoded.Subrecords, s => s.Signature == "DATA");
        Assert.Equal(250, BinaryPrimitives.ReadInt32LittleEndian(data.Bytes.AsSpan(0, 4)));
    }

    [Fact]
    public void NpcEncoder_EncodeNew_WithZeroBaseHealth_FallsBackToSynthesis()
    {
        var npc = new NpcRecord
        {
            FormId = 0x802,
            EditorId = "Npc",
            Stats = MakeMinimalAcbs(),
            SpecialStats = [5, 6, 7, 8, 9, 4, 3],
            BaseHealth = 0 // Treated as "unknown" — synthesize.
        };

        var encoded = NpcEncoder.EncodeNew(npc);

        var data = Assert.Single(encoded.Subrecords, s => s.Signature == "DATA");
        Assert.Equal(95, BinaryPrimitives.ReadInt32LittleEndian(data.Bytes.AsSpan(0, 4)));
    }

    [Fact]
    public void NpcEncoder_EncodeNew_WithNullStats_SynthesizesWithLevelOne()
    {
        var npc = new NpcRecord
        {
            FormId = 0x803,
            EditorId = "Npc",
            // No Stats — synthesis treats Level as 1.
            SpecialStats = [5, 6, 10, 8, 9, 4, 3] // Endurance = 10
        };

        var encoded = NpcEncoder.EncodeNew(npc);

        var data = Assert.Single(encoded.Subrecords, s => s.Signature == "DATA");
        // 10*5 + 50 + 1*10 = 110.
        Assert.Equal(110, BinaryPrimitives.ReadInt32LittleEndian(data.Bytes.AsSpan(0, 4)));
    }

    [Fact]
    public void NpcEncoder_EncodeNew_WithSkills_EmitsDnamTwentyEightBytes()
    {
        var skills = new byte[14];
        for (var i = 0; i < 14; i++)
        {
            skills[i] = (byte)(20 + i);
        }

        var npc = new NpcRecord
        {
            FormId = 0x800,
            EditorId = "Npc",
            Stats = MakeMinimalAcbs(),
            Skills = skills
        };

        var encoded = NpcEncoder.EncodeNew(npc);

        var dnam = Assert.Single(encoded.Subrecords, s => s.Signature == "DNAM");
        Assert.Equal(28, dnam.Bytes.Length);
        // bytes 0-13 = base skills
        for (var i = 0; i < 14; i++)
        {
            Assert.Equal((byte)(20 + i), dnam.Bytes[i]);
        }

        // bytes 14-27 = mod offsets, all zero
        for (var i = 14; i < 28; i++)
        {
            Assert.Equal(0, dnam.Bytes[i]);
        }
    }

    [Fact]
    public void NpcEncoder_EncodeNew_WithFaceGenMorphs_EmitsFggsFggaFgts()
    {
        var fggs = new float[50];
        var fgga = new float[30];
        var fgts = new float[50];
        for (var i = 0; i < 50; i++)
        {
            fggs[i] = i * 0.01f;
            fgts[i] = i * 0.02f;
        }

        for (var i = 0; i < 30; i++)
        {
            fgga[i] = i * 0.03f;
        }

        var npc = new NpcRecord
        {
            FormId = 0x800,
            EditorId = "Npc",
            Stats = MakeMinimalAcbs(),
            FaceGenGeometrySymmetric = fggs,
            FaceGenGeometryAsymmetric = fgga,
            FaceGenTextureSymmetric = fgts
        };

        var encoded = NpcEncoder.EncodeNew(npc);

        var fggsBytes = Assert.Single(encoded.Subrecords, s => s.Signature == "FGGS");
        Assert.Equal(200, fggsBytes.Bytes.Length);
        // Last float should be index 49 * 0.01f. Use the same expression to get the same
        // float-precision result as the encoder.
        Assert.Equal(49 * 0.01f, BinaryPrimitives.ReadSingleLittleEndian(fggsBytes.Bytes.AsSpan(196, 4)));

        var fggaBytes = Assert.Single(encoded.Subrecords, s => s.Signature == "FGGA");
        Assert.Equal(120, fggaBytes.Bytes.Length);

        var fgtsBytes = Assert.Single(encoded.Subrecords, s => s.Signature == "FGTS");
        Assert.Equal(200, fgtsBytes.Bytes.Length);
    }

    [Fact]
    public void NpcEncoder_EncodeNew_WithoutFaceGen_OmitsAllMorphSubrecords()
    {
        var npc = new NpcRecord { FormId = 0x800, EditorId = "Npc", Stats = MakeMinimalAcbs() };
        var encoded = NpcEncoder.EncodeNew(npc);

        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "FGGS");
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "FGGA");
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "FGTS");
    }

    [Fact]
    public void NpcEncoder_EncodeNew_WrongLengthSpecialStats_OmitsData()
    {
        // Defensive: model with malformed length should be skipped, not corrupt the record.
        var npc = new NpcRecord
        {
            FormId = 0x800,
            EditorId = "Npc",
            Stats = MakeMinimalAcbs(),
            SpecialStats = [1, 2, 3] // wrong length, should be 7
        };

        var encoded = NpcEncoder.EncodeNew(npc);
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "DATA");
    }

    [Fact]
    public void NpcEncoder_EncodeNew_WithMasterRaceTemplate_IsRenderableTemplateSafe()
    {
        var npc = new NpcRecord
        {
            FormId = 0x800,
            EditorId = "Npc",
            Race = 0x33184,
            Stats = MakeMinimalAcbs()
        };

        var encoded = NpcEncoder.EncodeNew(
            npc,
            new HashSet<uint> { 0x123456 },
            new Dictionary<uint, uint> { [0x33184] = 0x123456 });

        Assert.True(PluginBuilder.NpcHasRenderableTemplate(encoded.Subrecords));
        var tplt = Assert.Single(encoded.Subrecords, s => s.Signature == "TPLT");
        Assert.Equal(0x123456u, BinaryPrimitives.ReadUInt32LittleEndian(tplt.Bytes));
        var acbs = Assert.Single(encoded.Subrecords, s => s.Signature == "ACBS");
        Assert.Equal(0x0001, BinaryPrimitives.ReadUInt16LittleEndian(acbs.Bytes.AsSpan(22, 2)));
    }

    [Fact]
    public void NpcEncoder_EncodeNew_WithoutMasterTemplate_IsRenderableTemplateUnsafe()
    {
        var npc = new NpcRecord
        {
            FormId = 0x800,
            EditorId = "Npc",
            Race = 0x33184,
            Stats = MakeMinimalAcbs()
        };

        var encoded = NpcEncoder.EncodeNew(
            npc,
            new HashSet<uint>(),
            new Dictionary<uint, uint>());

        Assert.False(PluginBuilder.NpcHasRenderableTemplate(encoded.Subrecords));
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "TPLT");
        var acbs = Assert.Single(encoded.Subrecords, s => s.Signature == "ACBS");
        Assert.Equal(0x0000, BinaryPrimitives.ReadUInt16LittleEndian(acbs.Bytes.AsSpan(22, 2)));
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
