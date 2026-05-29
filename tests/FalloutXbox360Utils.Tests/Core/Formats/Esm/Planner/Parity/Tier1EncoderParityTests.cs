using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Planner.Parity;

/// <summary>
///     Tier 1 byte-exact parity for the six trivial encoders (STAT lives in its own file).
///     Each test builds a synthetic record, runs it through PlanWriter, runs the same
///     record through the legacy primitives directly, and asserts byte equality.
/// </summary>
public sealed class Tier1EncoderParityTests
{
    [Fact]
    public void New_Glob_GRUP_Bytes_Match_Legacy()
    {
        var glob = new GlobalRecord
        {
            FormId = 0x01000800,
            EditorId = "TestGlob",
            ValueType = 'f',
            Value = 42.5f,
        };

        var legacy = GlobEncoder.EncodeNew(glob);

        PlannerTier1ParityHelper.AssertNewRecordParity("GLOB", glob.FormId, glob, legacy);
    }

    [Fact]
    public void New_Gmst_Float_GRUP_Bytes_Match_Legacy()
    {
        var gmst = new GameSettingRecord
        {
            FormId = 0x01000800,
            EditorId = "fTestSetting",
            ValueType = GameSettingType.Float,
            FloatValue = 3.14159f,
        };

        var legacy = GmstEncoder.EncodeNew(gmst);

        PlannerTier1ParityHelper.AssertNewRecordParity("GMST", gmst.FormId, gmst, legacy);
    }

    [Fact]
    public void New_Gmst_Integer_GRUP_Bytes_Match_Legacy()
    {
        var gmst = new GameSettingRecord
        {
            FormId = 0x01000801,
            EditorId = "iTestInt",
            ValueType = GameSettingType.Integer,
            IntValue = 42,
        };

        var legacy = GmstEncoder.EncodeNew(gmst);

        PlannerTier1ParityHelper.AssertNewRecordParity("GMST", gmst.FormId, gmst, legacy);
    }

    [Fact]
    public void New_Armo_GRUP_Bytes_Match_Legacy()
    {
        var armo = new ArmorRecord
        {
            FormId = 0x01000800,
            EditorId = "TestArmor",
            FullName = "Test Armor",
            ModelPath = "armor/test/test.nif",
            Value = 100,
            Health = 200,
            Weight = 5.0f,
            DamageResistance = 10,
            DamageThreshold = 5.0f,
            BipedFlags = 0x4,
            EquipmentType = EquipmentType.BodyWear,
        };

        var legacy = ArmoEncoder.EncodeNew(armo);

        PlannerTier1ParityHelper.AssertNewRecordParity("ARMO", armo.FormId, armo, legacy);
    }

    [Fact]
    public void New_Ammo_GRUP_Bytes_Match_Legacy()
    {
        var ammo = new AmmoRecord
        {
            FormId = 0x01000800,
            EditorId = "TestAmmo",
            FullName = "Test Ammo",
            ModelPath = "ammo/test/test.nif",
            Speed = 1000.0f,
            Flags = 0,
            Value = 5,
            ClipRounds = 30,
        };

        var legacy = AmmoEncoder.EncodeNew(ammo);

        PlannerTier1ParityHelper.AssertNewRecordParity("AMMO", ammo.FormId, ammo, legacy);
    }

    [Fact]
    public void New_Book_GRUP_Bytes_Match_Legacy()
    {
        var book = new BookRecord
        {
            FormId = 0x01000800,
            EditorId = "TestBook",
            FullName = "Test Book",
            ModelPath = "books/test/test.nif",
            Text = "Test contents.",
            Flags = 0,
            SkillTaught = 3,
            Value = 25,
            Weight = 1.0f,
        };

        var legacy = BookEncoder.EncodeNew(book);

        PlannerTier1ParityHelper.AssertNewRecordParity("BOOK", book.FormId, book, legacy);
    }

    [Fact]
    public void New_Alch_GRUP_Bytes_Match_Legacy()
    {
        var alch = new ConsumableRecord
        {
            FormId = 0x01000800,
            EditorId = "TestAlch",
            FullName = "Test Consumable",
            ModelPath = "alch/test/test.nif",
            Weight = 0.5f,
        };

        var legacy = AlchEncoder.EncodeNew(alch);

        PlannerTier1ParityHelper.AssertNewRecordParity("ALCH", alch.FormId, alch, legacy);
    }
}
