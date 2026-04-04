using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Probes;

/// <summary>
///     Probe for TESObjectWEAP sound field layout auto-detection across build versions.
///     Some builds are missing a 4-byte field between DryFireSound (PDB +548) and
///     IdleSound (PDB +556), shifting Idle/Equip/Unequip and later fields by -4.
///     Uses pointer validation on sound fields to score candidate offsets.
/// </summary>
internal static class RuntimeWeaponSoundProbe
{
    private static readonly RuntimeReaderFieldProbe.FieldSpec[] ProbeFields =
    [
        // Group 1: Sound pointers that shift together (IdleSound and later).
        // TESSound FormType = 0x0D.
        // IdleSound: many weapons have an idle loop (e.g., miniguns, energy weapons).
        // Not all weapons have all sounds, so use PointerToForm (nullable) with weight 1
        // and PointerToFormType (strict) with weight 2 where sounds are almost always present.
        new("IdleSound", 556, 1, RuntimeReaderFieldProbe.FieldCheck.PointerToFormType, 2,
            (byte)0x0D), // SOUN
        new("EquipSound", 560, 1, RuntimeReaderFieldProbe.FieldCheck.PointerToFormType, 2,
            (byte)0x0D), // SOUN
        new("UnequipSound", 564, 1, RuntimeReaderFieldProbe.FieldCheck.PointerToFormType, 2,
            (byte)0x0D), // SOUN
        new("ImpactDataSet", 568, 1, RuntimeReaderFieldProbe.FieldCheck.PointerToFormType, 2,
            (byte)0x5F), // IPDS

        // Anchor fields that should NOT shift (group 0 is always shift=0).
        // DryFireSound should be stable across builds — validates the probe is meaningful.
        new("DryFireSound", 548, 0, RuntimeReaderFieldProbe.FieldCheck.PointerToFormType, 2,
            (byte)0x0D) // SOUN
    ];

    private static readonly int[] ShiftOptions = [-8, -4, 0, 4, 8];

    public static RuntimeLayoutProbeResult<int[]>? Probe(
        RuntimeMemoryContext context,
        IReadOnlyList<RuntimeEditorIdEntry> entries,
        Action<string>? log = null)
    {
        var weapEntries = entries.Where(e => e.FormType == 0x28).ToList();
        if (weapEntries.Count == 0)
        {
            return null;
        }

        return RuntimeReaderFieldProbe.Probe(
            context,
            weapEntries,
            ProbeFields,
            1, // 1 variable group (the sound block)
            ShiftOptions,
            924, // PDB struct size (+_s applied internally by caller)
            "WeaponSoundLayout",
            log: log);
    }
}
