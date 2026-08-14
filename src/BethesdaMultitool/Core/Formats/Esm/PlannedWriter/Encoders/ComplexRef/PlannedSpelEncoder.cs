using BethesdaMultitool.Core.Formats.Esm.Models.Records.Magic;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Magic;

namespace BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Encoders.ComplexRef;

/// <summary>Planned SPEL encoder with plan-resolved EFID and effect-condition FormIDs.</summary>
public sealed class PlannedSpelEncoder : IPlannedRecordEncoder<SpellRecord>
{
    private static readonly EncodedRecord EmptyEncoded = new() { Subrecords = [], Warnings = [] };

    public string RecordType => "SPEL";

    public EncodedRecord Encode(SpellRecord model, RecordPlan plan, PlanReferenceLookup refs)
    {
        if (plan.Disposition == RecordDisposition.Override)
        {
            return EmptyEncoded;
        }

        if (plan.Disposition != RecordDisposition.New)
        {
            throw new InvalidOperationException(
                $"PlannedSpelEncoder called with disposition {plan.Disposition}; expected New or Override.");
        }

        var resolution = PlannedMagicEffectResolver.Resolve(RecordType, plan.FormId, model.Effects, refs);
        var encoded = SpelEncoder.EncodeNew(model with { Effects = resolution.Effects });
        return encoded with { Warnings = [.. encoded.Warnings, .. resolution.Warnings] };
    }
}
