using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;

namespace BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Encoders;

/// <summary>
///     The one shape shared by every simple-reference planned encoder: New delegates to a legacy
///     <c>EncodeNew(model)</c> primitive, Override emits an empty record, any other disposition is
///     a routing bug. Replaces 49 structurally identical per-signature classes; register instances
///     through <c>PlannedEncoders.Simple</c>.
/// </summary>
public sealed class DelegatingPlannedEncoder<TModel>(
    string recordType, Func<TModel, EncodedRecord> encodeNew) : IPlannedRecordEncoder<TModel>
    where TModel : class
{
    private readonly EncodedRecord _emptyEncoded = new() { Subrecords = [], Warnings = [] };

    public string RecordType { get; } = recordType;

    public EncodedRecord Encode(TModel model, RecordPlan plan, PlanReferenceLookup refs)
    {
        return plan.Disposition switch
        {
            RecordDisposition.New => encodeNew(model),
            RecordDisposition.Override => _emptyEncoded,
            _ => throw new InvalidOperationException(
                $"Planned {RecordType} encoder called with disposition {plan.Disposition}; expected New or Override."),
        };
    }
}
