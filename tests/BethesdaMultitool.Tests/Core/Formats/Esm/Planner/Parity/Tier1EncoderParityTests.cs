using BethesdaMultitool.Core.Formats.Esm.Enums;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Item;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Item;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner.Parity;

/// <summary>
///     Tier 1 byte-exact parity for the six trivial encoders (STAT lives in its own file).
///     Each case builds a synthetic record, runs it through PlanWriter, runs the same
///     record through the legacy primitives directly, and asserts byte equality.
/// </summary>
public sealed class Tier1EncoderParityTests
{
    public static TheoryData<PlannerParityCase> Cases => new()
    {
        new PlannerParityCase("GLOB", "GLOB", () =>
        {
            var glob = new GlobalRecord
            {
                FormId = 0x01000800,
                EditorId = "TestGlob",
                ValueType = 'f',
                Value = 42.5f
            };
            return (glob.FormId, glob, GlobEncoder.EncodeNew(glob));
        }),
        new PlannerParityCase("GMST", "GMST (float)", () =>
        {
            var gmst = new GameSettingRecord
            {
                FormId = 0x01000800,
                EditorId = "fTestSetting",
                ValueType = GameSettingType.Float,
                FloatValue = 3.14159f
            };
            return (gmst.FormId, gmst, GmstEncoder.EncodeNew(gmst));
        }),
        new PlannerParityCase("GMST", "GMST (integer)", () =>
        {
            var gmst = new GameSettingRecord
            {
                FormId = 0x01000801,
                EditorId = "iTestInt",
                ValueType = GameSettingType.Integer,
                IntValue = 42
            };
            return (gmst.FormId, gmst, GmstEncoder.EncodeNew(gmst));
        }),
        new PlannerParityCase("ARMO", "ARMO", () =>
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
                EquipmentType = EquipmentType.BodyWear
            };
            return (armo.FormId, armo, ArmoEncoder.EncodeNew(armo));
        }),
        new PlannerParityCase("AMMO", "AMMO", () =>
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
                ClipRounds = 30
            };
            return (ammo.FormId, ammo, AmmoEncoder.EncodeNew(ammo));
        }),
        new PlannerParityCase("BOOK", "BOOK", () =>
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
                Weight = 1.0f
            };
            return (book.FormId, book, BookEncoder.EncodeNew(book));
        }),
        new PlannerParityCase("ALCH", "ALCH", () =>
        {
            var alch = new ConsumableRecord
            {
                FormId = 0x01000800,
                EditorId = "TestAlch",
                FullName = "Test Consumable",
                ModelPath = "alch/test/test.nif",
                Weight = 0.5f
            };
            return (alch.FormId, alch, AlchEncoder.EncodeNew(alch));
        })
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void PlannedNewRecord_MatchesLegacyGrupBytes(PlannerParityCase parityCase)
    {
        var (formId, model, legacy) = parityCase.Build();

        PlannerTier1ParityHelper.AssertNewRecordParity(parityCase.Signature, formId, model, legacy);
    }
}
