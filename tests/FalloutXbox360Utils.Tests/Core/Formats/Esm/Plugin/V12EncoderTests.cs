using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     v12 tests covering the actionable-bundle work:
///     - ARMA (Armor Addon) new-record encoder
///     - RCPE (Recipe) new-record encoder
///     - WEAP VATS subrecord emission (closes v6-deferred warning)
/// </summary>
public class V12EncoderTests
{
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

        Assert.Equal(8, bmdt.Length);
        Assert.Equal(0x12345678u, BinaryPrimitives.ReadUInt32LittleEndian(bmdt.AsSpan(0, 4)));
        Assert.Equal(0x42, bmdt[4]);
        Assert.Equal(0, bmdt[5]);
        Assert.Equal(0, bmdt[6]);
        Assert.Equal(0, bmdt[7]);
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
}
