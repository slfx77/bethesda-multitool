using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Probes;

/// <summary>
///     Probe for BGSProjectile (PROJ, ~208 bytes) layout auto-detection.
///     The effect reader already uses a build-specific _s shift; this probe
///     replaces that with data-driven detection.
///     Group 0: TESForm (anchored), Group 1: all post-TESForm fields (uniform shift).
/// </summary>
internal static class RuntimeEffectProbe
{
    private static readonly RuntimeReaderFieldProbe.FieldSpec[] ProbeFields =
    [
        // Group 1: Everything after TESForm
        new("FullName", 52, 1, RuntimeReaderFieldProbe.FieldCheck.BSStringT, 2),
        new("Speed", 104, 1, RuntimeReaderFieldProbe.FieldCheck.RangedFloat, CheckArg: (0f, 50000f)),
        new("Gravity", 100, 1, RuntimeReaderFieldProbe.FieldCheck.NormalFloat),
        new("Range", 108, 1, RuntimeReaderFieldProbe.FieldCheck.RangedFloat, CheckArg: (0f, 100000f)),
        new("Light", 112, 1, RuntimeReaderFieldProbe.FieldCheck.PointerToForm, 2),
        new("Explosion", 132, 1, RuntimeReaderFieldProbe.FieldCheck.PointerToForm, 2),
        new("ActiveSound", 136, 1, RuntimeReaderFieldProbe.FieldCheck.PointerToForm),
        new("DefaultWeaponSource", 160, 1, RuntimeReaderFieldProbe.FieldCheck.PointerToForm)
    ];

    private static readonly int[] ShiftOptions = [-8, -4, 0, 4, 8, 12, 16, 20];

    public static RuntimeLayoutProbeResult<int[]>? Probe(
        RuntimeMemoryContext context,
        IReadOnlyList<RuntimeEditorIdEntry> entries,
        Action<string>? log = null)
    {
        var projEntries = entries.Where(e => e.FormType == 0x33).ToList();
        if (projEntries.Count == 0)
        {
            return null;
        }

        return RuntimeReaderFieldProbe.Probe(
            context,
            projEntries,
            ProbeFields,
            1,
            ShiftOptions,
            208,
            "EffectLayout",
            log: log);
    }
}
