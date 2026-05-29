using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Planner;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Quest;

namespace FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter.Encoders.ComplexRef;

/// <summary>
///     Planned encoder for SCPT (script). Tier 3 transitional: delegates to
///     <c>ScptEncoder.EncodeNew(script, validFormIds, remapTable)</c> with the plan's
///     emit set + alias table so SCRO/SCRV resolution mirrors legacy byte-for-byte.
///     This is the encoder at the heart of the v54 dangle bug; the planner is the
///     long-term path for resolving SCROs only against actually-emitted FormIDs rather
///     than the legacy allocated-but-maybe-not-emitted set.
/// </summary>
public sealed class PlannedScptEncoder : IPlannedRecordEncoder<ScriptRecord>
{
    private static readonly EncodedRecord EmptyEncoded =
        new() { Subrecords = [], Warnings = [] };

    public string RecordType => "SCPT";

    public EncodedRecord Encode(ScriptRecord model, RecordPlan plan, PlanReferenceLookup refs)
    {
        return plan.Disposition switch
        {
            RecordDisposition.New => ScptEncoder.EncodeNew(
                model, refs.EmittedFormIds, refs.SourceToEmittedFormId),
            RecordDisposition.Override => EmptyEncoded,
            _ => throw new InvalidOperationException(
                $"PlannedScptEncoder called with disposition {plan.Disposition}; expected New or Override."),
        };
    }
}
