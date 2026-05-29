using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Planner;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter.Encoders.World;

/// <summary>
///     Planned encoder shared by REFR / ACHR / ACRE. All three placed-reference signatures
///     use the same <see cref="PlacedReference" /> model and the same encoder logic;
///     the record type just labels the GRUP they emit under. The constructor takes the
///     signature so the same class registers three times.
/// </summary>
/// <remarks>
///     Tier 5b kickoff: placed-reference emission currently happens through
///     <c>CellGrupBuilder</c>'s persistent / temporary / VWD children GRUP loop. Until
///     that pipeline routes through the planner, this encoder is registered but never
///     invoked. The implementation is correct so the routing change is a one-line swap
///     when it lands.
/// </remarks>
public sealed class PlannedPlacedReferenceEncoder : IPlannedRecordEncoder<PlacedReference>
{
    public string RecordType { get; }

    public PlannedPlacedReferenceEncoder(string recordType)
    {
        if (recordType is not ("REFR" or "ACHR" or "ACRE"))
        {
            throw new ArgumentException(
                $"Placed-reference encoder expects REFR/ACHR/ACRE, got '{recordType}'.",
                nameof(recordType));
        }

        RecordType = recordType;
    }

    public EncodedRecord Encode(PlacedReference model, RecordPlan plan, PlanReferenceLookup refs)
    {
        return plan.Disposition switch
        {
            RecordDisposition.New => RefrEncoder.EncodeNewPlacedReference(
                model, refs.EmittedFormIds, refs.SourceToEmittedFormId),
            RecordDisposition.Override => RefrEncoder.EncodePlacedReference(model),
            _ => throw new InvalidOperationException(
                $"PlannedPlacedReferenceEncoder called with disposition {plan.Disposition}; expected New or Override."),
        };
    }
}
