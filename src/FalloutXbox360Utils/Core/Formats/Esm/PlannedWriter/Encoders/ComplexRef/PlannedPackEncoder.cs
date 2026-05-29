using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.AI;
using FalloutXbox360Utils.Core.Formats.Esm.Planner;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.AI;

namespace FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter.Encoders.ComplexRef;

/// <summary>
///     Planned encoder for PACK (AI package). Tier 4. Transitional pass-through to
///     <c>PackEncoder.EncodeNew(pack, validFormIds, remapTable)</c>; PLDT/PTDT Union FormIDs
///     get sanitized against the emit set. The PLDT-degradation to Type-2 (the original
///     pain point that spawned this entire two-pass effort) still happens inside legacy
///     EncodeNew for now — when the planner-side reference resolver lands, PLDT will
///     downgrade via <see cref="ResolvedRefAction.DowngradeContainer" /> instead.
/// </summary>
public sealed class PlannedPackEncoder : IPlannedRecordEncoder<PackageRecord>
{
    private static readonly EncodedRecord EmptyEncoded =
        new() { Subrecords = [], Warnings = [] };

    public string RecordType => "PACK";

    public EncodedRecord Encode(PackageRecord model, RecordPlan plan, PlanReferenceLookup refs)
    {
        return plan.Disposition switch
        {
            RecordDisposition.New => PackEncoder.EncodeNew(
                model, refs.EmittedFormIds, refs.SourceToEmittedFormId),
            RecordDisposition.Override => EmptyEncoded,
            _ => throw new InvalidOperationException(
                $"PlannedPackEncoder called with disposition {plan.Disposition}; expected New or Override."),
        };
    }
}
