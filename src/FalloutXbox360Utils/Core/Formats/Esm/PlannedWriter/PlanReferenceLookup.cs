using FalloutXbox360Utils.Core.Formats.Esm.Planner;

namespace FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter;

/// <summary>
///     Per-record accessor for <see cref="RecordPlan.References" />. Encoders look up
///     pre-resolved references by canonical field path — they never call into the
///     <c>ReferenceResolver</c> or validate against any FormID set.
/// </summary>
/// <remarks>
///     Also exposes the whole-plan <see cref="EmittedFormIds" /> and
///     <see cref="SourceToEmittedFormId" /> as transitional accessors. Encoders being
///     migrated incrementally (Tier 2+) can pass these straight through to their legacy
///     <c>EncodeNew(model, validFormIds, remapTable)</c> overload while the per-field
///     walker / <see cref="ResolvedRef" /> work catches up.
/// </remarks>
public sealed class PlanReferenceLookup
{
    private readonly Dictionary<string, ResolvedRef> _byPath;
    private readonly EmitPlan? _plan;

    public PlanReferenceLookup(RecordPlan record)
        : this(record, plan: null)
    {
    }

    public PlanReferenceLookup(RecordPlan record, EmitPlan? plan)
    {
        ArgumentNullException.ThrowIfNull(record);

        _plan = plan;
        _byPath = new Dictionary<string, ResolvedRef>(record.References.Length, StringComparer.Ordinal);
        foreach (var resolved in record.References)
        {
            _byPath[resolved.FieldPath] = resolved;
        }
    }

    /// <summary>
    ///     Whole-plan emit set. Transitional accessor for encoders still delegating to
    ///     legacy <c>EncodeNew(model, validFormIds, …)</c> overloads.
    /// </summary>
    public IReadOnlySet<uint> EmittedFormIds =>
        _plan?.EmittedFormIds
        ?? throw new InvalidOperationException(
            "PlanReferenceLookup was constructed without an EmitPlan — whole-plan accessors are unavailable. " +
            "Pass the plan via the (record, plan) constructor.");

    /// <summary>
    ///     Whole-plan source→allocated FormID map. Transitional accessor for encoders still
    ///     delegating to legacy <c>EncodeNew(model, …, remapTable)</c> overloads.
    /// </summary>
    public IReadOnlyDictionary<uint, uint> SourceToEmittedFormId =>
        _plan?.SourceToEmittedFormId
        ?? throw new InvalidOperationException(
            "PlanReferenceLookup was constructed without an EmitPlan — whole-plan accessors are unavailable. " +
            "Pass the plan via the (record, plan) constructor.");

    /// <summary>
    ///     Reference for <paramref name="fieldPath" />. Throws if the path is not present;
    ///     encoders only key off paths their walker reported, so a missing path means the
    ///     encoder and walker disagree (a bug worth surfacing loudly).
    /// </summary>
    public ResolvedRef this[string fieldPath] =>
        _byPath.TryGetValue(fieldPath, out var resolved)
            ? resolved
            : throw new KeyNotFoundException(
                $"Encoder requested resolution for {fieldPath} but the walker did not produce one.");

    /// <summary>Non-throwing variant. Returns true when present.</summary>
    public bool TryGet(string fieldPath, out ResolvedRef resolved) =>
        _byPath.TryGetValue(fieldPath, out resolved!);

    /// <summary>
    ///     Convenience: returns the final FormID when <see cref="ResolvedRefAction.Resolved" />,
    ///     otherwise null. Encoders use this for "emit this FormID OR drop the subrecord" sites.
    /// </summary>
    public uint? GetResolvedOrNull(string fieldPath) =>
        _byPath.TryGetValue(fieldPath, out var resolved)
            && resolved.Action == ResolvedRefAction.Resolved
            ? resolved.FinalFormId
            : null;

    /// <summary>
    ///     True when any reference with FieldPath starting with <paramref name="signature" />
    ///     has <see cref="ResolvedRefAction.DropSubrecord" />. Lets a structural-subrecord
    ///     encoder short-circuit when even one operand dangles.
    /// </summary>
    public bool ShouldDropSubrecord(string signature)
    {
        foreach (var resolved in _byPath.Values)
        {
            if (resolved.Action != ResolvedRefAction.DropSubrecord)
            {
                continue;
            }

            if (resolved.FieldPath.StartsWith(signature, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
