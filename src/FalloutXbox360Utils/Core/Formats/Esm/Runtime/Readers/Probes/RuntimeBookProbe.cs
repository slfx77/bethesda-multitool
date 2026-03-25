using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Probes;

/// <summary>
///     Probe for TESObjectBOOK layout auto-detection across build versions.
///     Uses pointer validation, float checks, and string resolution to score candidates.
/// </summary>
internal static class RuntimeBookProbe
{
    private static readonly RuntimeReaderFieldProbe.FieldSpec[] ProbeFields =
    [
        // Group 1: TESFullName through TESEnchantableForm
        new("FullName", 68, 1, RuntimeReaderFieldProbe.FieldCheck.BSStringT, 2),
        new("Model", 80, 1, RuntimeReaderFieldProbe.FieldCheck.BSStringT, 2),
        new("Enchantment", 136, 1, RuntimeReaderFieldProbe.FieldCheck.PointerToFormType, 2,
            (byte)0x13), // ENCH
        // Group 2: TESValueForm through OBJ_BOOK
        new("Value", 152, 2, RuntimeReaderFieldProbe.FieldCheck.Int32Range, CheckArg: (0, 1_000_000)),
        new("Weight", 160, 2, RuntimeReaderFieldProbe.FieldCheck.RangedFloat, CheckArg: (0f, 500f)),
        new("SkillTaught", 209, 2, RuntimeReaderFieldProbe.FieldCheck.ByteRange, CheckArg: ((byte)0, (byte)80))
    ];

    private static readonly int[] ShiftOptions = [-8, -4, 0, 4, 8];

    public static RuntimeLayoutProbeResult<int[]>? Probe(
        RuntimeMemoryContext context,
        IReadOnlyList<RuntimeEditorIdEntry> entries,
        Action<string>? log = null)
    {
        var bookEntries = entries.Where(e => e.FormType == 0x19).ToList();
        if (bookEntries.Count == 0)
        {
            return null;
        }

        return RuntimeReaderFieldProbe.Probe(
            context,
            bookEntries,
            ProbeFields,
            2,
            ShiftOptions,
            212,
            "BookLayout",
            log: log);
    }
}