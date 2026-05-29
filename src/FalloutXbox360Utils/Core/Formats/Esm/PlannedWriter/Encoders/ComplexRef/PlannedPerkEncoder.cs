using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Magic;
using FalloutXbox360Utils.Core.Formats.Esm.Planner;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Magic;

namespace FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter.Encoders.ComplexRef;

/// <summary>
///     Planned encoder for PERK. Transitional pass-through to legacy
///     <c>PerkEncoder.EncodeNew(perk, validFormIds, remapTable)</c>; the legacy path
///     sanitizes CTDA condition FormIDs against the union, which the planner now feeds
///     from its emit set.
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
