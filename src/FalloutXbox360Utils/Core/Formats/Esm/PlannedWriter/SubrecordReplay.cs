using FalloutXbox360Utils.Core.Formats.Esm.Planner;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;

namespace FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter;

/// <summary>
///     Replays a planner-computed <see cref="SubrecordDecision" /> list against a master
///     record + encoder output, producing the final per-subrecord byte stream for an
///     <c>Override</c> emit.
/// </summary>
/// <remarks>
///     Tier 0: stub. The legacy <c>RecordMergeEngine</c> still drives Override emission for
///     all types. As tiers ship, encoders return <see cref="EncodedRecord" /> instances plus
///     pre-computed <see cref="SubrecordDecision" /> lists; the replay walks those.
///     The replay is pure — it never consults <c>SubrecordMergePolicy</c>, only the planner's
///     decision array. Policy lookups belong in phase B (Disposition).
/// </remarks>
public static class SubrecordReplay
{
    /// <summary>
    ///     Replay one override record. Throws <see cref="NotImplementedException" /> until
    ///     a tier needs it; Tier 0 ships the type signature only.
    /// </summary>
    public static IReadOnlyList<EncodedSubrecord> Replay(
        ParsedMainRecord master,
        EncodedRecord encoded,
        IReadOnlyList<SubrecordDecision> decisions)
    {
        ArgumentNullException.ThrowIfNull(master);
        ArgumentNullException.ThrowIfNull(encoded);
        ArgumentNullException.ThrowIfNull(decisions);

        throw new NotImplementedException(
            "SubrecordReplay is stubbed until Tier 3 introduces real override records through the planned writer.");
    }
}
