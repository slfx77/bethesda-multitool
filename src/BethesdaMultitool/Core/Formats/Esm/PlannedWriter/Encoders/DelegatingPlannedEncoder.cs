using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;

namespace BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Encoders;

/// <summary>
///     The shared shape for simple planned encoders: New delegates to an existing
///     <c>EncodeNew(model)</c> primitive, Override emits an empty record, any other disposition is
///     a routing bug. Register instances through <c>PlannedEncoders.Simple</c>.
/// </summary>
public sealed class DelegatingPlannedEncoder<TModel>(
    string recordType,
    Func<TModel, EncodedRecord> encodeNew) : IPlannedRecordEncoder<TModel>
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
                $"Planned {RecordType} encoder called with disposition {plan.Disposition}; expected New or Override.")
        };
    }
}
