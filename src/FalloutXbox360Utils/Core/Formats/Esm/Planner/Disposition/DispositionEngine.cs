using FalloutXbox360Utils.Core.Formats.Esm.Planner.Catalog;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.Disposition.Policies;

namespace FalloutXbox360Utils.Core.Formats.Esm.Planner.Disposition;

/// <summary>
///     Phase B — walks the catalog and produces a disposition (+ provenance) per entry.
///     Dispatches first to any type-specific policy registered for the entry's record type,
///     then falls through to the type-agnostic chain (runtime-state skip, then default).
/// </summary>
/// <remarks>
///     Policy chain order matters: type-specific policies fire first so they can override
///     the default (e.g. <c>ScriptDispositionPolicy</c> chooses Override vs KeepMaster based
///     on whether the DMP-captured script body differs from master). The default policy never
///     returns null, so the chain always terminates with a decision.
/// </remarks>
public sealed class DispositionEngine
{
    private readonly Dictionary<string, List<IDispositionPolicy>> _byType =
        new(StringComparer.Ordinal);
    private readonly List<IDispositionPolicy> _fallback = [];

    public DispositionEngine(IEnumerable<IDispositionPolicy> policies)
    {
        if (policies is null)
        {
            throw new ArgumentNullException(nameof(policies));
        }

        foreach (var policy in policies)
        {
            if (policy.RecordTypes.Count == 0)
            {
                _fallback.Add(policy);
                continue;
            }

            foreach (var type in policy.RecordTypes)
            {
                if (!_byType.TryGetValue(type, out var list))
                {
                    list = [];
                    _byType[type] = list;
                }

                list.Add(policy);
            }
        }

        if (!_fallback.OfType<DefaultDispositionPolicy>().Any())
        {
            throw new InvalidOperationException(
                "DispositionEngine requires a DefaultDispositionPolicy in the policy chain.");
        }
    }

    /// <summary>
    ///     Decide every entry's disposition. Output indices match input indices; consumers
    ///     can zip the two without re-keying.
    /// </summary>
    public IReadOnlyList<(CatalogEntry Entry, DispositionDecision Decision)> Decide(
        IReadOnlyList<CatalogEntry> entries)
    {
        var result = new List<(CatalogEntry, DispositionDecision)>(entries.Count);

        foreach (var entry in entries)
        {
            var decision = DecideOne(entry)
                ?? throw new InvalidOperationException(
                    $"No policy returned a decision for {entry.Type} 0x{entry.MasterFormId ?? entry.DmpFormId ?? 0:X8}.");
            result.Add((entry, decision));
        }

        return result;
    }

    private DispositionDecision? DecideOne(CatalogEntry entry)
    {
        if (_byType.TryGetValue(entry.Type, out var typed))
        {
            foreach (var policy in typed)
            {
                var decision = policy.Decide(entry);
                if (decision is not null)
                {
                    return decision;
                }
            }
        }

        foreach (var policy in _fallback)
        {
            var decision = policy.Decide(entry);
            if (decision is not null)
            {
                return decision;
            }
        }

        return null;
    }
}
