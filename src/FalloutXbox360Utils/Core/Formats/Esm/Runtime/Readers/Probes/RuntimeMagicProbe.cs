using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Probes;

/// <summary>
///     Probe for magic/effect types: MGEF (192B), SPEL (84B), ENCH (84B), PERK (96B).
///     Uses MGEF as the primary probe target (largest struct, most validation signals).
///     Group 0: TESForm (anchored), Group 1: all post-TESForm fields.
/// </summary>
internal static class RuntimeMagicProbe
{
    private static readonly RuntimeReaderFieldProbe.FieldSpec[] MgefProbeFields =
    [
        // Group 1: Post-TESForm fields in EffectSetting
        new("Model", 44, 1, RuntimeReaderFieldProbe.FieldCheck.BSStringT, 2),
        new("FullName", 76, 1, RuntimeReaderFieldProbe.FieldCheck.BSStringT, 2),
        new("Data.BaseCost", 108, 1, RuntimeReaderFieldProbe.FieldCheck.NormalFloat),
        new("Data.AssociatedItem", 112, 1, RuntimeReaderFieldProbe.FieldCheck.PointerToForm),
        new("Data.Light", 128, 1, RuntimeReaderFieldProbe.FieldCheck.PointerToForm),
        new("Data.ProjSpeed", 132, 1, RuntimeReaderFieldProbe.FieldCheck.NormalFloat),
        new("Data.EffectShader", 136, 1, RuntimeReaderFieldProbe.FieldCheck.PointerToForm)
    ];

    private static readonly int[] ShiftOptions = [-8, -4, 0, 4, 8];

    public static RuntimeLayoutProbeResult<int[]>? Probe(
        RuntimeMemoryContext context,
        IReadOnlyList<RuntimeEditorIdEntry> entries,
        Action<string>? log = null)
    {
        // Probe using MGEF entries (largest and most distinctive magic struct)
        var mgefEntries = entries.Where(e => e.FormType == 0x10).ToList();
        if (mgefEntries.Count == 0)
        {
            return null;
        }

        return RuntimeReaderFieldProbe.Probe(
            context,
            mgefEntries,
            MgefProbeFields,
            1,
            ShiftOptions,
            192,
            "MagicLayout",
            log: log);
    }
}
