using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;

namespace BethesdaMultitool.Core.Formats.Esm.PlannedWriter;

/// <summary>
///     Encoder contract for the planned-writer pipeline. Implementations do not allocate
///     FormIDs or choose dispositions. Field-level resolutions and the remaining whole-plan
///     liveness/remap compatibility data are exposed through <see cref="PlanReferenceLookup" />.
/// </summary>
public interface IPlannedRecordEncoder
{
    /// <summary>The 4-character record signature this encoder handles.</summary>
    string RecordType { get; }

    /// <summary>CLR type of the model this encoder accepts.</summary>
    Type ModelType { get; }

    /// <summary>Encode one planned record.</summary>
    EncodedRecord Encode(object model, RecordPlan plan, PlanReferenceLookup refs);
}

/// <summary>
///     Strongly-typed variant of <see cref="IPlannedRecordEncoder" /> for ergonomic
///     implementations.
/// </summary>
public interface IPlannedRecordEncoder<in TModel> : IPlannedRecordEncoder where TModel : class
{
    EncodedRecord IPlannedRecordEncoder.Encode(object model, RecordPlan plan, PlanReferenceLookup refs)
    {
        if (model is not TModel typed)
        {
            throw new ArgumentException(
                $"Model is not of type {typeof(TModel).Name}: actual {model?.GetType().Name ?? "null"}.",
                nameof(model));
        }

        return Encode(typed, plan, refs);
    }

    Type IPlannedRecordEncoder.ModelType => typeof(TModel);

    /// <summary>Strongly-typed Encode method.</summary>
    EncodedRecord Encode(TModel model, RecordPlan plan, PlanReferenceLookup refs);
}
