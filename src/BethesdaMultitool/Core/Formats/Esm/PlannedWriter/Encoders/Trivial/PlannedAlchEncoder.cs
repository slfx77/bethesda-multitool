using BethesdaMultitool.Core.Formats.Esm.Models.Records.Item;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Planner.References;
using BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Encoders.ComplexRef;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Item;

namespace BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Encoders.Trivial;

/// <summary>
///     Planned encoder for ALCH. Override path emits DATA (4-byte weight) only. New-record
///     path emits EDID + OBND? + FULL? + MODL? + MODT? + ICON? + MICO? + DATA + ENIT? +
///     (EFID + EFIT + CTDA*)*. Every emitted FormID is consumed from the immutable record plan;
///     dangling EFIDs remove one complete effect and dangling CTDA members replace that effect's
///     complete condition expression with a never-fire condition.
/// </summary>
public sealed class PlannedAlchEncoder : IPlannedRecordEncoder<ConsumableRecord>
{
    private readonly AlchEncoder _legacy = new();

    public string RecordType => "ALCH";

    public EncodedRecord Encode(ConsumableRecord model, RecordPlan plan, PlanReferenceLookup refs)
    {
        return plan.Disposition switch
        {
            RecordDisposition.New => EncodeNew(model, plan, refs),
            RecordDisposition.Override => _legacy.Encode(model),
            _ => throw new InvalidOperationException(
                $"PlannedAlchEncoder called with disposition {plan.Disposition}; expected New or Override."),
        };
    }

    private static EncodedRecord EncodeNew(
        ConsumableRecord model,
        RecordPlan plan,
        PlanReferenceLookup refs)
    {
        var warnings = new List<string>();
        var topLevelRemaps = 0;
        var resolvedModel = model with
        {
            ScriptFormId = ResolveOptional(model.ScriptFormId, FieldPath.Subrecord("SCRI"), refs, warnings,
                ref topLevelRemaps),
            PickupSoundFormId = ResolveOptional(model.PickupSoundFormId, FieldPath.Subrecord("YNAM"), refs,
                warnings, ref topLevelRemaps),
            DropSoundFormId = ResolveOptional(model.DropSoundFormId, FieldPath.Subrecord("ZNAM"), refs, warnings,
                ref topLevelRemaps),
            WithdrawalEffectFormId = ResolveOptional(
                model.WithdrawalEffectFormId,
                FieldPath.Member("ENIT", "WithdrawalEffect"),
                refs,
                warnings,
                ref topLevelRemaps),
            ConsumeSoundFormId = ResolveOptional(
                model.ConsumeSoundFormId,
                FieldPath.Member("ENIT", "ConsumeSound"),
                refs,
                warnings,
                ref topLevelRemaps),
        };

        var effectResolution = PlannedMagicEffectResolver.Resolve(
            "ALCH",
            plan.FormId,
            resolvedModel.Effects,
            refs);
        resolvedModel = resolvedModel with { Effects = effectResolution.Effects };

        if (topLevelRemaps > 0)
        {
            warnings.Add(
                $"New ALCH 0x{plan.FormId:X8} planner: remapped {topLevelRemaps} top-level FormID field(s) " +
                "to emitted identities.");
        }

        var encoded = AlchEncoder.EncodeNew(resolvedModel);
        return encoded with
        {
            Warnings = [.. encoded.Warnings, .. warnings, .. effectResolution.Warnings],
        };
    }

    private static uint? ResolveOptional(
        uint? original,
        string fieldPath,
        PlanReferenceLookup refs,
        List<string> warnings,
        ref int remappedFields)
    {
        if (original is not > 0)
        {
            return null;
        }

        var resolved = refs[fieldPath];
        if (resolved.Action != ResolvedRefAction.Resolved
            || resolved.FinalFormId is not uint finalFormId
            || finalFormId == 0)
        {
            warnings.Add(
                $"New ALCH omitted {fieldPath} 0x{original.Value:X8}: " +
                (resolved.Reason ?? "the planner did not produce a usable FormID."));
            return null;
        }

        if (finalFormId != original.Value)
        {
            remappedFields++;
        }

        return finalFormId;
    }
}
