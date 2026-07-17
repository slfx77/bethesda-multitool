using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

namespace BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Encoders.ComplexRef;

/// <summary>
///     Planned IMAD encoder. New records replay the complete captured stream while sound
///     links come only from settled reference decisions; overrides stay master-pure.
/// </summary>
public sealed class PlannedImadEncoder : IPlannedRecordEncoder<ImageSpaceModifierRecord>
{
    private static readonly EncodedRecord EmptyEncoded =
        new() { Subrecords = [], Warnings = [] };

    public string RecordType => "IMAD";

    public EncodedRecord Encode(
        ImageSpaceModifierRecord model,
        RecordPlan plan,
        PlanReferenceLookup refs)
    {
        return plan.Disposition switch
        {
            RecordDisposition.New => EncodeRequiredNew(model, refs),
            RecordDisposition.Override => EmptyEncoded,
            _ => throw new InvalidOperationException(
                $"PlannedImadEncoder called with disposition {plan.Disposition}; expected New or Override."),
        };
    }

    private static EncodedRecord EncodeRequiredNew(
        ImageSpaceModifierRecord model,
        PlanReferenceLookup refs)
    {
        if (!ImageSpaceModifierCaptureValidator.IsCompleteNewCapture(model, out var reason))
        {
            // Disposition normally prevents this path. Throwing is deliberate defense in
            // depth: an empty encoder result would leave the allocated FormID logically
            // live and could allow a dependent SCPT to survive with a phantom SCRO target.
            throw new InvalidOperationException(
                $"Planner admitted incomplete new IMAD 0x{model.FormId:X8}: {reason}.");
        }

        var encoded = ImadEncoder.EncodeOrdered(model, (signature, sourceFormId) =>
            ResolveSound(refs, signature, sourceFormId));
        if (encoded.Subrecords.Count == 0)
        {
            throw new InvalidOperationException(
                $"Complete new IMAD 0x{model.FormId:X8} produced no subrecords.");
        }

        return encoded;
    }

    private static uint? ResolveSound(
        PlanReferenceLookup refs,
        string signature,
        uint sourceFormId)
    {
        if (sourceFormId == 0)
        {
            return 0u;
        }

        if (!refs.TryGet(signature, out var resolved))
        {
            throw new InvalidOperationException(
                $"IMAD encoder found nonzero {signature} 0x{sourceFormId:X8}, but its reference walker did not report it.");
        }

        return resolved.Action == ResolvedRefAction.Resolved
            ? resolved.FinalFormId
            : null;
    }
}
