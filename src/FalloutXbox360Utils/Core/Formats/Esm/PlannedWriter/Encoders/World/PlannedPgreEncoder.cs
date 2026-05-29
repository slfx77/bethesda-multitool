using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Planner;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter.Encoders.World;

/// <summary>
///     Planned encoder for PGRE (placed grenade). Structural mirror of
///     <see cref="PlannedPlacedReferenceEncoder" /> — dispatches to the legacy
///     <see cref="PgreEncoder" /> primitive based on the record's disposition.
/// </summary>
/// <remarks>
///     Registered through <see cref="PlannedEncoders.BuildAll" /> so PGRE shows up in
///     <see cref="PlannedEncoders.KnownRecordTypes" /> and the CLI's
///     <c>--planner-types all</c> resolution. PGRE→parent-cell mapping is a follow-up;
///     until that lands, no production pipeline actually invokes this encoder.
/// </remarks>
public sealed class PlannedPgreEncoder : IPlannedRecordEncoder<PlacedGrenadeRecord>
{
    public string RecordType => "PGRE";

    public EncodedRecord Encode(PlacedGrenadeRecord model, RecordPlan plan, PlanReferenceLookup refs)
    {
        return plan.Disposition switch
        {
            RecordDisposition.New => PgreEncoder.EncodeNew(
                model, refs.EmittedFormIds, refs.SourceToEmittedFormId),
            RecordDisposition.Override => PgreEncoder.EncodeOverride(model),
            _ => throw new InvalidOperationException(
                $"PlannedPgreEncoder called with disposition {plan.Disposition}; expected New or Override."),
        };
    }
}
