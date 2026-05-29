using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Planner;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter.Encoders.ComplexRef;

/// <summary>
///     Planned encoder for SCOL (static collection). Transitional pass-through. Legacy
///     <c>ScolEncoder.EncodeNew(scol, masterFormIds, emittedNewStats)</c> takes the two
///     validity sets separately; the planner conservatively passes
///     <see cref="PlanReferenceLookup.EmittedFormIds" /> for both — the planner's emit set
///     already unions master + planner-allocated, so an ONAM part that legitimately points
///     at a master STAT or planner-allocated STAT still resolves.
/// </summary>
public sealed class PlannedScolEncoder : IPlannedRecordEncoder<StaticCollectionRecord>
{
    private static readonly EncodedRecord EmptyEncoded =
        new() { Subrecords = [], Warnings = [] };

    public string RecordType => "SCOL";

    public EncodedRecord Encode(StaticCollectionRecord model, RecordPlan plan, PlanReferenceLookup refs)
    {
        return plan.Disposition switch
        {
            RecordDisposition.New => ScolEncoder.EncodeNew(
                model, refs.EmittedFormIds, refs.EmittedFormIds),
            RecordDisposition.Override => EmptyEncoded,
            _ => throw new InvalidOperationException(
                $"PlannedScolEncoder called with disposition {plan.Disposition}; expected New or Override."),
        };
    }
}
