using FalloutXbox360Utils.Core.Formats.Esm.Planner;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;

namespace FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter;

/// <summary>
///     Encoder contract for the planned-writer pipeline. Implementations are pure
///     <c>(model, plan) → bytes</c> — no allocator, no <c>validFormIds</c>, no
///     degrade-on-dangle fallback. Every reference the encoder needs is pre-resolved
///     in <see cref="RecordPlan.References" /> and accessed via <see cref="PlanReferenceLookup" />.
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
    /// <summary>Strongly-typed Encode method.</summary>
    EncodedRecord Encode(TModel model, RecordPlan plan, PlanReferenceLookup refs);

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
}
