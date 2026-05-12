using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Probes;

/// <summary>
///     Probe for TESObjectWEAP.criticalData (OBJ_WEAP_CRITICAL, 16 bytes at PDB +464).
///     Layout per the CRDT schema:
///     <c>UInt16 CritDamage(+0) | Float CritChance(+4) | UInt8 EffectOnDeath(+8) |
///     Padding(3) | FormID CritEffect(+12)</c>.
///     The audit shows 0 records agree on CritDamage / CritChance / CritEffect across
///     both baselines while adjacent fields (Weapon Damage at +176, sound pointers
///     at +252/+256) agree healthily — strong evidence the OBJ_WEAP_DATA block before
///     criticalData has the right size in the reader, but the criticalData block
///     itself sits at a different offset in many builds.
///     This probe sweeps ±8 bytes around the reference position and picks the shift
///     where CritDamage / CritChance / CritEffect read plausible values.
/// </summary>
internal static class RuntimeWeaponCritProbe
{
    private const int BaseStructSize = 924; // TESObjectWEAP (per RuntimeBuildOffsets.GetStructSize(0x28))
    private const int MaxSamples = 24;

    private static readonly int[] CritShiftOptions = [-8, -4, 0, 4, 8];

    private static readonly RuntimeReaderFieldProbe.FieldSpec[] CriticalFields =
    [
        // Group 0 anchor: PickupSound (TESForm-relative; stable across all builds).
        // Establishes that the sample IS a real weapon at the expected struct base.
        new("PickupSound", 252, 0, RuntimeReaderFieldProbe.FieldCheck.PointerToFormType,
            3, (byte)0x0D),

        // Group 1: OBJ_WEAP_CRITICAL block — shifts together.
        new("CritDamage", 464, 1, RuntimeReaderFieldProbe.FieldCheck.Int32Range,
            3, (0, 10_000)),
        new("CritChance", 468, 1, RuntimeReaderFieldProbe.FieldCheck.RangedFloat,
            3, (0f, 100f)),
        // CritEffect → MGEF (FormType 0x10) is the tightest signal — most weapons have
        // a null crit effect, so a wrong offset would resolve non-MGEF pointers and
        // lose these points.
        new("CritEffect", 476, 1, RuntimeReaderFieldProbe.FieldCheck.PointerToFormType,
            4, (byte)0x10)
    ];

    public static RuntimeWeaponCritProbeResult? Probe(
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

        var result = RuntimeReaderFieldProbe.Probe(
            context,
            weapEntries,
            CriticalFields,
            groupCount: 1,
            CritShiftOptions,
            BaseStructSize,
            "WeaponCritLayout",
            MaxSamples,
            log,
            editorIdsByFormId);

        if (result == null)
        {
            return null;
        }

        var critShift = result.Winner.Layout.Length > 1 ? result.Winner.Layout[1] : 0;
        var margin = result.WinnerScore - result.RunnerUpScore;
        // Tight margin requirement: CritEffect on most weapons is null, so total scores
        // are modest. Require the winner to clear the runner-up by at least one full
        // CritDamage/CritChance signal (3 points) before applying the shift.
        var isHighConfidence = result.WinnerScore > 0 && margin >= 3;

        log?.Invoke(
            $"  [WeaponCritProbe] Selected crit-block shift={critShift:+0;-0;0} " +
            $"(score={result.WinnerScore}, runner-up={result.RunnerUpScore}, " +
            $"margin={margin}, confidence={(isHighConfidence ? "high" : "low")}, " +
            $"samples={result.SampleCount})");

        return new RuntimeWeaponCritProbeResult(
            critShift,
            result.WinnerScore,
            result.RunnerUpScore,
            result.SampleCount,
            isHighConfidence);
    }
}
