using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Dialogue;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Magic;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Magic;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using Xunit;
using static FalloutXbox360Utils.Tests.Helpers.EsmTestRecordMakers;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     Tests for the complex new-record encoders: WEAP, ARMO, FACT, NPC_, ARMA, RCPE,
///     RCCT, COBJ, QUST (top-level + per-stage + per-target CTDA + CIS1/CIS2), plus the
///     medium-complexity batch (CREA, CLAS, SOUN, TXST, LTEX, CHAL, BPTD, ENCH, SPEL,
///     PERK) and the large encoders (MGEF, WRLD, RACE). Verifies subrecord byte layouts
///     against PDB schemas, optional-field emission, and canonical fopdoc ordering.
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

    // ====================================================================================
    // ARMA, RCPE — actionable-bundle new-record encoders; WEAP VATS subrecord
    // ====================================================================================

    // ====================================================================================
    // ARMA encoder
    // ====================================================================================

    [Fact]
    public void ArmaEncoder_EncodeNew_CanonicalSubrecordOrder()
    {
        var arma = new ArmaRecord
        {
            FormId = 0xB00,
            EditorId = "TestArmaAddon",
            FullName = "Test Body",
            Bounds = new ObjectBounds { X1 = -10, Y1 = -20, Z1 = 0, X2 = 10, Y2 = 20, Z2 = 30 },
            BipedFlags = 0x00000004u,
            GeneralFlags = 0x80,
            MaleModelPath = "armor_male.nif",
            FemaleModelPath = "armor_female.nif",
            MaleFirstPersonModelPath = "armor_male_fp.nif",
            FemaleFirstPersonModelPath = "armor_female_fp.nif",
            Value = 100,
            MaxCondition = 1000,
            Weight = 5.0f
        };

        var encoded = ArmaEncoder.EncodeNew(arma);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();

        Assert.Equal(["EDID", "OBND", "FULL", "BMDT", "MODL", "MOD2", "MOD3", "MOD4", "DATA"], sigs);
    }

    [Fact]
    public void ArmaEncoder_EncodeNew_BmdtLayout()
    {
        var arma = new ArmaRecord
        {
            FormId = 0xB00,
            EditorId = "T",
            BipedFlags = 0x12345678u,
            GeneralFlags = 0x42
        };

        var encoded = ArmaEncoder.EncodeNew(arma);
        var bmdt = Assert.Single(encoded.Subrecords, s => s.Signature == "BMDT").Bytes;

        // v20.11: BMDT shrunk to 4 bytes (BipedFlags only). The FNV runtime caps BMDT at
        // 4 bytes in its chunk table — writing 8 triggered "Chunk size 8 too big" warnings
        // and the GeneralFlags byte was truncated by the engine anyway.
        Assert.Equal(4, bmdt.Length);
        Assert.Equal(0x12345678u, BinaryPrimitives.ReadUInt32LittleEndian(bmdt.AsSpan(0, 4)));
    }

    [Fact]
    public void ArmaEncoder_EncodeNew_DataLayout()
    {
        var arma = new ArmaRecord
        {
            FormId = 0xB00,
            EditorId = "T",
            Value = 250,
            MaxCondition = 5000,
            Weight = 12.5f
        };

        var encoded = ArmaEncoder.EncodeNew(arma);
        var data = Assert.Single(encoded.Subrecords, s => s.Signature == "DATA").Bytes;

        Assert.Equal(12, data.Length);
        Assert.Equal(250, BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0, 4)));
        Assert.Equal(5000, BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4, 4)));
        Assert.Equal(12.5f, BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(8, 4)));
    }

    [Fact]
    public void ArmaEncoder_EncodeNew_OmitsOptionalModelsWhenNull()
    {
        var arma = new ArmaRecord
        {
            FormId = 0xB00,
            EditorId = "T",
            MaleModelPath = "male.nif"
            // FemaleModelPath / MaleFirstPersonModelPath / FemaleFirstPersonModelPath all null
        };

        var encoded = ArmaEncoder.EncodeNew(arma);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();

        // Mandatory: EDID, BMDT, DATA. Plus MODL (male) since it's set.
        Assert.Equal(["EDID", "BMDT", "MODL", "DATA"], sigs);
    }

    [Fact]
    public void ArmaEncoder_EncodeNew_NoEditorIdEmitsWarning()
    {
        var arma = new ArmaRecord { FormId = 0xB00 };
        var encoded = ArmaEncoder.EncodeNew(arma);
        Assert.Contains(encoded.Warnings, w => w.Contains("has no EditorId"));
    }

    // ====================================================================================
    // RCPE encoder
    // ====================================================================================

    [Fact]
    public void RcpeEncoder_EncodeNew_CanonicalSubrecordOrder()
    {
        var recipe = new RecipeRecord
        {
            FormId = 0xC00,
            EditorId = "RecipeTest",
            FullName = "Bottle Cap Mine",
            RequiredSkill = 10,
            RequiredSkillLevel = 25,
            CategoryFormId = 0x100u,
            SubcategoryFormId = 0x200u,
            Ingredients =
            [
                new RecipeIngredient { ItemFormId = 0x301u, Count = 2 },
                new RecipeIngredient { ItemFormId = 0x302u, Count = 1 }
            ],
            Outputs =
            [
                new RecipeOutput { ItemFormId = 0x401u, Count = 1 }
            ]
        };

        var encoded = RcpeEncoder.EncodeNew(recipe);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();

        Assert.Equal(["EDID", "FULL", "DATA", "RCIL", "RCQY", "RCIL", "RCQY", "RCOD", "RCQY"], sigs);
    }

    [Fact]
    public void RcpeEncoder_EncodeNew_DataLayout()
    {
        var recipe = new RecipeRecord
        {
            FormId = 0xC00,
            EditorId = "T",
            RequiredSkill = 41, // Guns
            RequiredSkillLevel = 50,
            CategoryFormId = 0xABCDEFu,
            SubcategoryFormId = 0x12345678u
        };

        var encoded = RcpeEncoder.EncodeNew(recipe);
        var data = Assert.Single(encoded.Subrecords, s => s.Signature == "DATA").Bytes;

        Assert.Equal(16, data.Length);
        Assert.Equal(41, BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0, 4)));
        Assert.Equal(50u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4)));
        Assert.Equal(0xABCDEFu, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8, 4)));
        Assert.Equal(0x12345678u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12, 4)));
    }

    [Fact]
    public void RcpeEncoder_EncodeNew_IngredientAndOutputPairings()
    {
        var recipe = new RecipeRecord
        {
            FormId = 0xC00,
            EditorId = "T",
            Ingredients = [new RecipeIngredient { ItemFormId = 0xAAAu, Count = 3 }],
            Outputs = [new RecipeOutput { ItemFormId = 0xBBBu, Count = 7 }]
        };

        var encoded = RcpeEncoder.EncodeNew(recipe);

        var rcil = Assert.Single(encoded.Subrecords, s => s.Signature == "RCIL").Bytes;
        Assert.Equal(0xAAAu, BinaryPrimitives.ReadUInt32LittleEndian(rcil));

        var rcod = Assert.Single(encoded.Subrecords, s => s.Signature == "RCOD").Bytes;
        Assert.Equal(0xBBBu, BinaryPrimitives.ReadUInt32LittleEndian(rcod));

        var rcqys = encoded.Subrecords.Where(s => s.Signature == "RCQY").ToList();
        Assert.Equal(2, rcqys.Count);
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(rcqys[0].Bytes));
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(rcqys[1].Bytes));
    }

    [Fact]
    public void RcpeEncoder_EncodeNew_NoIngredientsOrOutputsEmitsOnlyEdidAndData()
    {
        var recipe = new RecipeRecord { FormId = 0xC00, EditorId = "Empty" };
        var encoded = RcpeEncoder.EncodeNew(recipe);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();

        Assert.Equal(["EDID", "DATA"], sigs);
    }

    // ====================================================================================
    // WEAP VATS subrecord
    // ====================================================================================

    [Fact]
    public void WeapEncoder_EncodeNew_EmitsVatsWhenSet()
    {
        var weap = new WeaponRecord
        {
            FormId = 0x700,
            EditorId = "VatsGun",
            ModelPath = "gun.nif",
            VatsAttack = new VatsAttackData
            {
                EffectFormId = 0x12345u,
                ActionPointCost = 25.0f,
                DamageMultiplier = 1.5f,
                SkillRequired = 40.0f,
                IsSilent = true,
                RequiresMod = false,
                ExtraFlags = 0x03
            }
        };

        var encoded = WeapEncoder.EncodeNew(weap);
        var vats = Assert.Single(encoded.Subrecords, s => s.Signature == "VATS").Bytes;

        Assert.Equal(20, vats.Length);
        Assert.Equal(0x12345u, BinaryPrimitives.ReadUInt32LittleEndian(vats.AsSpan(0, 4)));
        Assert.Equal(25.0f, BinaryPrimitives.ReadSingleLittleEndian(vats.AsSpan(4, 4)));
        Assert.Equal(1.5f, BinaryPrimitives.ReadSingleLittleEndian(vats.AsSpan(8, 4)));
        Assert.Equal(40.0f, BinaryPrimitives.ReadSingleLittleEndian(vats.AsSpan(12, 4)));
        Assert.Equal(1, vats[16]); // IsSilent = true
        Assert.Equal(0, vats[17]); // RequiresMod = false
        Assert.Equal(0x03, vats[18]); // ExtraFlags
        Assert.Equal(0, vats[19]); // padding
    }

    [Fact]
    public void WeapEncoder_EncodeNew_OmitsVatsWhenNull()
    {
        var weap = new WeaponRecord
        {
            FormId = 0x700,
            EditorId = "PlainGun",
            ModelPath = "gun.nif",
            VatsAttack = null
        };

        var encoded = WeapEncoder.EncodeNew(weap);
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "VATS");
        Assert.DoesNotContain(encoded.Warnings, w => w.Contains("VATS subrecord emission deferred"));
    }

    // ====================================================================================
    // RCCT, COBJ — new-record encoders; ARMA texture-hash/icon/DNAM extensions;
    // QUST top-level CTDA + CIS1/CIS2 emission
    // ====================================================================================

    // ====================================================================================
    // RcctEncoder
    // ====================================================================================

    [Fact]
    public void RcctEncoder_EncodeNew_CanonicalSubrecordOrder()
    {
        var rcct = new RecipeCategoryRecord
        {
            FormId = 0xD00,
            EditorId = "WeaponModRepair",
            FullName = "Weapon Mod Repair",
            Flags = 0x01
        };

        var encoded = RcctEncoder.EncodeNew(rcct);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();

        Assert.Equal(["EDID", "FULL", "DATA"], sigs);
        var data = Assert.Single(encoded.Subrecords, s => s.Signature == "DATA");
        Assert.Single(data.Bytes);
        Assert.Equal(0x01, data.Bytes[0]);
    }

    [Fact]
    public void RcctEncoder_EncodeNew_OmitsFullWhenNull()
    {
        var rcct = new RecipeCategoryRecord { FormId = 0xD00, EditorId = "Empty", Flags = 0 };
        var encoded = RcctEncoder.EncodeNew(rcct);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();

        Assert.Equal(["EDID", "DATA"], sigs);
    }

    [Fact]
    public void RcctEncoder_EncodeNew_MissingEditorIdEmitsWarning()
    {
        var rcct = new RecipeCategoryRecord { FormId = 0xD00 };
        var encoded = RcctEncoder.EncodeNew(rcct);
        Assert.Contains(encoded.Warnings, w => w.Contains("has no EditorId"));
    }

    // ====================================================================================
    // CobjEncoder
    // ====================================================================================

    [Fact]
    public void CobjEncoder_EncodeNew_CanonicalSubrecordOrderWithAllFields()
    {
        var cobj = new ConstructibleObjectRecord
        {
            FormId = 0xE00,
            EditorId = "RecipeStimpak",
            FullName = "Make Stimpak",
            Bounds = new ObjectBounds { X1 = -1, Y1 = -1, Z1 = -1, X2 = 1, Y2 = 1, Z2 = 1 },
            ModelPath = "stimpak.nif",
            TextureHashData = [0xAA, 0xBB],
            Ingredients =
            [
                new InventoryItem(0x101u, 1),
                new InventoryItem(0x102u, 2)
            ],
            Conditions =
            [
                new DialogueCondition
                {
                    Type = 0x20,
                    FunctionIndex = 449,
                    Parameter1String = "MedicineSkill"
                }
            ],
            CreatedItemFormId = 0xCAFE,
            WorkbenchKeywordFormId = 0xBEEF
        };

        var encoded = CobjEncoder.EncodeNew(cobj);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();

        Assert.Equal(
            ["EDID", "OBND", "FULL", "MODL", "MODT", "COCT",
                "CNTO", "CNTO", "CTDA", "CIS1", "CNAM", "BNAM"],
            sigs);
    }

    [Fact]
    public void CobjEncoder_EncodeNew_CoctMatchesCntoCount()
    {
        var cobj = new ConstructibleObjectRecord
        {
            FormId = 0xE00,
            EditorId = "R",
            Ingredients =
            [
                new InventoryItem(0x1u, 1),
                new InventoryItem(0x2u, 2),
                new InventoryItem(0x3u, 3)
            ],
            CreatedItemFormId = 0x100
        };

        var encoded = CobjEncoder.EncodeNew(cobj);
        var coct = Assert.Single(encoded.Subrecords, s => s.Signature == "COCT");
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(coct.Bytes));
        Assert.Equal(3, encoded.Subrecords.Count(s => s.Signature == "CNTO"));
    }

    [Fact]
    public void CobjEncoder_EncodeNew_CntoLayout()
    {
        var cobj = new ConstructibleObjectRecord
        {
            FormId = 0xE00,
            EditorId = "R",
            Ingredients = [new InventoryItem(0xABCDEFu, 42)],
            CreatedItemFormId = 0x100
        };

        var encoded = CobjEncoder.EncodeNew(cobj);
        var cnto = Assert.Single(encoded.Subrecords, s => s.Signature == "CNTO").Bytes;

        Assert.Equal(8, cnto.Length);
        Assert.Equal(0xABCDEFu, BinaryPrimitives.ReadUInt32LittleEndian(cnto.AsSpan(0, 4)));
        Assert.Equal(42, BinaryPrimitives.ReadInt32LittleEndian(cnto.AsSpan(4, 4)));
    }

    [Fact]
    public void CobjEncoder_EncodeNew_OmitsCoctAndCntoWhenNoIngredients()
    {
        var cobj = new ConstructibleObjectRecord
        {
            FormId = 0xE00,
            EditorId = "R",
            CreatedItemFormId = 0x100
        };

        var encoded = CobjEncoder.EncodeNew(cobj);
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "COCT");
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "CNTO");
    }

    [Fact]
    public void CobjEncoder_EncodeNew_OmitsBnamWhenNull()
    {
        var cobj = new ConstructibleObjectRecord
        {
            FormId = 0xE00,
            EditorId = "R",
            CreatedItemFormId = 0x100
            // WorkbenchKeywordFormId not set
        };

        var encoded = CobjEncoder.EncodeNew(cobj);
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "BNAM");
    }

    [Fact]
    public void CobjEncoder_EncodeNew_MissingCnamWarns()
    {
        var cobj = new ConstructibleObjectRecord { FormId = 0xE00, EditorId = "R" };
        var encoded = CobjEncoder.EncodeNew(cobj);
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "CNAM");
        Assert.Contains(encoded.Warnings, w => w.Contains("CreatedItemFormId"));
    }

    // ====================================================================================
    // ArmaEncoder v14 extensions
    // ====================================================================================

    [Fact]
    public void ArmaEncoder_EncodeNew_EmitsModtAfterModl()
    {
        var arma = new ArmaRecord
        {
            FormId = 0xF00,
            EditorId = "T",
            MaleModelPath = "m.nif",
            MaleTextureHashData = [0x01, 0x02]
        };

        var encoded = ArmaEncoder.EncodeNew(arma);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();
        var modlIdx = sigs.IndexOf("MODL");
        var modtIdx = sigs.IndexOf("MODT");

        Assert.True(modlIdx >= 0);
        Assert.Equal(modlIdx + 1, modtIdx);
    }

    [Fact]
    public void ArmaEncoder_EncodeNew_TextureHashesFollowEachModelVariant()
    {
        var arma = new ArmaRecord
        {
            FormId = 0xF00,
            EditorId = "T",
            MaleModelPath = "m.nif",
            FemaleModelPath = "f.nif",
            MaleFirstPersonModelPath = "mfp.nif",
            FemaleFirstPersonModelPath = "ffp.nif",
            MaleTextureHashData = [0x01],
            FemaleTextureHashData = [0x02],
            MaleFirstPersonTextureHashData = [0x03],
            FemaleFirstPersonTextureHashData = [0x04]
        };

        var encoded = ArmaEncoder.EncodeNew(arma);
        var sigs = encoded.Subrecords.Where(s => s.Signature.StartsWith("MOD") || s.Signature.StartsWith("MO"))
            .Select(s => s.Signature).ToList();

        // Each model variant is immediately followed by its texture-hash subrecord.
        Assert.Equal(["MODL", "MODT", "MOD2", "MO2T", "MOD3", "MO3T", "MOD4", "MO4T"], sigs);
    }

    [Fact]
    public void ArmaEncoder_EncodeNew_EmitsIconAndMic2BeforeData()
    {
        var arma = new ArmaRecord
        {
            FormId = 0xF00,
            EditorId = "T",
            MaleIconPath = "icons/m.dds",
            FemaleIconPath = "icons/f.dds"
        };

        var encoded = ArmaEncoder.EncodeNew(arma);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();
        var iconIdx = sigs.IndexOf("ICON");
        var mic2Idx = sigs.IndexOf("MIC2");
        var dataIdx = sigs.IndexOf("DATA");

        Assert.True(iconIdx >= 0 && mic2Idx >= 0 && dataIdx >= 0);
        Assert.True(iconIdx < dataIdx);
        Assert.True(mic2Idx < dataIdx);
    }

    [Fact]
    public void ArmaEncoder_EncodeNew_EmitsDnamWhenNonZero()
    {
        var arma = new ArmaRecord { FormId = 0xF00, EditorId = "T", DetectionSoundLevel = 2 };
        var encoded = ArmaEncoder.EncodeNew(arma);
        var dnam = Assert.Single(encoded.Subrecords, s => s.Signature == "DNAM");
        Assert.Single(dnam.Bytes);
        Assert.Equal(2, dnam.Bytes[0]);
    }

    [Fact]
    public void ArmaEncoder_EncodeNew_OmitsDnamWhenLoudDefault()
    {
        var arma = new ArmaRecord { FormId = 0xF00, EditorId = "T", DetectionSoundLevel = 0 };
        var encoded = ArmaEncoder.EncodeNew(arma);
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "DNAM");
    }

    // ====================================================================================
    // QustEncoder top-level CTDA + CIS1/CIS2
    // ====================================================================================

    [Fact]
    public void QustEncoder_EncodeNew_EmitsCtdaBetweenDataAndIndx()
    {
        var quest = new QuestRecord
        {
            FormId = 0x1000,
            EditorId = "MQ01",
            Conditions =
            [
                new DialogueCondition { Type = 0x20, FunctionIndex = 76 }
            ],
            Stages = [new QuestStage { Index = 10 }]
        };

        var encoded = QustEncoder.EncodeNew(quest);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();
        var dataIdx = sigs.IndexOf("DATA");
        var ctdaIdx = sigs.IndexOf("CTDA");
        var indxIdx = sigs.IndexOf("INDX");

        Assert.True(dataIdx < ctdaIdx);
        Assert.True(ctdaIdx < indxIdx);
    }

    [Fact]
    public void QustEncoder_EncodeNew_EmitsCis1AndCis2WhenBothSet()
    {
        var quest = new QuestRecord
        {
            FormId = 0x1000,
            EditorId = "MQ01",
            Conditions =
            [
                new DialogueCondition
                {
                    Type = 0x20,
                    FunctionIndex = 449,
                    Parameter1String = "QuestVar",
                    Parameter2String = "Stage"
                }
            ]
        };

        var encoded = QustEncoder.EncodeNew(quest);
        var sigs = encoded.Subrecords
            .Where(s => s.Signature is "CTDA" or "CIS1" or "CIS2")
            .Select(s => s.Signature).ToList();

        Assert.Equal(["CTDA", "CIS1", "CIS2"], sigs);
    }

    [Fact]
    public void QustEncoder_EncodeNew_OmitsConditionsWhenEmpty()
    {
        var quest = new QuestRecord { FormId = 0x1000, EditorId = "MQ01" };
        var encoded = QustEncoder.EncodeNew(quest);
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "CTDA");
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "CIS1");
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "CIS2");
    }

    // ====================================================================================
    // QustEncoder per-stage CTDA + CIS1/CIS2; per-target QSTA CTDA; ARMA ETYP/REPL
    // ====================================================================================

    // ====================================================================================
    // QustEncoder per-stage conditions
    // ====================================================================================

    [Fact]
    public void QustEncoder_EncodeNew_StageCtdaBetweenQsdtAndCnam()
    {
        var quest = new QuestRecord
        {
            FormId = 0x2000,
            EditorId = "TestQuest",
            Stages =
            [
                new QuestStage
                {
                    Index = 10,
                    Flags = 0x01,
                    LogEntry = "Started",
                    Conditions =
                    [
                        new DialogueCondition { Type = 0x20, FunctionIndex = 76 }
                    ]
                }
            ]
        };

        var encoded = QustEncoder.EncodeNew(quest);
        var sigs = encoded.Subrecords
            .Where(s => s.Signature is "INDX" or "QSDT" or "CTDA" or "CNAM")
            .Select(s => s.Signature).ToList();

        Assert.Equal(["INDX", "QSDT", "CTDA", "CNAM"], sigs);
    }

    [Fact]
    public void QustEncoder_EncodeNew_StageCis1Cis2EmittedWithStageCtda()
    {
        var quest = new QuestRecord
        {
            FormId = 0x2000,
            EditorId = "T",
            Stages =
            [
                new QuestStage
                {
                    Index = 5,
                    Conditions =
                    [
                        new DialogueCondition
                        {
                            Type = 0x20,
                            FunctionIndex = 449,
                            Parameter1String = "stagevar",
                            Parameter2String = "stage2"
                        }
                    ]
                }
            ]
        };

        var encoded = QustEncoder.EncodeNew(quest);
        var sigs = encoded.Subrecords
            .Where(s => s.Signature is "INDX" or "CTDA" or "CIS1" or "CIS2")
            .Select(s => s.Signature).ToList();

        Assert.Equal(["INDX", "CTDA", "CIS1", "CIS2"], sigs);
    }

    // ====================================================================================
    // QustEncoder per-target conditions
    // ====================================================================================

    [Fact]
    public void QustEncoder_EncodeNew_ObjectiveTargetEmitsQstaThenCtda()
    {
        var quest = new QuestRecord
        {
            FormId = 0x2000,
            EditorId = "T",
            Objectives =
            [
                new QuestObjective
                {
                    Index = 100,
                    DisplayText = "Kill Benny",
                    Targets =
                    [
                        new QuestObjectiveTarget
                        {
                            TargetFormId = 0xDEAD,
                            Flags = 0x01,
                            Conditions =
                            [
                                new DialogueCondition { Type = 0x20, FunctionIndex = 1 }
                            ]
                        }
                    ]
                }
            ]
        };

        var encoded = QustEncoder.EncodeNew(quest);
        var sigs = encoded.Subrecords
            .Where(s => s.Signature is "QOBJ" or "NNAM" or "QSTA" or "CTDA")
            .Select(s => s.Signature).ToList();

        Assert.Equal(["QOBJ", "NNAM", "QSTA", "CTDA"], sigs);
    }

    [Fact]
    public void QustEncoder_EncodeNew_QstaLayout()
    {
        var quest = new QuestRecord
        {
            FormId = 0x2000,
            EditorId = "T",
            Objectives =
            [
                new QuestObjective
                {
                    Index = 0,
                    Targets =
                    [
                        new QuestObjectiveTarget { TargetFormId = 0xABCDEFu, Flags = 0x42 }
                    ]
                }
            ]
        };

        var encoded = QustEncoder.EncodeNew(quest);
        var qsta = Assert.Single(encoded.Subrecords, s => s.Signature == "QSTA").Bytes;

        Assert.Equal(8, qsta.Length);
        Assert.Equal(0xABCDEFu, BinaryPrimitives.ReadUInt32LittleEndian(qsta.AsSpan(0, 4)));
        Assert.Equal(0x42, qsta[4]);
        Assert.Equal(0, qsta[5]);
        Assert.Equal(0, qsta[6]);
        Assert.Equal(0, qsta[7]);
    }

    [Fact]
    public void QustEncoder_EncodeNew_MultipleTargetsEachWithOwnConditions()
    {
        var quest = new QuestRecord
        {
            FormId = 0x2000,
            EditorId = "T",
            Objectives =
            [
                new QuestObjective
                {
                    Index = 0,
                    Targets =
                    [
                        new QuestObjectiveTarget
                        {
                            TargetFormId = 0xA,
                            Conditions = [new DialogueCondition { Type = 0x20, FunctionIndex = 1 }]
                        },
                        new QuestObjectiveTarget
                        {
                            TargetFormId = 0xB,
                            Conditions = [new DialogueCondition { Type = 0x20, FunctionIndex = 2 }]
                        }
                    ]
                }
            ]
        };

        var encoded = QustEncoder.EncodeNew(quest);
        var sigs = encoded.Subrecords
            .Where(s => s.Signature is "QSTA" or "CTDA")
            .Select(s => s.Signature).ToList();

        Assert.Equal(["QSTA", "CTDA", "QSTA", "CTDA"], sigs);
    }

    [Fact]
    public void QustEncoder_EncodeNew_AllConditionScopesCoexist()
    {
        // Top-level + per-stage + per-target conditions all in one quest.
        var quest = new QuestRecord
        {
            FormId = 0x2000,
            EditorId = "T",
            Conditions = [new DialogueCondition { Type = 0x20, FunctionIndex = 100 }],
            Stages =
            [
                new QuestStage
                {
                    Index = 10,
                    Conditions = [new DialogueCondition { Type = 0x20, FunctionIndex = 200 }]
                }
            ],
            Objectives =
            [
                new QuestObjective
                {
                    Index = 0,
                    Targets =
                    [
                        new QuestObjectiveTarget
                        {
                            TargetFormId = 0xC,
                            Conditions = [new DialogueCondition { Type = 0x20, FunctionIndex = 300 }]
                        }
                    ]
                }
            ]
        };

        var encoded = QustEncoder.EncodeNew(quest);
        Assert.Equal(3, encoded.Subrecords.Count(s => s.Signature == "CTDA"));

        // Verify function indices appear in scope order: top-level, stage, target.
        var ctdas = encoded.Subrecords.Where(s => s.Signature == "CTDA").ToList();
        Assert.Equal(100, BinaryPrimitives.ReadUInt16LittleEndian(ctdas[0].Bytes.AsSpan(8, 2)));
        Assert.Equal(200, BinaryPrimitives.ReadUInt16LittleEndian(ctdas[1].Bytes.AsSpan(8, 2)));
        Assert.Equal(300, BinaryPrimitives.ReadUInt16LittleEndian(ctdas[2].Bytes.AsSpan(8, 2)));
    }

    // ====================================================================================
    // ArmaEncoder ETYP + REPL
    // ====================================================================================

    [Fact]
    public void ArmaEncoder_EncodeNew_EmitsEtypAsInt32()
    {
        var arma = new ArmaRecord
        {
            FormId = 0xF00,
            EditorId = "T",
            EquipmentType = EquipmentType.BodyWear
        };

        var encoded = ArmaEncoder.EncodeNew(arma);
        var etyp = Assert.Single(encoded.Subrecords, s => s.Signature == "ETYP").Bytes;

        Assert.Equal(4, etyp.Length);
        Assert.Equal((int)EquipmentType.BodyWear, BinaryPrimitives.ReadInt32LittleEndian(etyp));
    }

    [Fact]
    public void ArmaEncoder_EncodeNew_OmitsEtypWhenNone()
    {
        var arma = new ArmaRecord
        {
            FormId = 0xF00,
            EditorId = "T",
            EquipmentType = EquipmentType.None
        };

        var encoded = ArmaEncoder.EncodeNew(arma);
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "ETYP");
    }

    [Fact]
    public void ArmaEncoder_EncodeNew_EmitsReplAsFormId()
    {
        var arma = new ArmaRecord
        {
            FormId = 0xF00,
            EditorId = "T",
            RepairItemListFormId = 0x12345u
        };

        var encoded = ArmaEncoder.EncodeNew(arma);
        var repl = Assert.Single(encoded.Subrecords, s => s.Signature == "REPL").Bytes;

        Assert.Equal(4, repl.Length);
        Assert.Equal(0x12345u, BinaryPrimitives.ReadUInt32LittleEndian(repl));
    }

    [Fact]
    public void ArmaEncoder_EncodeNew_OmitsReplWhenNull()
    {
        var arma = new ArmaRecord { FormId = 0xF00, EditorId = "T", RepairItemListFormId = null };
        var encoded = ArmaEncoder.EncodeNew(arma);
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "REPL");
    }

    [Fact]
    public void ArmaEncoder_EncodeNew_EtypAndReplFollowDnam()
    {
        var arma = new ArmaRecord
        {
            FormId = 0xF00,
            EditorId = "T",
            DetectionSoundLevel = 2,
            EquipmentType = EquipmentType.HeadWear,
            RepairItemListFormId = 0x100
        };

        var encoded = ArmaEncoder.EncodeNew(arma);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();
        var dataIdx = sigs.IndexOf("DATA");
        var dnamIdx = sigs.IndexOf("DNAM");
        var etypIdx = sigs.IndexOf("ETYP");
        var replIdx = sigs.IndexOf("REPL");

        Assert.True(dataIdx < dnamIdx);
        Assert.True(dnamIdx < etypIdx);
        Assert.True(etypIdx < replIdx);
    }

    // ====================================================================================
    // Medium-complexity encoders: CREA, CLAS, SOUN, TXST, LTEX, CHAL, BPTD, ENCH, SPEL, PERK
    // ====================================================================================

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
    public void TxstEncoder_EncodeNew_EmitsDodtAfterTexturesBeforeDnam()
    {
        var txst = new TextureSetRecord
        {
            FormId = 0x2401,
            EditorId = "BloodSplatter",
            DiffuseTexture = "textures/decals/blood_d.dds",
            DecalData = new TxstDecalData
            {
                MinWidth = 12.5f,
                MaxWidth = 64f,
                MinHeight = 12.5f,
                MaxHeight = 64f,
                Depth = 1.5f,
                Shininess = 0.25f,
                ParallaxScale = 1f,
                ParallaxPasses = 4,
                Flags = 0x03,
                ColorArgb = 0xFFAA1122
            }
        };

        var encoded = TxstEncoder.EncodeNew(txst);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();

        Assert.Equal(["EDID", "TX00", "DODT", "DNAM"], sigs);
        var dodt = Assert.Single(encoded.Subrecords, s => s.Signature == "DODT").Bytes;
        Assert.Equal(36, dodt.Length);
        Assert.Equal(12.5f, BinaryPrimitives.ReadSingleLittleEndian(dodt.AsSpan(0)));
        Assert.Equal(64f, BinaryPrimitives.ReadSingleLittleEndian(dodt.AsSpan(4)));
        Assert.Equal(1.5f, BinaryPrimitives.ReadSingleLittleEndian(dodt.AsSpan(16)));
        Assert.Equal((byte)4, dodt[28]);
        Assert.Equal((byte)0x03, dodt[29]);
        // Padding stays zero.
        Assert.Equal(0, dodt[30]);
        Assert.Equal(0, dodt[31]);
        Assert.Equal(0xFFAA1122u, BinaryPrimitives.ReadUInt32LittleEndian(dodt.AsSpan(32)));
    }

    [Fact]
    public void TxstEncoder_EncodeNew_OmitsDodtWhenDecalDataIsNull()
    {
        var txst = new TextureSetRecord
        {
            FormId = 0x2402,
            EditorId = "PlainTerrainTextureSet",
            DiffuseTexture = "textures/landscape/dirt_d.dds"
        };

        var encoded = TxstEncoder.EncodeNew(txst);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();
        Assert.DoesNotContain("DODT", sigs);
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
    // GrasEncoder
    // ====================================================================================

    [Fact]
    public void GrasEncoder_EncodeNew_EmitsCanonicalOrderWithDataPayload()
    {
        var gras = new GrassRecord
        {
            FormId = 0x2470,
            EditorId = "MojaveScrub",
            ModelPath = "meshes/landscape/grass/mojavescrub.nif",
            ModelBound = 12.5f,
            ModelTextureData = [0xAA, 0xBB, 0xCC, 0xDD],
            Data = new GrassData
            {
                Density = 200,
                MinSlope = 0,
                MaxSlope = 75,
                UnitsFromWaterAmount = 32,
                UnitsFromWaterType = 3,
                PositionRange = 256f,
                HeightRange = 16f,
                ColorRange = 0.25f,
                WavePeriod = 4.5f,
                Flags = 0x02
            }
        };

        var encoded = GrasEncoder.EncodeNew(gras);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();

        Assert.Equal(["EDID", "MODL", "MODB", "MODT", "DATA"], sigs);

        var modb = Assert.Single(encoded.Subrecords, s => s.Signature == "MODB").Bytes;
        Assert.Equal(4, modb.Length);
        Assert.Equal(12.5f, BinaryPrimitives.ReadSingleLittleEndian(modb));

        var data = Assert.Single(encoded.Subrecords, s => s.Signature == "DATA").Bytes;
        Assert.Equal(32, data.Length);
        Assert.Equal(200, data[0]);
        Assert.Equal(75, data[2]);
        Assert.Equal((ushort)32, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4)));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8)));
        Assert.Equal(256f, BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(12)));
        Assert.Equal(4.5f, BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(24)));
        Assert.Equal((byte)0x02, data[28]);
        // Trailing padding bytes stay zero.
        Assert.Equal(0, data[29]);
        Assert.Equal(0, data[30]);
        Assert.Equal(0, data[31]);
    }

    [Fact]
    public void GrasEncoder_EncodeNew_OmitsMissingOptionalFields()
    {
        var gras = new GrassRecord
        {
            FormId = 0x2471,
            EditorId = "EmptyGrass"
        };

        var encoded = GrasEncoder.EncodeNew(gras);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();

        Assert.Equal(["EDID"], sigs);
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
        // FNVEdit wbPERK canonical order: top-level CTDA conditions precede DATA.
        Assert.Equal(["EDID", "FULL", "DESC", "ICON", "CTDA", "DATA"], sigs);

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
    public void PerkEncoder_EncodeNew_EmitsPrkePrkfPerEntry()
    {
        // Entry-point perk (type 2) with no function — should produce PRKE, DATA, PRKF
        // (no EPFT/EPFD since FunctionType is null).
        var perk = new PerkRecord
        {
            FormId = 0x2800,
            EditorId = "X",
            Entries = [new PerkEntry { Type = 2, Rank = 1, Priority = 0 }]
        };

        var encoded = PerkEncoder.EncodeNew(perk);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();

        Assert.Contains("PRKE", sigs);
        Assert.Contains("PRKF", sigs);
        // PRKE should precede PRKF.
        Assert.True(sigs.IndexOf("PRKE") < sigs.IndexOf("PRKF"));
        // No "deferred" warning should be emitted now that entries are written.
        Assert.DoesNotContain(encoded.Warnings, w => w.Contains("deferred"));
    }

    [Fact]
    public void PerkEncoder_EncodeNew_PreservesUnknownEntryRawData()
    {
        var rawData = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var perk = new PerkRecord
        {
            FormId = 0x2800,
            EditorId = "UnknownEntry",
            Entries =
            [
                new PerkEntry
                {
                    Type = 0x7F,
                    Rank = 1,
                    Priority = 2,
                    RawEntryData = rawData
                }
            ]
        };

        var encoded = PerkEncoder.EncodeNew(perk);
        var entryData = encoded.Subrecords.Last(s => s.Signature == "DATA");

        Assert.Equal(rawData, entryData.Bytes);
        Assert.DoesNotContain(encoded.Warnings, w => w.Contains("unknown type"));
    }

    [Fact]
    public void PerkEncoder_EncodeNew_PreservesUnknownFunctionRawPayload()
    {
        var rawEpfd = new byte[] { 0x11, 0x22, 0x33 };
        var perk = new PerkRecord
        {
            FormId = 0x2800,
            EditorId = "UnknownFunction",
            Entries =
            [
                new PerkEntry
                {
                    Type = 2,
                    Rank = 1,
                    Priority = 0,
                    EntryPoint = 4,
                    FunctionType = 0x7E,
                    RawFunctionData = rawEpfd
                }
            ]
        };

        var encoded = PerkEncoder.EncodeNew(perk);
        var epfd = Assert.Single(encoded.Subrecords, s => s.Signature == "EPFD");

        Assert.Equal(rawEpfd, epfd.Bytes);
        Assert.DoesNotContain(encoded.Warnings, w => w.Contains("unknown FunctionType"));
    }

    [Fact]
    public void CmnyEncoder_EncodeNew_EmitsEditorIdAndValue()
    {
        var cmny = new CaravanMoneyRecord
        {
            FormId = 0x3100,
            EditorId = "CaravanMoney",
            Value = 250
        };

        var encoded = CmnyEncoder.EncodeNew(cmny);

        Assert.Equal(["EDID", "DATA"], encoded.Subrecords.Select(s => s.Signature));
        var data = Assert.Single(encoded.Subrecords, s => s.Signature == "DATA").Bytes;
        Assert.Equal(250u, BinaryPrimitives.ReadUInt32LittleEndian(data));
        Assert.Empty(encoded.Warnings);
    }

    // ====================================================================================
    // CdckEncoder (Phase 4.2a)
    // ====================================================================================

    [Fact]
    public void CdckEncoder_EncodeNew_EmitsEditorIdJokerCountAndCards()
    {
        var cdck = new CaravanDeckRecord
        {
            FormId = 0x7500,
            EditorId = "TestDeck",
            JokerCount = 2,
            Cards = [0x00012345, 0x00012346, 0x00012347]
        };

        var encoded = CdckEncoder.EncodeNew(cdck);

        // Canonical order: EDID, DATA, CARD*
        Assert.Equal(["EDID", "DATA", "CARD", "CARD", "CARD"],
            encoded.Subrecords.Select(s => s.Signature));

        var data = Assert.Single(encoded.Subrecords, s => s.Signature == "DATA").Bytes;
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(data));

        var cards = encoded.Subrecords.Where(s => s.Signature == "CARD")
            .Select(s => BinaryPrimitives.ReadUInt32LittleEndian(s.Bytes))
            .ToList();
        Assert.Equal(new[] { 0x00012345u, 0x00012346u, 0x00012347u }, cards);
        Assert.Empty(encoded.Warnings);
    }

    [Fact]
    public void CdckEncoder_EncodeNew_EmptyDeck_StillEmitsEditorIdAndData()
    {
        var cdck = new CaravanDeckRecord
        {
            FormId = 0x7501,
            EditorId = "EmptyDeck",
            JokerCount = 0,
            Cards = []
        };

        var encoded = CdckEncoder.EncodeNew(cdck);

        // No CARD subrecords; just EDID + DATA.
        Assert.Equal(["EDID", "DATA"], encoded.Subrecords.Select(s => s.Signature));
        var data = Assert.Single(encoded.Subrecords, s => s.Signature == "DATA").Bytes;
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(data));
        Assert.Empty(encoded.Warnings);
    }

    [Fact]
    public void CdckEncoder_EncodeNew_SkipsZeroCardFormIds()
    {
        var cdck = new CaravanDeckRecord
        {
            FormId = 0x7502,
            EditorId = "DeckWithZero",
            JokerCount = 1,
            Cards = [0x00012345, 0x00000000, 0x00012346]  // middle entry is invalid sentinel
        };

        var encoded = CdckEncoder.EncodeNew(cdck);

        // Only 2 CARD subrecords (the zero is skipped to avoid emitting a null reference).
        var cards = encoded.Subrecords.Where(s => s.Signature == "CARD")
            .Select(s => BinaryPrimitives.ReadUInt32LittleEndian(s.Bytes))
            .ToList();
        Assert.Equal(new[] { 0x00012345u, 0x00012346u }, cards);
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
            ["EDID", "FULL", "MODL", "SPLO", "ACBS", "SNAM", "PKID", "DATA"],
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

    [Fact]
    public void CreaEncoder_EncodeNew_EmitsObndAndPhysicalSubrecords()
    {
        var crea = new CreatureRecord
        {
            FormId = 0x2901,
            EditorId = "Robot",
            Bounds = new ObjectBounds { X1 = -23, Y1 = -17, Z1 = 0, X2 = 23, Y2 = 17, Z2 = 132 },
            SoundType = 0,
            TurningSpeed = 180f,
            BaseScale = 1.0f,
            FootWeight = 20.0f,
            ImpactMaterialType = 4,
            SoundLevel = 1,
            EquippedAttackAnimation = 0x00FF
        };

        var encoded = CreaEncoder.EncodeNew(crea);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();

        Assert.Contains("OBND", sigs);
        Assert.Contains("EAMT", sigs);
        Assert.Contains("RNAM", sigs);
        Assert.Contains("TNAM", sigs);
        Assert.Contains("BNAM", sigs);
        Assert.Contains("WNAM", sigs);
        Assert.Contains("NAM4", sigs);
        Assert.Contains("NAM5", sigs);

        var obnd = Assert.Single(encoded.Subrecords, s => s.Signature == "OBND").Bytes;
        Assert.Equal(12, obnd.Length);
        Assert.Equal(-23, BinaryPrimitives.ReadInt16LittleEndian(obnd.AsSpan(0, 2)));
        Assert.Equal(132, BinaryPrimitives.ReadInt16LittleEndian(obnd.AsSpan(10, 2)));
    }

    [Fact]
    public void CreaEncoder_EncodeNew_RemapsFormIdBearingSubrecords()
    {
        // Source FormID (proto-allocated) → allocated FormID (in plugin) for new records.
        var remap = new Dictionary<uint, uint>
        {
            [0x000ABCDE] = 0x01001234, // CombatStyle
            [0x000FEDCB] = 0x01005678  // Voice type
        };
        var valid = new HashSet<uint> { 0x01001234, 0x01005678, 0x000F1111, 0x000F2222 };

        var crea = new CreatureRecord
        {
            FormId = 0x2902,
            EditorId = "Robot",
            CombatStyleFormId = 0x000ABCDE,
            VoiceType = 0x000FEDCB,
            Template = 0x000F1111,    // already in valid set, stays
            BodyData = 0x000F2222,    // already in valid set, stays
            DeathItemLootList = 0x00DEAD00 // dangling, no remap — subrecord omitted
        };

        var encoded = CreaEncoder.EncodeNew(crea, valid, remap);

        var znam = Assert.Single(encoded.Subrecords, s => s.Signature == "ZNAM").Bytes;
        Assert.Equal(0x01001234u, BinaryPrimitives.ReadUInt32LittleEndian(znam));

        var vtck = Assert.Single(encoded.Subrecords, s => s.Signature == "VTCK").Bytes;
        Assert.Equal(0x01005678u, BinaryPrimitives.ReadUInt32LittleEndian(vtck));

        var tplt = Assert.Single(encoded.Subrecords, s => s.Signature == "TPLT").Bytes;
        Assert.Equal(0x000F1111u, BinaryPrimitives.ReadUInt32LittleEndian(tplt));

        var pnam = Assert.Single(encoded.Subrecords, s => s.Signature == "PNAM").Bytes;
        Assert.Equal(0x000F2222u, BinaryPrimitives.ReadUInt32LittleEndian(pnam));

        // Dangling LNAM dropped — subrecord must not be emitted.
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "LNAM");
    }

    [Fact]
    public void CreaEncoder_EncodeNew_EmitsInventoryWithRemap()
    {
        var remap = new Dictionary<uint, uint> { [0x000AAAA0] = 0x01001000 };
        var valid = new HashSet<uint> { 0x01001000, 0x000B0000 };

        var crea = new CreatureRecord
        {
            FormId = 0x2903,
            EditorId = "Robot",
            Inventory =
            [
                new InventoryItem(0x000AAAA0, 5), // proto FormID → gets remapped
                new InventoryItem(0x000B0000, 3), // already valid master → stays
                new InventoryItem(0xDEADBEEF, 1)  // dangling → dropped
            ]
        };

        var encoded = CreaEncoder.EncodeNew(crea, valid, remap);
        var cntos = encoded.Subrecords.Where(s => s.Signature == "CNTO").ToList();
        Assert.Equal(2, cntos.Count);
        Assert.Equal(0x01001000u, BinaryPrimitives.ReadUInt32LittleEndian(cntos[0].Bytes));
        Assert.Equal(0x000B0000u, BinaryPrimitives.ReadUInt32LittleEndian(cntos[1].Bytes));
        Assert.Contains(encoded.Warnings, w => w.Contains("dropped 1 CNTO"));
    }

    // ====================================================================================
    // Cross-encoder warning check (medium-complexity batch)
    // ====================================================================================

    [Fact]
    public void MediumComplexityEncoders_EmitWarningWhenEditorIdMissing()
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

    // ====================================================================================
    // Large new-record encoders: MGEF, WRLD, RACE
    // ====================================================================================

    // ====================================================================================
    // MgefEncoder
    // ====================================================================================

    [Fact]
    public void MgefEncoder_EncodeNew_DataIs72Bytes()
    {
        var mgef = new BaseEffectRecord { FormId = 0x3100, EditorId = "FireRes" };
        var encoded = MgefEncoder.EncodeNew(mgef);
        var data = Assert.Single(encoded.Subrecords, s => s.Signature == "DATA").Bytes;
        Assert.Equal(72, data.Length);
    }

    [Fact]
    public void MgefEncoder_EncodeNew_DataLayoutMatchesPdb()
    {
        var mgef = new BaseEffectRecord
        {
            FormId = 0x3100,
            EditorId = "FireDmg",
            Flags = 0x12345678,
            BaseCost = 2.5f,
            AssociatedItem = 0xABC,
            MagicSchool = 1,
            ResistValue = 5,
            LightFormId = 0x100,
            ProjectileSpeed = 8000.0f,
            EffectShaderFormId = 0x200,
            EnchantEffectFormId = 0x300,
            CastingSoundFormId = 0x400,
            BoltSoundFormId = 0x500,
            HitSoundFormId = 0x600,
            AreaSoundFormId = 0x700,
            CEEnchantFactor = 1.5f,
            CEBarterFactor = 2.0f,
            Archetype = 5, // Absorb
            ActorValue = 10
        };

        var encoded = MgefEncoder.EncodeNew(mgef);
        var data = Assert.Single(encoded.Subrecords, s => s.Signature == "DATA").Bytes;

        Assert.Equal(0x12345678u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0, 4)));
        Assert.Equal(2.5f, BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(4, 4)));
        Assert.Equal(0xABCu, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8, 4)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12, 4)));
        Assert.Equal(5, BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(16, 4)));
        Assert.Equal(0x100u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(24, 4)));
        Assert.Equal(8000.0f, BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(28, 4)));
        Assert.Equal(0x700u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(52, 4)));
        Assert.Equal(1.5f, BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(56, 4)));
        Assert.Equal(5, BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(64, 4))); // Archetype
        Assert.Equal(10, BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(68, 4)));
    }

    [Fact]
    public void MgefEncoder_EncodeNew_CanonicalOrder()
    {
        var mgef = new BaseEffectRecord
        {
            FormId = 0x3100,
            EditorId = "T",
            FullName = "Fire",
            Description = "Burns",
            Icon = "icons/fire.dds",
            ModelPath = "fire.nif"
        };
        var encoded = MgefEncoder.EncodeNew(mgef);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();
        Assert.Equal(["EDID", "FULL", "DESC", "ICON", "MODL", "DATA"], sigs);
    }

    // ====================================================================================
    // WrldEncoder
    // ====================================================================================

    [Fact]
    public void WrldEncoder_EncodeNew_CanonicalOrder()
    {
        var wrld = new WorldspaceRecord
        {
            FormId = 0x3200,
            EditorId = "Wasteland",
            FullName = "The Mojave",
            EncounterZoneFormId = 0xAA,
            ParentWorldspaceFormId = 0xBB,
            ParentUseFlags = 0x07,
            ClimateFormId = 0xCC,
            WaterFormId = 0xDD,
            MapUsableWidth = 10,
            MapUsableHeight = 10,
            MapNWCellX = -5,
            MapNWCellY = 5,
            MapSECellX = 5,
            MapSECellY = -5,
            Flags = 0x01,
            BoundsMinX = -1000f,
            BoundsMinY = -1000f,
            BoundsMaxX = 1000f,
            BoundsMaxY = 1000f,
            MapOffsetScaleX = 1.0f,
            MapOffsetScaleY = 1.0f,
            MapOffsetZ = 0f,
            ImageSpaceFormId = 0xEE,
            MusicTypeFormId = 0xFF,
            DefaultLandHeight = -2048f,
            DefaultWaterHeight = 0f
        };

        var encoded = WrldEncoder.EncodeNew(wrld);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();

        // FNVEdit canonical wbWRLD order (confirmed against master WastelandNV):
        // DNAM clusters with the water subrecords (right after NAM2); ONAM/INAM go between
        // MNAM and DATA; PNAM is paired with WNAM (this fixture supplies both).
        Assert.Equal(
            ["EDID", "FULL", "XEZN", "WNAM", "PNAM", "CNAM", "NAM2", "DNAM", "MNAM",
                "ONAM", "INAM", "DATA", "NAM0", "NAM9", "ZNAM"],
            sigs);
    }

    [Fact]
    public void WrldEncoder_EncodeNew_MnamLayoutMatchesPdb()
    {
        var wrld = new WorldspaceRecord
        {
            FormId = 0x3200,
            EditorId = "W",
            MapUsableWidth = 100,
            MapUsableHeight = 50,
            MapNWCellX = -10,
            MapNWCellY = 10,
            MapSECellX = 10,
            MapSECellY = -10
        };

        var encoded = WrldEncoder.EncodeNew(wrld);
        var mnam = Assert.Single(encoded.Subrecords, s => s.Signature == "MNAM").Bytes;

        Assert.Equal(16, mnam.Length);
        Assert.Equal(100, BinaryPrimitives.ReadInt32LittleEndian(mnam.AsSpan(0, 4)));
        Assert.Equal(50, BinaryPrimitives.ReadInt32LittleEndian(mnam.AsSpan(4, 4)));
        Assert.Equal((short)-10, BinaryPrimitives.ReadInt16LittleEndian(mnam.AsSpan(8, 2)));
        Assert.Equal((short)10, BinaryPrimitives.ReadInt16LittleEndian(mnam.AsSpan(10, 2)));
        Assert.Equal((short)10, BinaryPrimitives.ReadInt16LittleEndian(mnam.AsSpan(12, 2)));
        Assert.Equal((short)-10, BinaryPrimitives.ReadInt16LittleEndian(mnam.AsSpan(14, 2)));
    }

    [Fact]
    public void WrldEncoder_EncodeNew_DnamHeightsLayout()
    {
        var wrld = new WorldspaceRecord
        {
            FormId = 0x3200,
            EditorId = "W",
            DefaultLandHeight = -1024f,
            DefaultWaterHeight = 128f
        };

        var encoded = WrldEncoder.EncodeNew(wrld);
        var dnam = Assert.Single(encoded.Subrecords, s => s.Signature == "DNAM").Bytes;

        Assert.Equal(8, dnam.Length);
        Assert.Equal(-1024f, BinaryPrimitives.ReadSingleLittleEndian(dnam.AsSpan(0, 4)));
        Assert.Equal(128f, BinaryPrimitives.ReadSingleLittleEndian(dnam.AsSpan(4, 4)));
    }

    [Fact]
    public void WrldEncoder_EncodeNew_WarnsOnChildCells()
    {
        var wrld = new WorldspaceRecord
        {
            FormId = 0x3200,
            EditorId = "W",
            Cells = [new CellRecord { FormId = 0x100 }]
        };
        var encoded = WrldEncoder.EncodeNew(wrld);
        Assert.Contains(encoded.Warnings, w => w.Contains("child cell"));
    }

    [Fact]
    public void WrldEncoder_EncodeNew_OmitsAllOptionalsWhenAbsent()
    {
        var wrld = new WorldspaceRecord { FormId = 0x3200, EditorId = "Empty" };
        var encoded = WrldEncoder.EncodeNew(wrld);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();
        Assert.Equal(["EDID"], sigs);
    }

    // ====================================================================================
    // RaceEncoder
    // ====================================================================================

    [Fact]
    public void RaceEncoder_EncodeNew_DataIs36Bytes()
    {
        var race = new RaceRecord
        {
            FormId = 0x3300,
            EditorId = "Caucasian",
            MaleHeight = 1.0f,
            FemaleHeight = 0.95f,
            MaleWeight = 1.0f,
            FemaleWeight = 1.0f,
            DataFlags = 0x01,
            SkillBoosts =
            [
                (3, 5),
                (7, 2),
                (-1, 0)
            ]
        };

        var encoded = RaceEncoder.EncodeNew(race);
        var data = Assert.Single(encoded.Subrecords, s => s.Signature == "DATA").Bytes;

        Assert.Equal(36, data.Length);
        Assert.Equal(3, (sbyte)data[0]);   // First skill boost index
        Assert.Equal(5, (sbyte)data[1]);   // First skill boost value
        Assert.Equal(7, (sbyte)data[2]);
        Assert.Equal(2, (sbyte)data[3]);
        Assert.Equal(-1, (sbyte)data[4]);
        Assert.Equal(0, (sbyte)data[5]);
        // Remaining skill slots (indices 3-6) should be -1 sentinel padding.
        Assert.Equal(-1, (sbyte)data[6]);
        Assert.Equal(1.0f, BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(16, 4)));
        Assert.Equal(0.95f, BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(20, 4)));
        Assert.Equal(0x01u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(32, 4)));
    }

    [Fact]
    public void RaceEncoder_EncodeNew_VtckPairsMaleAndFemale()
    {
        var race = new RaceRecord
        {
            FormId = 0x3300,
            EditorId = "T",
            MaleVoiceFormId = 0x111,
            FemaleVoiceFormId = 0x222
        };

        var encoded = RaceEncoder.EncodeNew(race);
        var vtck = Assert.Single(encoded.Subrecords, s => s.Signature == "VTCK").Bytes;
        Assert.Equal(8, vtck.Length);
        Assert.Equal(0x111u, BinaryPrimitives.ReadUInt32LittleEndian(vtck.AsSpan(0, 4)));
        Assert.Equal(0x222u, BinaryPrimitives.ReadUInt32LittleEndian(vtck.AsSpan(4, 4)));
    }

    [Fact]
    public void RaceEncoder_EncodeNew_BodyPartsEmitNam0Nam1WithIndxMembers()
    {
        var race = new RaceRecord
        {
            FormId = 0x3300,
            EditorId = "T",
            MaleHeadModelPath = "characters/head_m.nif",
            MaleHeadTexturePath = "characters/head_m.dds",
            MaleMouthModelPath = "characters/mouth_m.nif",
            MaleUpperBodyPath = "characters/body_m.nif",
            MaleBodyTexturePath = "characters/body_m.dds",
            MaleLeftHandPath = "characters/lhand.nif",
            MaleRightHandPath = "characters/rhand.nif"
        };

        var encoded = RaceEncoder.EncodeNew(race);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();

        var nam0Idx = sigs.IndexOf("NAM0");
        var nam1Idx = sigs.IndexOf("NAM1");
        Assert.True(nam0Idx >= 0 && nam1Idx > nam0Idx);

        // Each body part block carries INDX/MODL/ICON groups; total INDX count = head parts (2: head+mouth)
        // + body parts (3: upper body + left hand + right hand) = 5.
        Assert.Equal(5, sigs.Count(s => s == "INDX"));
    }

    [Fact]
    public void RaceEncoder_EncodeNew_FaceGenMorphsEmitMnamAndFnamSections()
    {
        var race = new RaceRecord
        {
            FormId = 0x3300,
            EditorId = "T",
            MaleFaceGenGeometrySymmetric = new float[50],
            MaleFaceGenGeometryAsymmetric = new float[30],
            MaleFaceGenTextureSymmetric = new float[50],
            FemaleFaceGenGeometrySymmetric = new float[50],
            FemaleFaceGenGeometryAsymmetric = new float[30],
            FemaleFaceGenTextureSymmetric = new float[50]
        };

        var encoded = RaceEncoder.EncodeNew(race);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();

        // Layout: ... MNAM, FGGS(200B), FGGA(120B), FGTS(200B), FNAM, FGGS, FGGA, FGTS.
        var mnamIdx = sigs.IndexOf("MNAM");
        var fnamIdx = sigs.IndexOf("FNAM");
        Assert.True(mnamIdx >= 0 && fnamIdx > mnamIdx);

        // Three FGGS/FGGA/FGTS pairs each follow their gender marker.
        Assert.Equal(2, sigs.Count(s => s == "FGGS"));
        Assert.Equal(2, sigs.Count(s => s == "FGGA"));
        Assert.Equal(2, sigs.Count(s => s == "FGTS"));

        // FGGS = 200 bytes (50 floats), FGGA = 120 bytes (30 floats).
        var fggs = encoded.Subrecords.First(s => s.Signature == "FGGS");
        var fgga = encoded.Subrecords.First(s => s.Signature == "FGGA");
        Assert.Equal(200, fggs.Bytes.Length);
        Assert.Equal(120, fgga.Bytes.Length);
    }

    [Fact]
    public void RaceEncoder_EncodeNew_HairAndEyeFormIdLists()
    {
        var race = new RaceRecord
        {
            FormId = 0x3300,
            EditorId = "T",
            HairStyleFormIds = [0x111, 0x222, 0x333],
            EyeColorFormIds = [0x444, 0x555]
        };

        var encoded = RaceEncoder.EncodeNew(race);

        Assert.Equal(3, encoded.Subrecords.Count(s => s.Signature == "HNAM"));
        Assert.Equal(2, encoded.Subrecords.Count(s => s.Signature == "ENAM"));
    }

    [Fact]
    public void RaceEncoder_EncodeNew_AllSkillBoostSlotsFilledWithNegOneSentinel()
    {
        var race = new RaceRecord { FormId = 0x3300, EditorId = "Empty", SkillBoosts = [] };
        var encoded = RaceEncoder.EncodeNew(race);
        var data = Assert.Single(encoded.Subrecords, s => s.Signature == "DATA").Bytes;

        // All 7 skill slots default to -1 sentinel (boost 0).
        for (var i = 0; i < 7; i++)
        {
            Assert.Equal(-1, (sbyte)data[i * 2]);
            Assert.Equal(0, (sbyte)data[i * 2 + 1]);
        }
    }

    // ====================================================================================
    // Cross-encoder warning check (large new-record encoders)
    // ====================================================================================

    [Fact]
    public void LargeNewRecordEncoders_EmitWarningWhenEditorIdMissing()
    {
        Assert.Contains(MgefEncoder.EncodeNew(new BaseEffectRecord { FormId = 1 }).Warnings,
            w => w.Contains("has no EditorId"));
        Assert.Contains(WrldEncoder.EncodeNew(new WorldspaceRecord { FormId = 1 }).Warnings,
            w => w.Contains("has no EditorId"));
        Assert.Contains(RaceEncoder.EncodeNew(new RaceRecord { FormId = 1 }).Warnings,
            w => w.Contains("has no EditorId"));
    }
}
