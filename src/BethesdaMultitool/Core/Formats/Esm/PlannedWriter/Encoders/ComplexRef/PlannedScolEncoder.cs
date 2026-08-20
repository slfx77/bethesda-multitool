using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.World;

namespace BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Encoders.ComplexRef;

/// <summary>
///     Planned encoder for SCOL (static collection). Transitional pass-through. Legacy
///     <c>ScolEncoder.EncodeNew(scol, masterFormIds, emittedNewStats)</c> takes the two
///     validity sets separately; the planner passes its final liveness set for both and its
///     source alias table separately. This lets source-domain ONAM values validate against
///     newly allocated STATs before PlanWriter's generic byte remapper writes the final ID.
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
                model, refs.EmittedFormIds, refs.EmittedFormIds,
                refs.SourceToEmittedFormId),
            RecordDisposition.Override => EmptyEncoded,
            _ => throw new InvalidOperationException(
                $"PlannedScolEncoder called with disposition {plan.Disposition}; expected New or Override.")
        };
    }
}
