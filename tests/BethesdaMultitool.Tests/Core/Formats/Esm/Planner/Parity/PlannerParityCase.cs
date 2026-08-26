using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner.Parity;

/// <summary>
///     One "the planner emits the same bytes the legacy encoder does" case.
///     <para>
///         Every such test has the identical shape — build a synthetic record, run it through the
///         legacy encoder, hand both to
///         <see cref="PlannerTier1ParityHelper.AssertNewRecordParity" /> — and differs only in the
///         record type, its FormID, and which encoder to call. Capturing that as data lets the
///         tiers share a single theory body instead of repeating the same four lines per record
///         type.
///     </para>
///     <para>
///         <paramref name="Build" /> is a factory rather than a prepared value so each theory case
///         constructs its own record: a shared instance would be reused across cases and let one
///         test observe another's mutations.
///     </para>
/// </summary>
/// <param name="Signature">The 4-character record signature, e.g. <c>GLOB</c>.</param>
/// <param name="Label">Display name for the test case; distinguishes rows sharing a signature.</param>
/// <param name="Build">Produces the synthetic record together with its legacy encoding.</param>
public sealed record PlannerParityCase(
    string Signature,
    string Label,
    Func<(uint FormId, object Model, EncodedRecord Legacy)> Build)
{
    public override string ToString()
    {
        return Label;
    }
}
