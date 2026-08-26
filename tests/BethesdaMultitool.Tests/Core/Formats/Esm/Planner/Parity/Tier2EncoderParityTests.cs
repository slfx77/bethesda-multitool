using BethesdaMultitool.Core.Formats.Esm.Models.Records.Item;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Item;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.World;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner.Parity;

/// <summary>
///     Tier 2 byte-exact parity. Each case feeds a synthetic record (with no outgoing FormID
///     refs so legacy and planner produce identical bytes regardless of validFormIds /
///     remapTable contents) through PlanWriter, replays the legacy primitives directly,
///     and asserts byte equality.
/// </summary>
public sealed class Tier2EncoderParityTests
{
    public static TheoryData<PlannerParityCase> Cases => new()
    {
        // No outgoing FormID refs: ammo/projectile/etc. all null, no critical effect.
        new PlannerParityCase("WEAP", "WEAP (no refs)", () =>
        {
            var weap = new WeaponRecord
            {
                FormId = 0x01000800,
                EditorId = "TestWeapon",
                FullName = "Test Weapon",
                ModelPath = "weapons/test/test.nif",
                Value = 100,
                Health = 200,
                Weight = 3.0f,
                Damage = 25,
                ClipSize = 12
            };
            return (weap.FormId, weap, WeapEncoder.EncodeNew(weap));
        }),
        new PlannerParityCase("DOOR", "DOOR", () =>
        {
            var door = new DoorRecord
            {
                FormId = 0x01000800,
                EditorId = "TestDoor",
                FullName = "Test Door",
                ModelPath = "doors/test/test.nif",
                Flags = 0x02
            };
            return (door.FormId, door, DoorEncoder.EncodeNew(door));
        }),
        new PlannerParityCase("MISC", "MISC", () =>
        {
            var misc = new MiscItemRecord
            {
                FormId = 0x01000800,
                EditorId = "TestMisc",
                FullName = "Test Misc",
                ModelPath = "misc/test/test.nif",
                Value = 5,
                Weight = 0.1f
            };
            return (misc.FormId, misc, MiscEncoder.EncodeNew(misc));
        }),
        new PlannerParityCase("KEYM", "KEYM", () =>
        {
            var key = new KeyRecord
            {
                FormId = 0x01000800,
                EditorId = "TestKey",
                FullName = "Test Key",
                ModelPath = "keys/test/test.nif",
                Value = 0,
                Weight = 0.0f
            };
            return (key.FormId, key, KeymEncoder.EncodeNew(key));
        }),
        new PlannerParityCase("NOTE", "NOTE", () =>
        {
            var note = new NoteRecord
            {
                FormId = 0x01000800,
                EditorId = "TestNote",
                FullName = "Test Note",
                ModelPath = "notes/test/test.nif",
                NoteType = 0,
                Text = "Test contents."
            };
            return (note.FormId, note, NoteEncoder.EncodeNew(note));
        }),
        new PlannerParityCase("IMOD", "IMOD", () =>
        {
            var imod = new WeaponModRecord
            {
                FormId = 0x01000800,
                EditorId = "TestImod",
                FullName = "Test Mod",
                ModelPath = "mods/test/test.nif",
                Value = 50,
                Weight = 1.0f
            };
            return (imod.FormId, imod, ImodEncoder.EncodeNew(imod));
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
