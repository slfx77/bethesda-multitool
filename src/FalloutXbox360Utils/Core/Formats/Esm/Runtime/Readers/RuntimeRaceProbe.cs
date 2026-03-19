using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Probe for TESRace layout auto-detection across build versions.
///     TESRace (1260 bytes) has a deep inheritance chain:
///     TESForm → TESFullName → TESDescription → TESSpellList → TESReactionForm → ... → TESRace.
///     Group 0: TESForm (anchored), Group 1: mid-chain (FullName through FaceGenClamps),
///     Group 2: late fields (voice types, age races at +1228+).
/// </summary>
internal static class RuntimeRaceProbe
{
    private static readonly RuntimeReaderFieldProbe.FieldSpec[] ProbeFields =
    [
        // Group 1: TESFullName through TESReactionForm
        new("FullName", 44, 1, RuntimeReaderFieldProbe.FieldCheck.BSStringT, Weight: 2),
        new("RaceData.MaleHeight", 112, 1, RuntimeReaderFieldProbe.FieldCheck.RangedFloat, CheckArg: (0.5f, 2.0f)),
        new("RaceData.FemaleHeight", 116, 1, RuntimeReaderFieldProbe.FieldCheck.RangedFloat, CheckArg: (0.5f, 2.0f)),
        new("RaceData.MaleWeight", 120, 1, RuntimeReaderFieldProbe.FieldCheck.RangedFloat, CheckArg: (0.5f, 2.0f)),
        new("RaceData.FemaleWeight", 124, 1, RuntimeReaderFieldProbe.FieldCheck.RangedFloat, CheckArg: (0.5f, 2.0f)),
        new("FaceGenClamp1", 176, 1, RuntimeReaderFieldProbe.FieldCheck.NormalFloat),
        new("FaceGenClamp2", 180, 1, RuntimeReaderFieldProbe.FieldCheck.NormalFloat),
        // Group 2: Late TESRace-specific fields
        new("DefaultVoiceMale", 1228, 2, RuntimeReaderFieldProbe.FieldCheck.PointerToForm, Weight: 2),
        new("DefaultVoiceFemale", 1232, 2, RuntimeReaderFieldProbe.FieldCheck.PointerToForm, Weight: 2),
        new("OldRace", 1236, 2, RuntimeReaderFieldProbe.FieldCheck.PointerToFormType, Weight: 2,
            CheckArg: (byte)0x0C),
    ];

    private static readonly int[] ShiftOptions = [-8, -4, 0, 4, 8];

    public static RuntimeLayoutProbeResult<int[]>? Probe(
        RuntimeMemoryContext context,
        IReadOnlyList<RuntimeEditorIdEntry> entries,
        Action<string>? log = null)
    {
        var raceEntries = entries.Where(e => e.FormType == 0x0C).ToList();
        if (raceEntries.Count == 0)
        {
            return null;
        }

        return RuntimeReaderFieldProbe.Probe(
            context,
            raceEntries,
            ProbeFields,
            groupCount: 2,
            ShiftOptions,
            baseStructSize: 1260,
            "RaceLayout",
            log: log);
    }
}
