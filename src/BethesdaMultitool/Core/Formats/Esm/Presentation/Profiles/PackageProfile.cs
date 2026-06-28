using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.AI;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Decoding;
using BethesdaMultitool.Core.Games;
using static BethesdaMultitool.Core.Formats.Esm.Presentation.Profiles.DecodedTreeReader;

namespace BethesdaMultitool.Core.Formats.Esm.Presentation.Profiles;

/// <summary>
///     The PACK presentation profile — reproduces <see cref="RecordDetailBuilders.BuildPackage" />'s sections
///     (Identity / Schedule / Location / Target / Use Weapon) from a schema-decoded tree. FNV byte-exact
///     (proven by <c>PackageProfileParityTests</c>, against a BuildPackage with the two un-recoverable fields
///     stripped); other games get the same sectioned shape.
///     <para>
///         PACK is the wave's most heterogeneous record. PKDT/PSDT decode as plain structs; PTDT/PTD2/PLD2
///         carry a union mid-struct so the union + everything after it (FormID, count, radius) lands in one
///         raw tail child; PLDT is unmodeled by the generator and so arrives as a top-level raw node. Two
///         fields are read by the typed handler from bytes xEdit marks <em>Unused</em>, which the decoder
///         discards — PKPT byte 1 (Linked Start) and PKW3 byte 20 (the weapon FormID) — so the profile can't
///         recover them; FNV keeps BuildPackage for those, and the parity gate strips them from the reference.
///     </para>
/// </summary>
internal sealed class PackageProfile : IRecordProfile
{
    public string RecordType => "PACK";

    public RecordDetailModel Build(
        uint formId, string? editorId, string? displayName,
        IReadOnlyList<DecodedNode> tree, BethesdaGame game, FormIdResolver resolver)
    {
        var pkdt = TopBySignature(tree, "PKDT");
        var data = pkdt is not null
            ? new PackageData { Type = (byte)(Int(ChildByLabel(pkdt, "Type")) ?? 0) }
            : null;
        var typeName = data?.TypeName ?? "AI Package";
        var isRepeatable = (Int(ChildByLabel(TopBySignature(tree, "PKPT"), "Repeatable")) ?? 0) != 0;

        var schedule = Schedule(TopBySignature(tree, "PSDT"));
        var location = LocationFromRaw(TopBySignature(tree, "PLDT"));
        var location2 = LocationFromStruct(TopBySignature(tree, "PLD2"));
        var target = TargetFromStruct(TopBySignature(tree, "PTDT"));
        var target2 = TargetFromStruct(TopBySignature(tree, "PTD2"));
        var useWeapon = UseWeapon(TopBySignature(tree, "PKW3"));

        var sections = new List<RecordDetailSection>
        {
            RecordDetailHelpers.Section("Identity",
            [
                RecordDetailHelpers.Scalar("Form ID", $"0x{formId:X8}"),
                RecordDetailHelpers.Scalar("Editor ID", editorId ?? "(none)"),
                RecordDetailHelpers.Scalar("Type", typeName),
                RecordDetailHelpers.Scalar("Repeatable", isRepeatable ? "Yes" : "No"),
                // Linked Start (PKPT byte 1) is in a schema-Unused region — not tree-derivable, defaults No.
                RecordDetailHelpers.Scalar("Linked Start", "No")
            ]),
            RecordDetailHelpers.Section("Schedule",
            [
                RecordDetailHelpers.Scalar("Summary", schedule?.Summary),
                RecordDetailHelpers.Scalar("Month", schedule?.MonthName),
                RecordDetailHelpers.Scalar("Day", schedule?.DayOfWeekName),
                RecordDetailHelpers.Scalar("Date", schedule?.Date.ToString()),
                RecordDetailHelpers.Scalar("Hour", schedule?.Time.ToString()),
                RecordDetailHelpers.Scalar("Duration", schedule?.Duration.ToString())
            ]),
            RecordDetailHelpers.Section("Location",
            [
                RecordDetailHelpers.Scalar("Primary", RecordDetailHelpers.FormatPackageLocation(location, resolver)),
                RecordDetailHelpers.Scalar("Secondary", RecordDetailHelpers.FormatPackageLocation(location2, resolver))
            ]),
            RecordDetailHelpers.Section("Target",
            [
                RecordDetailHelpers.Scalar("Primary", RecordDetailHelpers.FormatPackageTarget(target, resolver)),
                RecordDetailHelpers.Scalar("Secondary", RecordDetailHelpers.FormatPackageTarget(target2, resolver))
            ]),
            RecordDetailHelpers.Section("Use Weapon",
            [
                // Weapon (PKW3 byte 20) is in a schema-Unused region — not tree-derivable, so the link is absent.
                RecordDetailHelpers.Link("Weapon", useWeapon?.WeaponFormId, resolver),
                RecordDetailHelpers.Scalar("Always Hit", RecordDetailHelpers.BoolText(useWeapon?.AlwaysHit)),
                RecordDetailHelpers.Scalar("Do No Damage", RecordDetailHelpers.BoolText(useWeapon?.DoNoDamage)),
                RecordDetailHelpers.Scalar("Crouch", RecordDetailHelpers.BoolText(useWeapon?.Crouch)),
                RecordDetailHelpers.Scalar("Hold Fire", RecordDetailHelpers.BoolText(useWeapon?.HoldFire)),
                RecordDetailHelpers.Scalar("Burst Count", useWeapon?.BurstCount.ToString()),
                RecordDetailHelpers.Scalar("Volley", RecordDetailHelpers.FormatVolley(useWeapon))
            ])
        };

        return RecordDetailHelpers.Model("PACK", formId, editorId, typeName, sections);
    }

    // PSDT: 5 single-byte schedule fields + Duration (S32).
    private static PackageSchedule? Schedule(DecodedNode? psdt) =>
        psdt is null
            ? null
            : new PackageSchedule
            {
                Month = (sbyte)(Int(ChildByLabel(psdt, "Month")) ?? 0),
                DayOfWeek = (sbyte)(Int(ChildByLabel(psdt, "Day of week")) ?? 0),
                Date = (sbyte)(Int(ChildByLabel(psdt, "Date")) ?? 0),
                Time = (sbyte)(Int(ChildByLabel(psdt, "Time")) ?? 0),
                Duration = (int)(Int(ChildByLabel(psdt, "Duration")) ?? 0)
            };

    // PLDT is unmodeled → a top-level raw node: Type(byte) @0, Union(u32) @4, Radius(s32) @8.
    private static PackageLocation? LocationFromRaw(DecodedNode? node) =>
        Bytes(node) is { Length: >= 12 } b
            ? new PackageLocation { Type = b[0], Union = ReadU32(b, 4) ?? 0, Radius = ReadS32(b, 8) ?? 0 }
            : null;

    // PLD2 is a struct with a mid-struct union: "Type"(S32, low byte) + a "Location" raw tail = Union @0, Radius @4.
    private static PackageLocation? LocationFromStruct(DecodedNode? node)
    {
        if (node is null || Bytes(ChildByLabel(node, "Location")) is not { } tail)
        {
            return null;
        }

        return new PackageLocation
        {
            Type = (byte)(Int(ChildByLabel(node, "Type")) ?? 0),
            Union = ReadU32(tail, 0) ?? 0,
            Radius = ReadS32(tail, 4) ?? 0
        };
    }

    // PTDT/PTD2: "Type"(S32, low byte) + a "Target" raw tail = FormID/Type @0, Count/Distance @4, AcquireRadius @8.
    private static PackageTarget? TargetFromStruct(DecodedNode? node)
    {
        if (node is null || Bytes(ChildByLabel(node, "Target")) is not { } tail)
        {
            return null;
        }

        return new PackageTarget
        {
            Type = (byte)(Int(ChildByLabel(node, "Type")) ?? 0),
            FormIdOrType = ReadU32(tail, 0) ?? 0,
            CountDistance = ReadS32(tail, 4) ?? 0,
            AcquireRadius = ReadFloat(tail, 8) ?? 0f
        };
    }

    // PKW3: the typed handler reads the four bool flags from the low byte of each Flags word; volley data from
    // the two sub-structs. The weapon FormID (byte 20) is in a schema-Unused region, so it stays null.
    private static PackageUseWeaponData? UseWeapon(DecodedNode? pkw3)
    {
        if (pkw3 is null)
        {
            return null;
        }

        var flags = (uint)(Int(ChildByLabel(pkw3, "Flags")) ?? 0);
        var shots = ChildByLabel(pkw3, "Shoots Per Volleys");
        var pause = ChildByLabel(pkw3, "Pause Between Volleys");
        return new PackageUseWeaponData
        {
            AlwaysHit = (flags & 0xFF) != 0,
            DoNoDamage = ((flags >> 8) & 0xFF) != 0,
            Crouch = ((flags >> 16) & 0xFF) != 0,
            HoldFire = ((flags >> 24) & 0xFF) != 0,
            BurstCount = (ushort)(Int(ChildByLabel(pkw3, "Number of Bursts")) ?? 0),
            VolleyShotsMin = (ushort)(Int(ChildByLabel(shots, "Min")) ?? 0),
            VolleyShotsMax = (ushort)(Int(ChildByLabel(shots, "Max")) ?? 0),
            VolleyWaitMin = Float(ChildByLabel(pause, "Min")) ?? 0f,
            VolleyWaitMax = Float(ChildByLabel(pause, "Max")) ?? 0f,
            WeaponFormId = null
        };
    }
}
