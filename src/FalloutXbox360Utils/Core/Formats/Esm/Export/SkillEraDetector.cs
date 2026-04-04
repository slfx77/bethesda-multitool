using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Detects which skill era a dump belongs to by examining AVIF records
///     and weapon Skill field values. Identifies Big Guns vs Guns merge
///     and Throwing vs Survival replacement.
/// </summary>
/// <remarks>
///     During Fallout New Vegas development:
///     - Big Guns (AV 33) was cut and merged into Small Guns (AV 41), renamed to Guns.
///     - Throwing (AV 44) was replaced by Survival.
///     The skill slot array stayed 14 entries; the change is purely which names/AV records
///     occupy slots 1 (Big Guns), 9 (Guns/Small Guns), and 12 (Survival/Throwing).
/// </remarks>
internal static class SkillEraDetector
{
    // AV codes for the contested skill slots.
    private const int AvBigGuns = 33;
    private const int AvSmallGuns = 41;
    private const int AvThrowing = 44;

    /// <summary>
    ///     Detected skill era profile for a single dump or ESM.
    /// </summary>
    internal sealed record SkillEraProfile(
        bool BigGunsActive,
        string Slot1Name,
        string Slot9Name,
        string Slot12Name)
    {
        /// <summary>Human-readable one-line summary.</summary>
        public string Summary
        {
            get
            {
                if (BigGunsActive)
                {
                    return $"Early: {Slot1Name} active, {Slot9Name}, {Slot12Name}";
                }

                return $"Final: Big Guns merged → {Slot9Name}, {Slot12Name}";
            }
        }
    }

    /// <summary>
    ///     Detects the skill era from AVIF records and weapon Skill field values.
    /// </summary>
    internal static SkillEraProfile Detect(RecordCollection records)
    {
        // 1. Check AVIF FullNames for the contested AV codes.
        string? avifBigGunsName = null;
        string? avifSmallGunsName = null;
        string? avifThrowingName = null;

        foreach (var avif in records.ActorValueInfos)
        {
            if (avif.EditorId == null) continue;

            if (FormIdResolver.AvifEditorIdToAvCode.TryGetValue(avif.EditorId, out var avCode))
            {
                switch (avCode)
                {
                    case AvBigGuns:
                        avifBigGunsName = avif.FullName;
                        break;
                    case AvSmallGuns:
                        avifSmallGunsName = avif.FullName;
                        break;
                    case AvThrowing:
                        avifThrowingName = avif.FullName;
                        break;
                }
            }
        }

        // 2. Scan weapon Skill fields for Big Guns usage (AV 33).
        var anyWeaponUsesBigGuns = false;
        foreach (var weapon in records.Weapons)
        {
            if (weapon.Skill == AvBigGuns)
            {
                anyWeaponUsesBigGuns = true;
                break;
            }
        }

        // 3. Determine Big Guns status.
        //    Big Guns is "active" if:
        //    - AVIF for AV 33 has a non-empty, non-OBSOLETE FullName (the skill is named), OR
        //    - Any weapon references Skill == 33 AND the AVIF is not explicitly marked OBSOLETE.
        //    Names like "Big Guns - OBSOLETE" indicate the skill was cut; stale weapon data
        //    referencing Skill 33 does not override an explicit OBSOLETE marker.
        var isObsolete = avifBigGunsName?.Contains("OBSOLETE", StringComparison.OrdinalIgnoreCase) == true;
        var bigGunsActive = !isObsolete
            && (!string.IsNullOrEmpty(avifBigGunsName) || anyWeaponUsesBigGuns);

        // 4. Determine slot display names.
        var slot1Name = !string.IsNullOrEmpty(avifBigGunsName) && !isObsolete
            ? avifBigGunsName
            : bigGunsActive
                ? "Big Guns"
                : "(unused)";

        var slot9Name = !string.IsNullOrEmpty(avifSmallGunsName)
            ? avifSmallGunsName
            : "Guns"; // default to final name

        var slot12Name = !string.IsNullOrEmpty(avifThrowingName)
            ? avifThrowingName
            : "Survival"; // default to final name

        return new SkillEraProfile(bigGunsActive, slot1Name, slot9Name, slot12Name);
    }
}
