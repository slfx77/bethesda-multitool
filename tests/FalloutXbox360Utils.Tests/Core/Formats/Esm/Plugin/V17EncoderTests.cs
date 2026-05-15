using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Magic;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     v17 tests covering the medium-tier encoder batch:
///     CREA, CLAS, SOUN, TXST, LTEX, CHAL, BPTD, ENCH, SPEL, PERK.
/// </summary>
public class V17EncoderTests
{
    // ====================================================================================
    // ClasEncoder
    // ====================================================================================

    [Fact]
    public void ClasEncoder_EncodeNew_CanonicalOrder()
    {
        var clas = new ClassRecord
        {
            FormId = 0x2100,
            EditorId = "Guard",
            FullName = "Guard",
            Description = "Protects the law.",
            Icon = "icons/class/guard.dds",
            TagSkills = [3, 5, 7, -1],
            Flags = 0x02,
            BarterFlags = 0x01,
            TrainingSkill = 5,
            TrainingLevel = 50,
            AttributeWeights = [5, 5, 5, 5, 5, 5, 5]
        };

        var encoded = ClasEncoder.EncodeNew(clas);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();

        Assert.Equal(["EDID", "FULL", "DESC", "ICON", "DATA", "ATTR"], sigs);

        var data = Assert.Single(encoded.Subrecords, s => s.Signature == "DATA").Bytes;
        Assert.Equal(28, data.Length);
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0, 4)));
        Assert.Equal(0x02u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16, 4)));
        Assert.Equal(5, data[24]);
        Assert.Equal(50, data[25]);
    }

    // ====================================================================================
    // ChalEncoder
    // ====================================================================================

    [Fact]
    public void ChalEncoder_EncodeNew_DataLayoutMatchesPdb()
    {
        var chal = new ChallengeRecord
        {
            FormId = 0x2200,
            EditorId = "FirstKill",
            FullName = "First Kill",
            ChallengeType = 1,
            Threshold = 10,
            Flags = 0x03,
            Interval = 5,
            Value1 = 0xAA,
            Value2 = 0xBB,
            Value3 = 0xCC,
            Script = 0x1234
        };

        var encoded = ChalEncoder.EncodeNew(chal);
        var data = Assert.Single(encoded.Subrecords, s => s.Signature == "DATA").Bytes;

        Assert.Equal(24, data.Length);
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0, 4)));
        Assert.Equal(10, BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4, 4)));
        Assert.Equal((ushort)0x03, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(8, 2)));
        Assert.Equal(5, BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12, 4)));
    }

    // ====================================================================================
    // SounEncoder
    // ====================================================================================

    [Fact]
    public void SounEncoder_EncodeNew_SnddLayout()
    {
        var soun = new SoundRecord
        {
            FormId = 0x2300,
            EditorId = "SfxBeep",
            FileName = "sfx/beep.wav",
            MinAttenuationDistance = 10,
            MaxAttenuationDistance = 100,
            Flags = 0x01,
            StaticAttenuation = -100,
            StartTime = 5,
            EndTime = 20,
            RandomPercentChance = 25
        };

        var encoded = SounEncoder.EncodeNew(soun);
        var sndd = Assert.Single(encoded.Subrecords, s => s.Signature == "SNDD").Bytes;

        Assert.Equal(36, sndd.Length);
        Assert.Equal(10, sndd[0]);
        Assert.Equal(100, sndd[1]);
        Assert.Equal(25, (sbyte)sndd[2]);
        Assert.Equal(0x01u, BinaryPrimitives.ReadUInt32LittleEndian(sndd.AsSpan(4, 4)));
        Assert.Equal((short)-100, BinaryPrimitives.ReadInt16LittleEndian(sndd.AsSpan(8, 2)));
        Assert.Equal(20, sndd[10]); // EndTime
        Assert.Equal(5, sndd[11]);  // StartTime
    }

    // ====================================================================================
    // TxstEncoder
    // ====================================================================================

    [Fact]
    public void TxstEncoder_EncodeNew_AllSixTexturePathsInOrder()
    {
        var txst = new TextureSetRecord
        {
            FormId = 0x2400,
            EditorId = "MetalBox",
            DiffuseTexture = "textures/metal/d.dds",
            NormalTexture = "textures/metal/n.dds",
            EnvironmentTexture = "textures/metal/em.dds",
            GlowTexture = "textures/metal/g.dds",
            ParallaxTexture = "textures/metal/p.dds",
            EnvironmentMapTexture = "textures/metal/cube.dds",
            Flags = 0x07
        };

        var encoded = TxstEncoder.EncodeNew(txst);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();

        Assert.Equal(["EDID", "TX00", "TX01", "TX02", "TX03", "TX04", "TX05", "DNAM"], sigs);
        var dnam = Assert.Single(encoded.Subrecords, s => s.Signature == "DNAM").Bytes;
        Assert.Equal(2, dnam.Length);
        Assert.Equal((ushort)0x07, BinaryPrimitives.ReadUInt16LittleEndian(dnam));
    }

    [Fact]
    public void TxstEncoder_EncodeNew_OmitsMissingTextures()
    {
        var txst = new TextureSetRecord
        {
            FormId = 0x2400,
            EditorId = "Minimal",
            DiffuseTexture = "d.dds"
        };

        var encoded = TxstEncoder.EncodeNew(txst);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();
        Assert.Equal(["EDID", "TX00", "DNAM"], sigs);
    }

    [Fact]
    public void LtexEncoder_EncodeNew_EmitsLandscapeTextureReferences()
    {
        var ltex = new LandscapeTextureRecord
        {
            FormId = 0x2450,
            EditorId = "StripDirt",
            IconPath = "textures/landscape/dirt.dds",
            SmallIconPath = "textures/landscape/dirt_small.dds",
            TextureSetFormId = 0x01020304,
            HavokData = [1, 2, 3],
            SpecularData = [4],
            GrassFormIds = [0x11111111, 0x22222222]
        };

        var encoded = LtexEncoder.EncodeNew(ltex);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();

        Assert.Equal(["EDID", "ICON", "MICO", "TNAM", "HNAM", "SNAM", "GNAM", "GNAM"], sigs);
        var tnam = Assert.Single(encoded.Subrecords, s => s.Signature == "TNAM").Bytes;
        Assert.Equal(0x01020304u, BinaryPrimitives.ReadUInt32LittleEndian(tnam));
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.Single(encoded.Subrecords, s => s.Signature == "HNAM").Bytes);
        Assert.Equal(new byte[] { 4 }, Assert.Single(encoded.Subrecords, s => s.Signature == "SNAM").Bytes);
        Assert.Equal([0x11111111u, 0x22222222u],
            encoded.Subrecords
                .Where(s => s.Signature == "GNAM")
                .Select(s => BinaryPrimitives.ReadUInt32LittleEndian(s.Bytes))
                .ToList());
    }

    // ====================================================================================
    // BptdEncoder
    // ====================================================================================

    [Fact]
    public void BptdEncoder_EncodeNew_BptnAndBpnnPaired()
    {
        var bptd = new BodyPartDataRecord
        {
            FormId = 0x2500,
            EditorId = "HumanBody",
            ModelPath = "characters/body.nif",
            PartNames = ["Head", "Torso", "ArmL"],
            NodeNames = ["Bip01 Head", "Bip01 Spine", "Bip01 L Arm"],
            TextureCount = 3
        };

        var encoded = BptdEncoder.EncodeNew(bptd);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();

        Assert.Equal(["EDID", "MODL", "BPTN", "BPNN", "BPTN", "BPNN", "BPTN", "BPNN", "NAM5"], sigs);
    }

    [Fact]
    public void BptdEncoder_EncodeNew_WarnsOnMismatchedPartAndNodeLists()
    {
        var bptd = new BodyPartDataRecord
        {
            FormId = 0x2500,
            EditorId = "X",
            PartNames = ["A", "B"],
            NodeNames = ["A"] // mismatched
        };

        var encoded = BptdEncoder.EncodeNew(bptd);
        Assert.Contains(encoded.Warnings, w => w.Contains("part names vs"));
        // Only the matched prefix is emitted (1 pair).
        Assert.Single(encoded.Subrecords.Where(s => s.Signature == "BPTN"));
        Assert.Single(encoded.Subrecords.Where(s => s.Signature == "BPNN"));
    }

    // ====================================================================================
    // EnchEncoder
    // ====================================================================================

    [Fact]
    public void EnchEncoder_EncodeNew_EnitLayoutAndEffectPairs()
    {
        var ench = new EnchantmentRecord
        {
            FormId = 0x2600,
            EditorId = "FireDmg",
            FullName = "Burning",
            EnchantType = 2, // Weapon
            ChargeAmount = 100,
            EnchantCost = 25,
            Flags = 0x01,
            Effects =
            [
                new EnchantmentEffect
                {
                    EffectFormId = 0x111u,
                    Magnitude = 10.0f,
                    Area = 5,
                    Duration = 3,
                    Type = 2, // Target
                    ActorValue = -1
                }
            ]
        };

        var encoded = EnchEncoder.EncodeNew(ench);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();
        Assert.Equal(["EDID", "FULL", "ENIT", "EFID", "EFIT"], sigs);

        var enit = Assert.Single(encoded.Subrecords, s => s.Signature == "ENIT").Bytes;
        Assert.Equal(16, enit.Length);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(enit.AsSpan(0, 4)));
        Assert.Equal(100u, BinaryPrimitives.ReadUInt32LittleEndian(enit.AsSpan(4, 4)));
        Assert.Equal(25u, BinaryPrimitives.ReadUInt32LittleEndian(enit.AsSpan(8, 4)));
        Assert.Equal((byte)0x01, enit[12]);

        var efit = Assert.Single(encoded.Subrecords, s => s.Signature == "EFIT").Bytes;
        Assert.Equal(20, efit.Length);
        Assert.Equal(10.0f, BinaryPrimitives.ReadSingleLittleEndian(efit.AsSpan(0, 4)));
        Assert.Equal(-1, BinaryPrimitives.ReadInt32LittleEndian(efit.AsSpan(16, 4)));
    }

    // ====================================================================================
    // SpelEncoder
    // ====================================================================================

    [Fact]
    public void SpelEncoder_EncodeNew_SpitLayoutAndReusesEfitHelper()
    {
        var spel = new SpellRecord
        {
            FormId = 0x2700,
            EditorId = "HealSelf",
            FullName = "Heal Self",
            Type = SpellType.Spell,
            Cost = 50,
            Level = 10,
            Flags = 0x02,
            Effects =
            [
                new EnchantmentEffect { EffectFormId = 0x222u, Magnitude = 50.0f, Type = 0 }
            ]
        };

        var encoded = SpelEncoder.EncodeNew(spel);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();
        Assert.Equal(["EDID", "FULL", "SPIT", "EFID", "EFIT"], sigs);

        var spit = Assert.Single(encoded.Subrecords, s => s.Signature == "SPIT").Bytes;
        Assert.Equal(16, spit.Length);
        Assert.Equal((uint)SpellType.Spell, BinaryPrimitives.ReadUInt32LittleEndian(spit.AsSpan(0, 4)));
        Assert.Equal(50u, BinaryPrimitives.ReadUInt32LittleEndian(spit.AsSpan(4, 4)));
        Assert.Equal(10u, BinaryPrimitives.ReadUInt32LittleEndian(spit.AsSpan(8, 4)));
        Assert.Equal((byte)0x02, spit[12]);
    }

    // ====================================================================================
    // PerkEncoder
    // ====================================================================================

    [Fact]
    public void PerkEncoder_EncodeNew_Data5BytesAndCtdaConditions()
    {
        var perk = new PerkRecord
        {
            FormId = 0x2800,
            EditorId = "FastShot",
            FullName = "Fast Shot",
            Description = "Fire faster.",
            IconPath = "icons/perk/fastshot.dds",
            Trait = 0,
            MinLevel = 2,
            Ranks = 1,
            Playable = 1,
            Conditions =
            [
                new PerkCondition
                {
                    FunctionIndex = 0x0E, // GetActorValue
                    Parameter1 = 41,      // Guns
                    ComparisonOperator = 3, // >=
                    ComparisonValue = 50.0f
                }
            ]
        };

        var encoded = PerkEncoder.EncodeNew(perk);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();
        Assert.Equal(["EDID", "FULL", "DESC", "ICON", "DATA", "CTDA"], sigs);

        var data = Assert.Single(encoded.Subrecords, s => s.Signature == "DATA").Bytes;
        Assert.Equal(5, data.Length);
        Assert.Equal(0, data[0]);
        Assert.Equal(2, data[1]);
        Assert.Equal(1, data[2]);
        Assert.Equal(1, data[3]);

        var ctda = Assert.Single(encoded.Subrecords, s => s.Signature == "CTDA").Bytes;
        Assert.Equal(28, ctda.Length);
        Assert.Equal(3, ctda[0]); // ComparisonOperator
        Assert.Equal(50.0f, BinaryPrimitives.ReadSingleLittleEndian(ctda.AsSpan(4, 4)));
        Assert.Equal((ushort)0x0E, BinaryPrimitives.ReadUInt16LittleEndian(ctda.AsSpan(8, 2)));
    }

    [Fact]
    public void PerkEncoder_EncodeNew_WarnsOnPrkeEntriesDeferred()
    {
        var perk = new PerkRecord
        {
            FormId = 0x2800,
            EditorId = "X",
            Entries = [new PerkEntry { Type = 2, Rank = 1, Priority = 0 }]
        };

        var encoded = PerkEncoder.EncodeNew(perk);
        Assert.Contains(encoded.Warnings, w => w.Contains("entry chain") && w.Contains("deferred"));
    }

    // ====================================================================================
    // CreaEncoder
    // ====================================================================================

    [Fact]
    public void CreaEncoder_EncodeNew_DataLayoutAndCanonicalOrder()
    {
        var crea = new CreatureRecord
        {
            FormId = 0x2900,
            EditorId = "Radroach",
            FullName = "Radroach",
            ModelPath = "creatures/radroach.nif",
            Stats = new ActorBaseSubrecord(
                Flags: 0x00000020,
                FatigueBase: 100,
                BarterGold: 0,
                Level: 1,
                CalcMin: 1,
                CalcMax: 1,
                SpeedMultiplier: 100,
                KarmaAlignment: 0,
                DispositionBase: 50,
                TemplateFlags: 0,
                Offset: 0,
                IsBigEndian: false),
            CreatureType = 1, // Mutated Animal
            CombatSkill = 30,
            MagicSkill = 0,
            StealthSkill = 50,
            AttackDamage = 5,
            Factions = [new FactionMembership(0xABC, 0)],
            Spells = [0x111],
            Packages = [0x222]
        };

        var encoded = CreaEncoder.EncodeNew(crea);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();

        Assert.Equal(
            ["EDID", "FULL", "MODL", "ACBS", "SNAM", "PKID", "SPLO", "DATA"],
            sigs);

        var data = Assert.Single(encoded.Subrecords, s => s.Signature == "DATA").Bytes;
        Assert.Equal(17, data.Length);
        Assert.Equal(1, data[0]); // CreatureType
        Assert.Equal(30, data[1]); // CombatSkill
        Assert.Equal(0, data[2]); // MagicSkill
        Assert.Equal(50, data[3]); // StealthSkill
        Assert.Equal((short)5, BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(8, 2)));
    }

    [Fact]
    public void CreaEncoder_EncodeNew_WarnsWhenStatsMissing()
    {
        var crea = new CreatureRecord { FormId = 0x2900, EditorId = "T" };
        var encoded = CreaEncoder.EncodeNew(crea);
        Assert.Contains(encoded.Warnings, w => w.Contains("no ACBS"));
        Assert.Equal(24, Assert.Single(encoded.Subrecords, s => s.Signature == "ACBS").Bytes.Length);
    }

    // ====================================================================================
    // Cross-encoder warning check
    // ====================================================================================

    [Fact]
    public void AllV17Encoders_EmitWarningWhenEditorIdMissing()
    {
        Assert.Contains(CreaEncoder.EncodeNew(new CreatureRecord { FormId = 1 }).Warnings,
            w => w.Contains("has no EditorId"));
        Assert.Contains(ClasEncoder.EncodeNew(new ClassRecord { FormId = 1 }).Warnings,
            w => w.Contains("has no EditorId"));
        Assert.Contains(SounEncoder.EncodeNew(new SoundRecord { FormId = 1 }).Warnings,
            w => w.Contains("has no EditorId"));
        Assert.Contains(TxstEncoder.EncodeNew(new TextureSetRecord { FormId = 1 }).Warnings,
            w => w.Contains("has no EditorId"));
        Assert.Contains(LtexEncoder.EncodeNew(new LandscapeTextureRecord { FormId = 1 }).Warnings,
            w => w.Contains("has no EditorId"));
        Assert.Contains(ChalEncoder.EncodeNew(new ChallengeRecord { FormId = 1 }).Warnings,
            w => w.Contains("has no EditorId"));
        Assert.Contains(BptdEncoder.EncodeNew(new BodyPartDataRecord { FormId = 1 }).Warnings,
            w => w.Contains("has no EditorId"));
        Assert.Contains(EnchEncoder.EncodeNew(new EnchantmentRecord { FormId = 1 }).Warnings,
            w => w.Contains("has no EditorId"));
        Assert.Contains(SpelEncoder.EncodeNew(new SpellRecord { FormId = 1 }).Warnings,
            w => w.Contains("has no EditorId"));
        Assert.Contains(PerkEncoder.EncodeNew(new PerkRecord { FormId = 1 }).Warnings,
            w => w.Contains("has no EditorId"));
    }
}
