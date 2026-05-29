using System.Collections.Immutable;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.Catalog;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.Disposition;

namespace FalloutXbox360Utils.Core.Formats.Esm.Planner.References;

/// <summary>
///     Phase D — walks every <see cref="RecordDisposition.Override" /> /
///     <see cref="RecordDisposition.New" /> record's outgoing FormID references and
///     decides per-reference: resolve to the emitted FormID, null out, drop the subrecord,
///     or downgrade the container.
/// </summary>
public sealed class ReferenceResolver
{
    private readonly Dictionary<string, IRecordReferenceWalker> _walkers =
        new(StringComparer.Ordinal);
    private readonly DegradationPolicy _policy;

    public ReferenceResolver(
        IEnumerable<IRecordReferenceWalker> walkers,
        DegradationPolicy policy)
    {
        if (walkers is null)
        {
            throw new ArgumentNullException(nameof(walkers));
        }

        _policy = policy ?? throw new ArgumentNullException(nameof(policy));

        foreach (var walker in walkers)
        {
            _walkers[walker.RecordType] = walker;
        }
    }

    /// <summary>
    ///     Resolve every reference on every Override / New record in the input.
    /// </summary>
    /// <param name="decisions">Output of phase B (Disposition).</param>
    /// <param name="emittedFormIds">
    ///     Master FormIDs ∪ plugin-range FormIDs allocated by phase C. The single source
    ///     of truth for "is this reference live?".
    /// </param>
    /// <param name="sourceToEmitted">
    ///     Phase C's allocation map. References whose source value is in this map get
    ///     translated to the allocated value before validity check.
    /// </param>
    public ImmutableDictionary<int, ImmutableArray<ResolvedRef>> ResolveAll(
        IReadOnlyList<(CatalogEntry Entry, DispositionDecision Decision)> decisions,
        IReadOnlySet<uint> emittedFormIds,
        IReadOnlyDictionary<uint, uint> sourceToEmitted)
    {
        var builder = ImmutableDictionary.CreateBuilder<int, ImmutableArray<ResolvedRef>>();

        for (var i = 0; i < decisions.Count; i++)
        {
            var (entry, decision) = decisions[i];
            if (decision.Disposition is RecordDisposition.KeepMaster or RecordDisposition.Skip)
            {
                builder[i] = ImmutableArray<ResolvedRef>.Empty;
                continue;
            }

            if (entry.Model is null
                || !_walkers.TryGetValue(entry.Type, out var walker))
            {
                builder[i] = ImmutableArray<ResolvedRef>.Empty;
                continue;
            }

            builder[i] = ResolveOne(walker, entry.Type, entry.Model, emittedFormIds, sourceToEmitted);
        }

        return builder.ToImmutable();
    }

    private ImmutableArray<ResolvedRef> ResolveOne(
        IRecordReferenceWalker walker,
        string recordType,
        object model,
        IReadOnlySet<uint> emittedFormIds,
        IReadOnlyDictionary<uint, uint> sourceToEmitted)
    {
        var refs = ImmutableArray.CreateBuilder<ResolvedRef>();

        foreach (var raw in walker.Walk(model))
        {
            refs.Add(ResolveOneReference(recordType, raw, emittedFormIds, sourceToEmitted));
        }

        return refs.ToImmutable();
    }

    private ResolvedRef ResolveOneReference(
        string recordType,
        RawReference raw,
        IReadOnlySet<uint> emittedFormIds,
        IReadOnlyDictionary<uint, uint> sourceToEmitted)
    {
        if (raw.FormId is null or 0u)
        {
            return new ResolvedRef
            {
                FieldPath = raw.FieldPath,
                OriginalFormId = raw.FormId,
                Action = ResolvedRefAction.Resolved,
                FinalFormId = raw.FormId,
            };
        }

        var source = raw.FormId.Value;
        var target = sourceToEmitted.TryGetValue(source, out var aliased) ? aliased : source;

        if (emittedFormIds.Contains(target))
        {
            return new ResolvedRef
            {
                FieldPath = raw.FieldPath,
                OriginalFormId = source,
                Action = ResolvedRefAction.Resolved,
                FinalFormId = target,
            };
        }

        var action = _policy.Lookup(recordType, raw.FieldPath);
        return action.Kind switch
        {
            DanglingActionKind.DropSubrecord => new ResolvedRef
            {
                FieldPath = raw.FieldPath,
                OriginalFormId = source,
                Action = ResolvedRefAction.DropSubrecord,
                Reason = $"Target 0x{target:X8} not in emit set; per-policy drop.",
            },
            DanglingActionKind.NullRef => new ResolvedRef
            {
                FieldPath = raw.FieldPath,
                OriginalFormId = source,
                Action = ResolvedRefAction.NullRef,
                FinalFormId = 0u,
                Reason = $"Target 0x{target:X8} not in emit set; per-policy null-out.",
            },
            DanglingActionKind.DowngradeContainer => new ResolvedRef
            {
                FieldPath = raw.FieldPath,
                OriginalFormId = source,
                Action = ResolvedRefAction.DowngradeContainer,
                Downgrade = action.Downgrade,
                Reason = $"Target 0x{target:X8} not in emit set; downgrading container.",
            },
            _ => throw new InvalidOperationException($"Unknown DanglingActionKind: {action.Kind}"),
        };
    }
}
