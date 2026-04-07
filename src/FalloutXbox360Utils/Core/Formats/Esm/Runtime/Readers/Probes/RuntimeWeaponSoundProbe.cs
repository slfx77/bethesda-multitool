using System.Text.RegularExpressions;
using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Probes;

/// <summary>
///     Probe for TESObjectWEAP sound field layout auto-detection across build versions.
///     Empirical investigation revealed two distinct struct layouts that don't differ by a
///     simple shift: they have entirely different field counts and strides.
///
///     Layout V1 (early FO3-derived builds: xex / xex1 / xex2): 7 fields, no Distant /
///     AttackLoop / Idle / MeleeBlock / ModSilenced. Fire2D directly follows Fire3D.
///
///     Layout V2 (all FNV-era builds: xex3+, MemDebug, Debug, Jacobstown): 14 fields including
///     all FNV additions. Matches the OBJ_WEAP definition in the MemDebug PDB.
///
///     Both layouts anchor on Fire3D at PDB-relative offset 548 (code base 532 + _s 16). The
///     probe uses pattern-matching against the runtime editor ID hash table to disambiguate
///     which layout the dump uses, plus a fine-grained shift sweep on top of each layout
///     for builds that drift from the reference.
///
///     The PDB-style offsets used here are STRUCT-RELATIVE (not file offsets). Each base
///     offset is meant to be added to `weapon.TesFormOffset` plus the runtime _s shift (16).
///     The probe operates on PDB-style offsets minus _s, matching the convention in
///     RuntimeItemLayouts.
/// </summary>
internal static class RuntimeWeaponSoundProbe
{
    // Compile patterns once. They are case-insensitive substring matches against the
    // resolved EditorID of whatever sound the slot's pointer leads to.
    private static readonly Regex Fire3DPattern =
        new(@"(?i)(fire3d|3dfire)", RegexOptions.Compiled);
    private static readonly Regex FireDistPattern =
        new(@"(?i)(dist|3ddist)", RegexOptions.Compiled);
    private static readonly Regex Fire2DPattern =
        new(@"(?i)(fire2d|2dfire)", RegexOptions.Compiled);
    private static readonly Regex DryFirePattern =
        new(@"(?i)(dryfire|firedry)", RegexOptions.Compiled);
    private static readonly Regex EquipPattern =
        new(@"(?i)equip(?!un)", RegexOptions.Compiled);
    private static readonly Regex UnequipPattern =
        new(@"(?i)(unequip|equipun)", RegexOptions.Compiled);

    // Probe field offsets are STRUCT-RELATIVE (raw buffer offsets, _s already baked in).
    // For Fire3D the field is at struct offset 548 in both V1 and V2 layouts (empirically
    // verified via dmp weapon-sound-layout diagnostic). The probe reads at buffer[offset]
    // directly without adding _s, unlike RuntimeItemLayouts which uses base + _s form.

    // V2 layout (FNV — 14 sound fields).
    // Group 0 anchor: Pickup/Putdown (stable across all builds).
    // Group 1: the entire sound block, all shifting together.
    private static readonly RuntimeReaderFieldProbe.FieldSpec[] LayoutV2Fields =
    [
        new("PickupSound", 252, 0, RuntimeReaderFieldProbe.FieldCheck.PointerToFormType, 3,
            (byte)0x0D),
        new("PutdownSound", 256, 0, RuntimeReaderFieldProbe.FieldCheck.PointerToFormType, 3,
            (byte)0x0D),

        // High-confidence pattern-matched anchors (almost every weapon has these populated)
        new("FireSound3D", 548, 1, RuntimeReaderFieldProbe.FieldCheck.PatternMatchFormId, 5,
            (Fire3DPattern, (byte)0x0D)),
        new("FireSound2D", 556, 1, RuntimeReaderFieldProbe.FieldCheck.PatternMatchFormId, 5,
            (Fire2DPattern, (byte)0x0D)),
        new("DryFireSound", 564, 1, RuntimeReaderFieldProbe.FieldCheck.PatternMatchFormId, 5,
            (DryFirePattern, (byte)0x0D)),
        new("EquipSound", 576, 1, RuntimeReaderFieldProbe.FieldCheck.PatternMatchFormId, 4,
            (EquipPattern, (byte)0x0D)),
        new("UnequipSound", 580, 1, RuntimeReaderFieldProbe.FieldCheck.PatternMatchFormId, 4,
            (UnequipPattern, (byte)0x0D)),

        // Lower-weight optional / pattern-matched fields
        new("FireSoundDist", 552, 1, RuntimeReaderFieldProbe.FieldCheck.PatternMatchFormId, 3,
            (FireDistPattern, (byte)0x0D)),
        new("ImpactDataSet", 596, 1, RuntimeReaderFieldProbe.FieldCheck.PointerToFormType, 2,
            (byte)0x5F),

        // Lowest-weight type fallbacks for slots with no naming convention
        new("AttackLoop", 560, 1, RuntimeReaderFieldProbe.FieldCheck.PointerToFormType, 1,
            (byte)0x0D),
        new("MeleeBlockSound", 568, 1, RuntimeReaderFieldProbe.FieldCheck.PointerToFormType, 1,
            (byte)0x0D),
        new("IdleSound", 572, 1, RuntimeReaderFieldProbe.FieldCheck.PointerToFormType, 1,
            (byte)0x0D)
    ];

    // V1 layout (FO3-derived early builds — 7 sound fields). No Distant / AttackLoop /
    // MeleeBlock / Idle / ModSilenced. Field positions match the empirically observed
    // V1 dump (xex/xex1/xex2): Fire2D directly after Fire3D, ImpactData 8 bytes earlier.
    private static readonly RuntimeReaderFieldProbe.FieldSpec[] LayoutV1Fields =
    [
        new("PickupSound", 252, 0, RuntimeReaderFieldProbe.FieldCheck.PointerToFormType, 3,
            (byte)0x0D),
        new("PutdownSound", 256, 0, RuntimeReaderFieldProbe.FieldCheck.PointerToFormType, 3,
            (byte)0x0D),

        new("FireSound3D", 548, 1, RuntimeReaderFieldProbe.FieldCheck.PatternMatchFormId, 5,
            (Fire3DPattern, (byte)0x0D)),
        // V1: Fire2D is at struct offset 552 (V2 has FireDist there)
        new("FireSound2D", 552, 1, RuntimeReaderFieldProbe.FieldCheck.PatternMatchFormId, 5,
            (Fire2DPattern, (byte)0x0D)),
        // V1: DryFire is at struct offset 560 (V2 has it at 564)
        new("DryFireSound", 560, 1, RuntimeReaderFieldProbe.FieldCheck.PatternMatchFormId, 5,
            (DryFirePattern, (byte)0x0D)),
        // V1: Equip at struct offset 572, Unequip at 576
        new("EquipSound", 572, 1, RuntimeReaderFieldProbe.FieldCheck.PatternMatchFormId, 4,
            (EquipPattern, (byte)0x0D)),
        new("UnequipSound", 576, 1, RuntimeReaderFieldProbe.FieldCheck.PatternMatchFormId, 4,
            (UnequipPattern, (byte)0x0D)),
        // V1: ImpactData at struct offset 588 (V2 at 596)
        new("ImpactDataSet", 588, 1, RuntimeReaderFieldProbe.FieldCheck.PointerToFormType, 2,
            (byte)0x5F)
    ];

    private static readonly int[] FineShiftOptions = [-4, 0, 4];

    public static RuntimeWeaponSoundProbeResult? Probe(
        RuntimeMemoryContext context,
        IReadOnlyList<RuntimeEditorIdEntry> entries,
        Action<string>? log = null,
        IReadOnlyDictionary<uint, RuntimeEditorIdEntry>? editorIdsByFormId = null)
    {
        var weapEntries = entries.Where(e => e.FormType == 0x28).ToList();
        if (weapEntries.Count == 0)
        {
            return null;
        }

        // Run both layout templates and pick the one with the highest score.
        // For each template, also try a fine-grained ±4 shift to handle minor build drift.
        var v2Result = RuntimeReaderFieldProbe.Probe(
            context, weapEntries, LayoutV2Fields, 1,
            FineShiftOptions, 924, "WeaponSoundLayout-V2",
            maxSamples: 24,
            log: log,
            editorIdsByFormId: editorIdsByFormId);

        var v1Result = RuntimeReaderFieldProbe.Probe(
            context, weapEntries, LayoutV1Fields, 1,
            FineShiftOptions, 924, "WeaponSoundLayout-V1",
            maxSamples: 24,
            log: log,
            editorIdsByFormId: editorIdsByFormId);

        var v1Score = v1Result?.WinnerScore ?? -1;
        var v2Score = v2Result?.WinnerScore ?? -1;

        // Need at least one variant to score above zero — otherwise the probe found nothing.
        if (v1Score <= 0 && v2Score <= 0)
        {
            log?.Invoke($"  [WeaponSoundProbe] No layout matched (V1 score={v1Score}, V2 score={v2Score})");
            return null;
        }

        // Pick the higher-scoring layout. Pattern matching gives V1 dumps a low score under
        // V2 (Fire2D pattern fails because the slot reads a non-Fire2D sound) and vice versa.
        RuntimeWeaponSoundLayoutVariant variant;
        int fineShift;
        int winnerScore;
        int runnerUpScore;
        int sampleCount;

        if (v2Score >= v1Score)
        {
            variant = RuntimeWeaponSoundLayoutVariant.V2;
            fineShift = v2Result!.Winner.Layout.Length > 1 ? v2Result.Winner.Layout[1] : 0;
            winnerScore = v2Score;
            runnerUpScore = Math.Max(v1Score, v2Result.RunnerUpScore);
            sampleCount = v2Result.SampleCount;
        }
        else
        {
            variant = RuntimeWeaponSoundLayoutVariant.V1;
            fineShift = v1Result!.Winner.Layout.Length > 1 ? v1Result.Winner.Layout[1] : 0;
            winnerScore = v1Score;
            runnerUpScore = Math.Max(v2Score, v1Result.RunnerUpScore);
            sampleCount = v1Result.SampleCount;
        }

        var margin = winnerScore - runnerUpScore;
        var isHighConfidence = margin >= 5;

        if (log != null)
        {
            var name = variant == RuntimeWeaponSoundLayoutVariant.V1
                ? "V1 (FO3-derived, 7 fields)"
                : "V2 (FNV, 14 fields)";
            log($"  [WeaponSoundProbe] Selected layout {name} fine-shift={fineShift:+0;-0;0} " +
                $"(V1 score={v1Score}, V2 score={v2Score}, margin={margin}, " +
                $"confidence={(isHighConfidence ? "high" : "low")}, samples={weapEntries.Count})");
        }

        return new RuntimeWeaponSoundProbeResult(
            variant, fineShift, winnerScore, runnerUpScore, sampleCount, isHighConfidence);
    }
}
