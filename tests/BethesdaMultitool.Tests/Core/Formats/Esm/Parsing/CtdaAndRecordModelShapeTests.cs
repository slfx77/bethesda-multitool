using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Item;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

/// <summary>
///     Parser + record-model shape coverage:
///     - CtdaParser shared classic/modern condition decoder (BE + LE)
///     - RecipeCategoryRecord model shape (RCCT)
///     - ConstructibleObjectRecord model shape (COBJ)
///     - ArmaRecord texture hashes / icons / detection-sound-level fields
///     - QuestRecord Conditions list with optional string parameters
/// </summary>
public class CtdaAndRecordModelShapeTests
{
    // ====================================================================================
    // CtdaParser.Decode — shared classic/modern CTDA decoder
    // ====================================================================================

    [Fact]
    public void CtdaParser_Decode_LittleEndianLayout()
    {
        var bytes = new byte[28];
        bytes[0] = 0x35; // Type
        // bytes[1..3] padding
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(4, 4), 3.5f);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8, 2), 250); // FunctionIndex
        // bytes[10..11] padding
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), 0xABCDEFu); // Parameter1
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), 0x12345678u); // Parameter2
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20, 4), 4u); // RunOn = Linked Reference
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24, 4), 0xDEADBEEFu); // Reference

        var condition = CtdaParser.Decode(bytes, false);

        Assert.Equal(0x35, condition.Type);
        Assert.Equal(3.5f, condition.ComparisonValue);
        Assert.Equal(250, condition.FunctionIndex);
        Assert.Equal(0xABCDEFu, condition.Parameter1);
        Assert.Equal(0x12345678u, condition.Parameter2);
        Assert.Equal(4u, condition.RunOn);
        Assert.Equal(0xDEADBEEFu, condition.Reference);
        Assert.Null(condition.Parameter3);
    }

    [Fact]
    public void CtdaParser_Decode_BigEndianLayout()
    {
        var bytes = new byte[28];
        bytes[0] = 0x20;
        BinaryPrimitives.WriteSingleBigEndian(bytes.AsSpan(4, 4), 1.0f);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(8, 2), 76); // GetIsID
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12, 4), 0x1234u);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), 0u);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20, 4), 0u);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(24, 4), 0u);

        var condition = CtdaParser.Decode(bytes, true);

        Assert.Equal(0x20, condition.Type);
        Assert.Equal(1.0f, condition.ComparisonValue);
        Assert.Equal(76, condition.FunctionIndex);
        Assert.Equal(0x1234u, condition.Parameter1);
        Assert.Null(condition.Parameter3);
    }

    [Theory]
    [InlineData(false, -123456789)]
    [InlineData(true, int.MinValue)]
    public void CtdaParser_Decode_ModernLayoutPreservesSignedParameter3(bool bigEndian, int parameter3)
    {
        var bytes = new byte[32];
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(8, 2), 76);
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(28, 4), parameter3);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8, 2), 76);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28, 4), parameter3);
        }

        var condition = CtdaParser.Decode(bytes, bigEndian);

        Assert.Equal(parameter3, condition.Parameter3);
    }

    [Fact]
    public void CtdaParser_Decode_NoStringParametersByDefault()
    {
        var bytes = new byte[28];
        var condition = CtdaParser.Decode(bytes, false);
        Assert.Null(condition.Parameter1String);
        Assert.Null(condition.Parameter2String);
        Assert.Null(condition.Parameter3);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(28)]
    public void CtdaParser_Decode_ExactLegacyWidthsDoNotInventParameter3(int length)
    {
        var bytes = new byte[length];

        var condition = CtdaParser.Decode(bytes, false);

        Assert.Null(condition.Parameter3);
    }

    [Theory]
    [InlineData(19)]
    [InlineData(21)]
    [InlineData(23)]
    [InlineData(25)]
    [InlineData(27)]
    [InlineData(29)]
    [InlineData(30)]
    [InlineData(31)]
    [InlineData(33)]
    public void CtdaParser_Decode_RejectsUnsupportedWidths(int length)
    {
        Assert.Throws<ArgumentException>(() => CtdaParser.Decode(new byte[length], false));
    }

    // ====================================================================================
    // RecipeCategoryRecord (RCCT) — model shape
    // ====================================================================================

    [Fact]
    public void RecipeCategoryRecord_CarriesFlagsField()
    {
        var record = new RecipeCategoryRecord
        {
            FormId = 0x100,
            EditorId = "WeaponMods",
            FullName = "Weapon Mods",
            Flags = 0x01
        };

        Assert.Equal(0x100u, record.FormId);
        Assert.Equal("WeaponMods", record.EditorId);
        Assert.Equal("Weapon Mods", record.FullName);
        Assert.Equal(0x01, record.Flags);
    }

    // ====================================================================================
    // ConstructibleObjectRecord (COBJ) — model shape + ingredient/condition lists
    // ====================================================================================

    [Fact]
    public void ConstructibleObjectRecord_CarriesIngredientsAndConditions()
    {
        var cobj = new ConstructibleObjectRecord
        {
            FormId = 0x200,
            EditorId = "MakeCookedRadroachMeat",
            FullName = "Cooked Radroach Meat",
            ModelPath = "radroach.nif",
            TextureHashData = [0x01, 0x02, 0x03, 0x04],
            Ingredients =
            [
                new InventoryItem(0xABCDu, 1),
                new InventoryItem(0xBCDEu, 2)
            ],
            Conditions =
            [
                new DialogueCondition { Type = 0x20, FunctionIndex = 449, Parameter1String = "Skill" }
            ],
            CreatedItemFormId = 0xCAFE,
            WorkbenchKeywordFormId = 0xBEEF
        };

        Assert.Equal(2, cobj.Ingredients.Count);
        Assert.Equal(0xABCDu, cobj.Ingredients[0].ItemFormId);
        Assert.Single(cobj.Conditions);
        Assert.Equal("Skill", cobj.Conditions[0].Parameter1String);
        Assert.Equal(0xCAFEu, cobj.CreatedItemFormId);
        Assert.Equal(0xBEEFu, cobj.WorkbenchKeywordFormId);
        Assert.Equal(4, cobj.TextureHashData?.Length);
    }

    // ====================================================================================
    // ArmaRecord — new fields surface
    // ====================================================================================

    [Fact]
    public void ArmaRecord_NewFieldsAreInitOnlyAndDefault()
    {
        var arma = new ArmaRecord { FormId = 0x300 };
        Assert.Null(arma.MaleTextureHashData);
        Assert.Null(arma.FemaleTextureHashData);
        Assert.Null(arma.MaleFirstPersonTextureHashData);
        Assert.Null(arma.FemaleFirstPersonTextureHashData);
        Assert.Null(arma.MaleIconPath);
        Assert.Null(arma.FemaleIconPath);
        Assert.Equal(0, arma.DamageResistance);
        Assert.Equal(0f, arma.DamageThreshold);
    }

    [Fact]
    public void ArmaRecord_CarriesAllNewFields()
    {
        var arma = new ArmaRecord
        {
            FormId = 0x300,
            EditorId = "LeatherArmor",
            MaleModelPath = "armor_m.nif",
            FemaleModelPath = "armor_f.nif",
            MaleFirstPersonModelPath = "armor_m_fp.nif",
            FemaleFirstPersonModelPath = "armor_f_fp.nif",
            MaleTextureHashData = [0x01],
            FemaleTextureHashData = [0x02],
            MaleFirstPersonTextureHashData = [0x03],
            FemaleFirstPersonTextureHashData = [0x04],
            MaleIconPath = "icons/armor_m.dds",
            FemaleIconPath = "icons/armor_f.dds",
            DamageResistance = 15,
            DamageThreshold = 8.5f
        };

        Assert.Equal((byte)0x01, arma.MaleTextureHashData?[0]);
        Assert.Equal((byte)0x02, arma.FemaleTextureHashData?[0]);
        Assert.Equal((byte)0x03, arma.MaleFirstPersonTextureHashData?[0]);
        Assert.Equal((byte)0x04, arma.FemaleFirstPersonTextureHashData?[0]);
        Assert.Equal("icons/armor_m.dds", arma.MaleIconPath);
        Assert.Equal("icons/armor_f.dds", arma.FemaleIconPath);
        Assert.Equal(15, arma.DamageResistance);
        Assert.Equal(8.5f, arma.DamageThreshold);
    }

    // ====================================================================================
    // QuestRecord — Conditions list
    // ====================================================================================

    [Fact]
    public void QuestRecord_ConditionsDefaultsToEmpty()
    {
        var quest = new QuestRecord { FormId = 0x400 };
        Assert.NotNull(quest.Conditions);
        Assert.Empty(quest.Conditions);
    }

    [Fact]
    public void QuestRecord_CarriesConditionsWithStringParameters()
    {
        var quest = new QuestRecord
        {
            FormId = 0x400,
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

        Assert.Single(quest.Conditions);
        Assert.Equal("QuestVar", quest.Conditions[0].Parameter1String);
        Assert.Equal("Stage", quest.Conditions[0].Parameter2String);
    }
}