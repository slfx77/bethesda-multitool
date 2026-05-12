using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Probes;

/// <summary>
///     Probe for BGSTerminal layout. Two independently-shifting groups:
///     - Group 1 covers TERMINAL_DATA (Difficulty / Flags / ServerType / Unused) at the
///       reference position +132. Difficulty agrees 230/293 records under the current
///       hardcoded offset on July2010↔xex44, so the reference is empirically strong;
///       the probe sweeps ±8 to refine or confirm.
///     - Group 2 covers MenuItemList (BSSimpleList head) at the reference position
///       +152. Currently 0 records agree on MenuItemCount across both audit baselines,
///       suggesting the list head is at a different offset; ±8 sweep may locate it.
///     Note the BaseOffset values match the EXISTING reader (116/117/134 + _s=16 = 132/133/134
///     for the data block, 136 + _s = 152 for the list head). These differ from
///     pdb_layouts.json which puts TERMINAL_DATA at +180 and MenuItemList at +168 —
///     the actual dump layout doesn't match the PDB, so the probe anchors on what
///     empirically agrees, not what the PDB advertises.
/// </summary>
internal static class RuntimeTerminalLayoutProbe
{
    private const int BaseStructSize = 184; // BGSTerminal (per RuntimeBuildOffsets.GetStructSize(0x17))
    private const int MaxSamples = 16;

    private static readonly int[] DataShiftOptions = [-8, -4, 0, 4, 8];
    private static readonly int[] MenuListShiftOptions = [-8, -4, 0, 4, 8];

    private static readonly RuntimeReaderFieldProbe.FieldSpec[] TerminalFields =
    [
        // Group 1: TERMINAL_DATA block (4 bytes). Difficulty weight is highest
        // because it already agrees for 230+ records and is a tight enum check.
        new("Difficulty", 132, 1, RuntimeReaderFieldProbe.FieldCheck.ByteRange,
            5, ((byte)0, (byte)4)),
        new("Flags", 133, 1, RuntimeReaderFieldProbe.FieldCheck.ByteRange,
            2, ((byte)0, (byte)0x1F)),
        new("ServerType", 134, 1, RuntimeReaderFieldProbe.FieldCheck.ByteRange,
            1, ((byte)0, (byte)3)),

        // Group 2: MenuItemList head pointer — must resolve to a plausible heap address.
        new("MenuItemListHead", 152, 2, RuntimeReaderFieldProbe.FieldCheck.NonZeroUInt32,
            3, null)
    ];

    public static RuntimeTerminalLayoutProbeResult? Probe(
        RuntimeMemoryContext context,
        IReadOnlyList<RuntimeEditorIdEntry> entries,
        Action<string>? log = null,
        IReadOnlyDictionary<uint, RuntimeEditorIdEntry>? editorIdsByFormId = null)
    {
        var termEntries = entries.Where(e => e.FormType == 0x17).ToList();
        if (termEntries.Count == 0)
        {
            return null;
        }

        // Run a 2D sweep: cross-product of DataShiftOptions × MenuListShiftOptions.
        // groupCount = 2 means shifts arrays have shape [TESForm-anchor, DataShift, MenuListShift].
        // Engine builds candidates for the variable groups only; group 0 stays at 0.
        var result = RuntimeReaderFieldProbe.Probe(
            context,
            termEntries,
            TerminalFields,
            groupCount: 2,
            // RuntimeReaderFieldProbe.GenerateCandidates uses the same shiftOptions array
            // for every variable group, so a single combined set works for both.
            DataShiftOptions,
            BaseStructSize,
            "TerminalLayout",
            MaxSamples,
            log,
            editorIdsByFormId);

        if (result == null)
        {
            return null;
        }

        var dataShift = result.Winner.Layout.Length > 1 ? result.Winner.Layout[1] : 0;
        var menuListShift = result.Winner.Layout.Length > 2 ? result.Winner.Layout[2] : 0;
        var margin = result.WinnerScore - result.RunnerUpScore;

        // Conservative gate: only apply shifts when the winner clears the runner-up by
        // at least one Difficulty signal (5 points) — Difficulty's high weight means a
        // strong winner reflects a real layout match, not noise on the menu pointer.
        var isHighConfidence = result.WinnerScore > 0 && margin >= 5;

        log?.Invoke(
            $"  [TerminalLayoutProbe] Selected data-shift={dataShift:+0;-0;0} " +
            $"menu-list-shift={menuListShift:+0;-0;0} " +
            $"(score={result.WinnerScore}, runner-up={result.RunnerUpScore}, " +
            $"margin={margin}, confidence={(isHighConfidence ? "high" : "low")}, " +
            $"samples={result.SampleCount})");

        return new RuntimeTerminalLayoutProbeResult(
            dataShift,
            menuListShift,
            result.WinnerScore,
            result.RunnerUpScore,
            result.SampleCount,
            isHighConfidence);
    }
}
