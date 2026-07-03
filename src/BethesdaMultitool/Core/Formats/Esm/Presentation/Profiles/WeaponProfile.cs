using BethesdaMultitool.Core.Formats.Esm.Enums;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Item;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Decoding;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Utils;
using static BethesdaMultitool.Core.Formats.Esm.Presentation.Profiles.DecodedTreeReader;

namespace BethesdaMultitool.Core.Formats.Esm.Presentation.Profiles;

/// <summary>
///     The WEAP presentation profile — reproduces <see cref="RecordDetailBuilders.BuildWeapon" />'s five
///     sections (Identity / Combat / Requirements / References / Presentation) from a schema-decoded tree.
///     For FNV this is byte-for-byte equivalent to the typed builder (proven by <c>WeaponProfileParityTests</c>);
///     for the other games it produces the same sectioned shape from their (differing) tree, with FNV-specific
///     values best-effort.
///     <para>
///         WEAP exercises several schema quirks. The generated-schema DNAM labels differ from the typed
///         handler's (Speed = "Animation Multiplier" @4, Shots/Sec = "Fire Rate" @64, WeaponType = low byte of
///         "Animation Type" @0) — same bytes, different names. A <c>RawMemberDef</c> mid-DNAM (offset 136)
///         halts struct decode, so Strength/Skill Req live in the trailing raw child (+32 / +64). SNAM×2 and
///         WMS1×2 group under "Sound - Gun" / "Sound - Mod 1" (the handler is last-wins on WMS1). ICON/MICO are
///         unmapped → top-level raw nodes; MODL/MOD2/NNAM decode as string children. Computed names/DPS are
///         taken from a throwaway <see cref="WeaponRecord" /> so the maps match exactly.
///     </para>
/// </summary>
internal sealed class WeaponProfile : IRecordProfile
{
    private static readonly WeaponModCombination[] ModCombinations =
    [
        WeaponModCombination.Mod1, WeaponModCombination.Mod2, WeaponModCombination.Mod3,
        WeaponModCombination.Mod12, WeaponModCombination.Mod13, WeaponModCombination.Mod23,
        WeaponModCombination.Mod123
    ];

    public string RecordType => "WEAP";

    public RecordDetailModel Build(
        uint formId, string? editorId, string? displayName,
        IReadOnlyList<DecodedNode> tree, BethesdaGame game, FormIdResolver resolver, RecordCollection? records)
    {
        var data = TopBySignature(tree, "DATA");
        var dnam = TopBySignature(tree, "DNAM");
        var crdt = TopBySignature(tree, "CRDT");
        var gun = TopByLabel(tree, "Sound - Gun");
        var modSound = TopByLabel(tree, "Sound - Mod 1");

        // DATA (15 bytes): Value / Health / Weight / Base Damage / Clip Size.
        var value = (int)(Int(ChildByLabel(data, "Value")) ?? 0);
        var health = (int)(Int(ChildByLabel(data, "Health")) ?? 0);
        var weight = Float(ChildByLabel(data, "Weight")) ?? 0f;
        var damage = (short)(Int(ChildByLabel(data, "Base Damage")) ?? 0);
        var clipSize = (byte)(Int(ChildByLabel(data, "Clip Size")) ?? 0);

        // DNAM: the typed handler reads these only when DNAM >= 64 bytes; otherwise speed/shots default to 1.0
        // and the rest to 0 — mirror that by reading from the struct only when the DNAM node is present.
        var weaponType = (WeaponType)0;
        var speed = 1.0f;
        var shotsPerSec = 1.0f;
        var skill = 0u;
        var minRange = 0f;
        var maxRange = 0f;
        uint? projectile = null;
        if (dnam is not null)
        {
            var wtByte = (byte)((Int(ChildByLabel(dnam, "Animation Type")) ?? 0) & 0xFF);
            weaponType = Enum.IsDefined(typeof(WeaponType), wtByte)
                ? (WeaponType)wtByte
                : WeaponType.HandToHandMelee;
            speed = Float(ChildByLabel(dnam, "Animation Multiplier")) ?? 1.0f;
            shotsPerSec = Float(ChildByLabel(dnam, "Fire Rate")) ?? 1.0f;
            skill = (uint)(Int(ChildByLabel(dnam, "Skill")) ?? 0);
            minRange = Float(ChildByLabel(dnam, "Min Range")) ?? 0f;
            maxRange = Float(ChildByLabel(dnam, "Max Range")) ?? 0f;
            projectile = FormIdOf(ChildByLabel(dnam, "Projectile")); // typed: set only when non-zero
        }

        // Strength/Skill Req sit past the mid-DNAM RawMemberDef, so they live in the trailing raw child.
        var dnamTail = Bytes(dnam?.Children.LastOrDefault(c => c.IsRaw));
        var strengthReq = ReadU32(dnamTail, 32) ?? 0;
        var skillReq = ReadU32(dnamTail, 64) ?? 0;

        // CRDT: typed reads these only when present (>= 16 bytes); CriticalChance defaults to 1.0 otherwise,
        // and CriticalEffect is kept even when zero (a present-but-zero Link).
        var criticalDamage = (short)(Int(ChildByLabel(crdt, "Critical Damage")) ?? 0);
        var criticalChance = crdt is not null ? Float(ChildByLabel(crdt, "Crit % Mult")) ?? 1.0f : 1.0f;
        var criticalEffect = crdt is not null ? KeepZero(ChildByLabel(crdt, "Effect")) : null;

        // ETYP / WeaponType / DPS reuse the typed record's own maps so formatting matches exactly.
        var equipmentType = EquipmentTypeFromEtyp(tree);
        var names = new WeaponRecord
        {
            WeaponType = weaponType, EquipmentType = equipmentType, Damage = damage, ShotsPerSec = shotsPerSec
        };

        var sections = new List<RecordDetailSection>
        {
            RecordDetailHelpers.Section("Identity",
            [
                RecordDetailHelpers.Scalar("Form ID", $"0x{formId:X8}"),
                RecordDetailHelpers.Scalar("Editor ID", editorId ?? "(none)"),
                RecordDetailHelpers.Scalar("Name", displayName ?? "(none)"),
                RecordDetailHelpers.Scalar("Type", names.WeaponTypeName),
                RecordDetailHelpers.Scalar("Equipment", names.EquipmentTypeName),
                RecordDetailHelpers.Scalar("Skill", resolver.GetActorValueName((int)skill) ?? $"AV#{skill}")
            ]),
            RecordDetailHelpers.Section("Combat",
            [
                RecordDetailHelpers.Scalar("Damage", damage.ToString()),
                RecordDetailHelpers.Scalar("Critical Chance", criticalChance.ToString("P0")),
                RecordDetailHelpers.Scalar("Critical Damage", criticalDamage.ToString()),
                RecordDetailHelpers.Scalar("Attack Speed", speed.ToString("F2")),
                RecordDetailHelpers.Scalar("Shots / Sec", shotsPerSec.ToString("F2")),
                RecordDetailHelpers.Scalar("Clip Size", clipSize.ToString()),
                RecordDetailHelpers.Scalar("DPS", names.DamagePerSecond.ToString("F1")),
                RecordDetailHelpers.Scalar("Min Range", minRange.ToString("F1")),
                RecordDetailHelpers.Scalar("Max Range", maxRange.ToString("F1"))
            ]),
            RecordDetailHelpers.Section("Requirements",
            [
                RecordDetailHelpers.Scalar("Strength Requirement", strengthReq.ToString()),
                RecordDetailHelpers.Scalar("Skill Requirement", skillReq.ToString()),
                RecordDetailHelpers.Scalar("Weight", weight.ToString("F1")),
                RecordDetailHelpers.Scalar("Value", value.ToString()),
                RecordDetailHelpers.Scalar("Health", health.ToString())
            ]),
            RecordDetailHelpers.Section("References",
            [
                RecordDetailHelpers.Link("Ammo", KeepZero(TopBySignature(tree, "NAM0")), resolver),
                RecordDetailHelpers.Link("Projectile", projectile, resolver),
                RecordDetailHelpers.Link("Critical Effect", criticalEffect, resolver),
                RecordDetailHelpers.Link("Impact Data Set", KeepZero(TopBySignature(tree, "INAM")), resolver)
            ]),
            RecordDetailHelpers.Section("Presentation",
            [
                RecordDetailHelpers.Scalar("Model", Str(ChildByLabel(TopByLabel(tree, "Model"), "Model FileName"))),
                RecordDetailHelpers.Scalar("Shell Casing",
                    Str(ChildByLabel(TopByLabel(tree, "Shell Casing Model"), "Model Filename"))),
                RecordDetailHelpers.Scalar("Inventory Icon", RawString(TopBySignature(tree, "ICON"))),
                RecordDetailHelpers.Scalar("Message Icon", RawString(TopBySignature(tree, "MICO"))),
                RecordDetailHelpers.Scalar("Embedded Node", Str(TopBySignature(tree, "NNAM"))),
                RecordDetailHelpers.Link("Pickup Sound", KeepZero(TopBySignature(tree, "YNAM")), resolver),
                RecordDetailHelpers.Link("Putdown Sound", KeepZero(TopBySignature(tree, "ZNAM")), resolver),
                RecordDetailHelpers.Link("Fire 3D Sound", KeepZero(ChildByLabel(gun, "Shoot 3D")), resolver),
                RecordDetailHelpers.Link("Fire Dist Sound", KeepZero(ChildByLabel(gun, "Shoot Dist")), resolver),
                RecordDetailHelpers.Link("Fire 2D Sound", KeepZero(TopBySignature(tree, "XNAM")), resolver),
                // Attack Loop (NAM7) + Melee Block (NAM6) are not read by the typed handler — left null.
                RecordDetailHelpers.Link("Attack Loop Sound", null, resolver),
                RecordDetailHelpers.Link("Dry Fire Sound", KeepZero(TopBySignature(tree, "TNAM")), resolver),
                RecordDetailHelpers.Link("Melee Block Sound", null, resolver),
                RecordDetailHelpers.Link("Idle Sound", KeepZero(TopBySignature(tree, "UNAM")), resolver),
                RecordDetailHelpers.Link("Equip Sound", KeepZero(TopBySignature(tree, "NAM9")), resolver),
                RecordDetailHelpers.Link("Unequip Sound", KeepZero(TopBySignature(tree, "NAM8")), resolver),
                // Mod Silenced 3D = the last WMS1 (typed handler overwrites on each occurrence); Dist is unset.
                RecordDetailHelpers.Link("Mod Silenced 3D",
                    KeepZero(modSound is { Children.Count: > 0 } ? modSound.Children[^1] : null), resolver),
                RecordDetailHelpers.Link("Mod Silenced Dist", null, resolver),
                RecordDetailHelpers.Link("Mod Silenced 2D", KeepZero(TopBySignature(tree, "WMS2")), resolver),
                RecordDetailHelpers.Scalar("Mod Variants", ModVariants(tree))
            ])
        };

        return RecordDetailHelpers.Model("WEAP", formId, editorId, displayName, sections);
    }

    // ETYP int → EquipmentType (the typed handler accepts only -1..13).
    private static EquipmentType EquipmentTypeFromEtyp(IReadOnlyList<DecodedNode> tree)
    {
        var etyp = Int(TopBySignature(tree, "ETYP"));
        return etyp is >= -1 and <= 13 ? (EquipmentType)(int)etyp.Value : EquipmentType.None;
    }

    // A FormID that is dropped when zero (typed "set only when non-zero" fields: Projectile, mod variants).
    private static uint? FormIdOf(DecodedNode? node) => node?.RawValue as uint? is { } v and not 0 ? v : null;

    // A FormID kept even when zero (typed fields assigned straight from ReadFormID: ammo, sounds, crit effect).
    private static uint? KeepZero(DecodedNode? node) => node?.RawValue as uint?;

    // A null-terminated string read from a top-level raw node (ICON/MICO are not modeled by the schema).
    private static string? RawString(DecodedNode? node) =>
        Bytes(node) is { Length: > 0 } b ? EsmStringUtils.ReadNullTermString(b) : null;

    // Reproduce BuildModelVariants' presence rule + CombinationName join: a Base entry when WNAM > 0, then one
    // entry per mod combination that has a 1st-person object (WNM, non-zero) or a 3rd-person mesh (MWD).
    private static string? ModVariants(IReadOnlyList<DecodedNode> tree)
    {
        var names = new List<string>();
        if (FormIdOf(TopBySignature(tree, "WNAM")) is > 0)
        {
            names.Add(new WeaponModelVariant { Combination = WeaponModCombination.None }.CombinationName);
        }

        for (var i = 0; i < 7; i++)
        {
            var obj = FormIdOf(TopBySignature(tree, $"WNM{i + 1}"));
            var mesh = Str(TopBySignature(tree, $"MWD{i + 1}"));
            if (obj is null && string.IsNullOrEmpty(mesh))
            {
                continue;
            }

            names.Add(new WeaponModelVariant { Combination = ModCombinations[i] }.CombinationName);
        }

        return names.Count > 0 ? string.Join(", ", names) : null;
    }
}
