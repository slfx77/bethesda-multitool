using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Dialogue;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     v14 tests covering the encoder side of v13's parser work:
///     - RcctEncoder (Recipe Category)
///     - CobjEncoder (Constructible Object) with ingredient list + conditions
///     - ArmaEncoder extensions (MODT/MO2T/MO3T/MO4T texture hashes, ICON/MIC2 icons, DNAM)
///     - QustEncoder top-level CTDA + CIS1/CIS2 emission
/// </summary>
public class V14EncoderTests
{
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
}
