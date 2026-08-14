using BethesdaMultitool.Core.Formats.Esm.Models.Records.Magic;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Magic;

namespace BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Encoders.ComplexRef;

/// <summary>
///     Planned encoder for PERK. Reuses the shared
///     <c>PerkEncoder.EncodeNew(perk, validFormIds, remapTable)</c> primitive, which sanitizes
///     CTDA condition FormIDs against the plan's whole reference-liveness set.
/// </summary>
public sealed class PlannedPerkEncoder : IPlannedRecordEncoder<PerkRecord>
{
    private static readonly EncodedRecord EmptyEncoded =
        new() { Subrecords = [], Warnings = [] };

    public string RecordType => "PERK";

    public EncodedRecord Encode(PerkRecord model, RecordPlan plan, PlanReferenceLookup refs)
    {
        return plan.Disposition switch
        {
            RecordDisposition.New => PerkEncoder.EncodeNew(
                model, refs.EmittedFormIds, refs.SourceToEmittedFormId),
            RecordDisposition.Override => EmptyEncoded,
            _ => throw new InvalidOperationException(
                $"PlannedPerkEncoder called with disposition {plan.Disposition}; expected New or Override."),
        };
    }
}
