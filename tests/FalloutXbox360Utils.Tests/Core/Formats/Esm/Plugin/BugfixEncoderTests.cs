using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     Encoder bugfix regression tests — covers subrecord emissions added or fixed in
///     response to in-game-load failures or post-validation gaps:
///     - ALCH ENIT (addiction/withdrawal data) and EFID/EFIT effect pairs.
///     - WEAP/ARMO ETYP (equipment-type as int32 enum).
///     - NPC_ DATA / DNAM / FaceGen morphs / ACBS speed-multiplier sanitization /
///       template-renderable safety.
///     - ArmoEncoder/ArmaEncoder BMDT-before-MODL ordering (post-model BMDT_ID size table
///       caps at 4 bytes and was truncating GeneralFlags).
///     - ScptEncoder stub-script filtering (empty EditorID + no bytecode → skip record).
///     - InfoEncoder defensive PNAM filtering (sentinel 0 / 0xFFFFFFFF / self-reference).
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

    // ====================================================================================
    // ArmoEncoder / ArmaEncoder BMDT ordering (BMDT must precede MODL)
    // ====================================================================================

    [Fact]
    public void ArmoEncoder_EncodeNew_PlacesBmdtBeforeModl()
    {
        var armo = new ArmorRecord
        {
            FormId = 0x800,
            EditorId = "ArmorBmdtOrder",
            FullName = "Test Armor",
            ModelPath = "armor\\test.nif",
            BipedFlags = 0x4u,
            GeneralFlags = 0x10
        };

        var encoded = ArmoEncoder.EncodeNew(armo);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();

        var bmdtIndex = sigs.IndexOf("BMDT");
        var modlIndex = sigs.IndexOf("MODL");

        Assert.True(bmdtIndex >= 0, "BMDT must be emitted");
        Assert.True(modlIndex >= 0, "MODL must be emitted");
        Assert.True(bmdtIndex < modlIndex,
            $"BMDT must precede MODL (got BMDT@{bmdtIndex}, MODL@{modlIndex})");
    }

    [Fact]
    public void ArmaEncoder_EncodeNew_PlacesBmdtBeforeModl()
    {
        var arma = new ArmaRecord
        {
            FormId = 0x800,
            EditorId = "ArmaBmdtOrder",
            FullName = "Test Armor Addon",
            MaleModelPath = "armor\\test_m.nif",
            FemaleModelPath = "armor\\test_f.nif",
            BipedFlags = 0x4u,
            GeneralFlags = 0x10
        };

        var encoded = ArmaEncoder.EncodeNew(arma);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();

        var bmdtIndex = sigs.IndexOf("BMDT");
        var modlIndex = sigs.IndexOf("MODL");

        Assert.True(bmdtIndex >= 0, "BMDT must be emitted");
        Assert.True(modlIndex >= 0, "MODL must be emitted");
        Assert.True(bmdtIndex < modlIndex,
            $"BMDT must precede MODL (got BMDT@{bmdtIndex}, MODL@{modlIndex})");
    }

    // ====================================================================================
    // NpcEncoder ACBS speed-multiplier sanitization
    // ====================================================================================

    [Fact]
    public void NpcEncoder_EncodeNew_StatsNull_EmitsAcbsWithDefaults()
    {
        var npc = new NpcRecord
        {
            FormId = 0x800,
            EditorId = "NpcNoStats",
            Stats = null
        };

        var encoded = NpcEncoder.EncodeNew(npc);
        var acbs = Assert.Single(encoded.Subrecords, s => s.Signature == "ACBS");

        Assert.Equal(24, acbs.Bytes.Length);
        // Level (int16 at offset 8) should be 1 (engine default for spawnable NPC).
        Assert.Equal((short)1, BinaryPrimitives.ReadInt16LittleEndian(acbs.Bytes.AsSpan(8, 2)));
        // SpeedMult (uint16 at offset 14) should be 100 (normal walk/run speed).
        Assert.Equal((ushort)100, BinaryPrimitives.ReadUInt16LittleEndian(acbs.Bytes.AsSpan(14, 2)));
    }

    [Fact]
    public void NpcEncoder_EncodeNew_StatsWithZeroSpeedMult_BumpsTo100()
    {
        var npc = new NpcRecord
        {
            FormId = 0x800,
            EditorId = "NpcZeroSpeed",
            Stats = new ActorBaseSubrecord(
                Flags: 0,
                FatigueBase: 50,
                BarterGold: 200,
                Level: 5,
                CalcMin: 1,
                CalcMax: 10,
                SpeedMultiplier: 0,
                KarmaAlignment: 0f,
                DispositionBase: 50,
                TemplateFlags: 0,
                Offset: 0,
                IsBigEndian: false)
        };

        var encoded = NpcEncoder.EncodeNew(npc);
        var acbs = Assert.Single(encoded.Subrecords, s => s.Signature == "ACBS");

        // Source said 0; encoder must rewrite to 100 so the engine doesn't refuse to move the NPC.
        Assert.Equal((ushort)100, BinaryPrimitives.ReadUInt16LittleEndian(acbs.Bytes.AsSpan(14, 2)));
        // Other fields preserved verbatim.
        Assert.Equal((short)5, BinaryPrimitives.ReadInt16LittleEndian(acbs.Bytes.AsSpan(8, 2)));
        Assert.Equal((ushort)200, BinaryPrimitives.ReadUInt16LittleEndian(acbs.Bytes.AsSpan(6, 2)));
    }

    [Fact]
    public void NpcEncoder_EncodeNew_StatsWithNonZeroSpeedMult_PreservesValue()
    {
        var npc = new NpcRecord
        {
            FormId = 0x800,
            EditorId = "NpcFastSpeed",
            Stats = new ActorBaseSubrecord(
                Flags: 0,
                FatigueBase: 0,
                BarterGold: 0,
                Level: 1,
                CalcMin: 1,
                CalcMax: 1,
                SpeedMultiplier: 150,
                KarmaAlignment: 0f,
                DispositionBase: 0,
                TemplateFlags: 0,
                Offset: 0,
                IsBigEndian: false)
        };

        var encoded = NpcEncoder.EncodeNew(npc);
        var acbs = Assert.Single(encoded.Subrecords, s => s.Signature == "ACBS");
        Assert.Equal((ushort)150, BinaryPrimitives.ReadUInt16LittleEndian(acbs.Bytes.AsSpan(14, 2)));
    }

    // ====================================================================================
    // AlchEncoder EFID/EFIT effect emission
    // ====================================================================================

    [Fact]
    public void AlchEncoder_EncodeNew_WithEffects_EmitsEfidEfitPairs()
    {
        var alch = new ConsumableRecord
        {
            FormId = 0x800,
            EditorId = "FoodWithEffects",
            Weight = 0.5f,
            Effects =
            [
                new EnchantmentEffect
                {
                    EffectFormId = 0x1234,
                    Magnitude = 10f,
                    Area = 0,
                    Duration = 60,
                    Type = 0,
                    ActorValue = 24
                },
                new EnchantmentEffect
                {
                    EffectFormId = 0x5678,
                    Magnitude = 5f,
                    Area = 0,
                    Duration = 30,
                    Type = 0,
                    ActorValue = 25
                }
            ]
        };

        var encoded = AlchEncoder.EncodeNew(alch);

        var efids = encoded.Subrecords.Where(s => s.Signature == "EFID").ToList();
        var efits = encoded.Subrecords.Where(s => s.Signature == "EFIT").ToList();

        Assert.Equal(2, efids.Count);
        Assert.Equal(2, efits.Count);

        Assert.Equal(0x1234u, BinaryPrimitives.ReadUInt32LittleEndian(efids[0].Bytes));
        Assert.Equal(0x5678u, BinaryPrimitives.ReadUInt32LittleEndian(efids[1].Bytes));

        // EFIT is 20 bytes (Magnitude/Area/Duration/Type/ActorValue).
        Assert.Equal(20, efits[0].Bytes.Length);
        Assert.Equal(10f, BinaryPrimitives.ReadSingleLittleEndian(efits[0].Bytes.AsSpan(0, 4)));
        Assert.Equal(60u, BinaryPrimitives.ReadUInt32LittleEndian(efits[0].Bytes.AsSpan(8, 4)));
        Assert.Equal(24, BinaryPrimitives.ReadInt32LittleEndian(efits[0].Bytes.AsSpan(16, 4)));
    }

    [Fact]
    public void AlchEncoder_EncodeNew_WithEffects_DoesNotWarnAboutDeferredEffects()
    {
        var alch = new ConsumableRecord
        {
            FormId = 0x800,
            EditorId = "Food",
            Effects = [new EnchantmentEffect { EffectFormId = 0x1234 }]
        };

        var encoded = AlchEncoder.EncodeNew(alch);
        Assert.DoesNotContain(encoded.Warnings, w => w.Contains("deferred"));
    }

    [Fact]
    public void AlchEncoder_EncodeNew_NoEffects_OmitsEfidEfit()
    {
        var alch = new ConsumableRecord { FormId = 0x800, EditorId = "Food", Weight = 0.5f };
        var encoded = AlchEncoder.EncodeNew(alch);
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "EFID");
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "EFIT");
    }

    // ====================================================================================
    // ScptEncoder stub-script filtering
    // ====================================================================================

    [Fact]
    public void ScptEncoder_EncodeNew_EmptyStub_ReturnsEmptySubrecords()
    {
        var script = new ScriptRecord
        {
            FormId = 0x800,
            EditorId = null,
            CompiledData = null,
            Variables = [],
            ReferencedObjects = []
        };

        var encoded = ScptEncoder.EncodeNew(script);

        // No subrecords → PluginBuilder will skip the record entirely.
        Assert.Empty(encoded.Subrecords);
        Assert.Contains(encoded.Warnings, w => w.Contains("stub"));
    }

    [Fact]
    public void ScptEncoder_EncodeNew_HasEditorId_EmitsRecord()
    {
        var script = new ScriptRecord
        {
            FormId = 0x800,
            EditorId = "TestScript"
        };

        var encoded = ScptEncoder.EncodeNew(script);
        Assert.Contains(encoded.Subrecords, s => s.Signature == "EDID");
        Assert.Contains(encoded.Subrecords, s => s.Signature == "SCHR");
    }

    [Fact]
    public void ScptEncoder_EncodeNew_HasCompiledDataButNoEditorId_EmitsRecord()
    {
        var script = new ScriptRecord
        {
            FormId = 0x800,
            EditorId = null,
            CompiledData = new byte[] { 0x01, 0x02, 0x03 },
            IsCompiled = true,
            CompiledSize = 3
        };

        var encoded = ScptEncoder.EncodeNew(script);
        Assert.Contains(encoded.Subrecords, s => s.Signature == "SCDA");
    }

    // ====================================================================================
    // InfoEncoder defensive PNAM filtering
    // ====================================================================================

    [Fact]
    public void InfoEncoder_EncodeNew_PreviousInfoZero_OmitsPnam()
    {
        var info = new DialogueRecord { FormId = 0x800, PreviousInfo = 0u };
        var encoded = InfoEncoder.EncodeNew(info);
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "PNAM");
    }

    [Fact]
    public void InfoEncoder_EncodeNew_PreviousInfoSentinelFFFFFFFF_OmitsPnam()
    {
        var info = new DialogueRecord { FormId = 0x800, PreviousInfo = 0xFFFFFFFFu };
        var encoded = InfoEncoder.EncodeNew(info);
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "PNAM");
    }

    [Fact]
    public void InfoEncoder_EncodeNew_PreviousInfoSelfReference_OmitsPnam()
    {
        var info = new DialogueRecord { FormId = 0x800, PreviousInfo = 0x800u };
        var encoded = InfoEncoder.EncodeNew(info);
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "PNAM");
    }

    [Fact]
    public void InfoEncoder_EncodeNew_PreviousInfoValid_EmitsPnam()
    {
        var info = new DialogueRecord { FormId = 0x800, PreviousInfo = 0x12345u };
        var encoded = InfoEncoder.EncodeNew(info);
        var pnam = Assert.Single(encoded.Subrecords, s => s.Signature == "PNAM");
        Assert.Equal(0x12345u, BinaryPrimitives.ReadUInt32LittleEndian(pnam.Bytes));
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
