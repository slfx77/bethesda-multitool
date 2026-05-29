using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Formats.Esm.Planner;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

namespace FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter.Encoders.SimpleRef;

/// <summary>Planned encoder for IPCT (impact data). Delegates to legacy primitives.</summary>
public sealed class PlannedIpctEncoder : IPlannedRecordEncoder<ImpactDataRecord>
{
    private static readonly EncodedRecord EmptyEncoded =
        new() { Subrecords = [], Warnings = [] };

    public string RecordType => "IPCT";

    public EncodedRecord Encode(ImpactDataRecord model, RecordPlan plan, PlanReferenceLookup refs)
    {
        return plan.Disposition switch
        {
            RecordDisposition.New => IpctEncoder.EncodeNew(model),
            RecordDisposition.Override => EmptyEncoded,
            _ => throw new InvalidOperationException(
                $"PlannedIpctEncoder called with disposition {plan.Disposition}; expected New or Override."),
        };
    }
}
