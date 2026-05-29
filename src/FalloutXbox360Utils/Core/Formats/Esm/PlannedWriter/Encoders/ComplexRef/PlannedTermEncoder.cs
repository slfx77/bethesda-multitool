using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Planner;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter.Encoders.ComplexRef;

/// <summary>Planned encoder for TERM (terminal). Transitional pass-through.</summary>
public sealed class PlannedTermEncoder : IPlannedRecordEncoder<TerminalRecord>
{
    private static readonly EncodedRecord EmptyEncoded =
        new() { Subrecords = [], Warnings = [] };

    public string RecordType => "TERM";

    public EncodedRecord Encode(TerminalRecord model, RecordPlan plan, PlanReferenceLookup refs)
    {
        return plan.Disposition switch
        {
            RecordDisposition.New => TermEncoder.EncodeNew(
                model, refs.EmittedFormIds, refs.SourceToEmittedFormId),
            RecordDisposition.Override => EmptyEncoded,
            _ => throw new InvalidOperationException(
                $"PlannedTermEncoder called with disposition {plan.Disposition}; expected New or Override."),
        };
    }
}
