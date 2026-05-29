using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Planner;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Item;

namespace FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter.Encoders.SimpleRef;

/// <summary>
///     Planned encoder for WEAP. Tier 2: transitional. Delegates to the legacy
///     <c>WeapEncoder.EncodeNew(weap, validFormIds, remapTable)</c> with whole-plan
///     accessors so NAM0/REPL/INAM/CRDT/DNAM-projectile resolution mirrors legacy
///     byte-for-byte.
/// </summary>
public sealed class PlannedWeapEncoder : IPlannedRecordEncoder<WeaponRecord>
{
    private readonly WeapEncoder _legacy = new();

    public string RecordType => "WEAP";

    public EncodedRecord Encode(WeaponRecord model, RecordPlan plan, PlanReferenceLookup refs)
    {
        return plan.Disposition switch
        {
            RecordDisposition.New => WeapEncoder.EncodeNew(
                model, refs.EmittedFormIds, refs.SourceToEmittedFormId),
            RecordDisposition.Override => _legacy.Encode(model),
            _ => throw new InvalidOperationException(
                $"PlannedWeapEncoder called with disposition {plan.Disposition}; expected New or Override."),
        };
    }
}
