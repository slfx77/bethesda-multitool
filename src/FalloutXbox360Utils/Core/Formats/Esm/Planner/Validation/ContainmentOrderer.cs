using System.Collections.Immutable;

namespace FalloutXbox360Utils.Core.Formats.Esm.Planner.Validation;

/// <summary>
///     Topologically orders the planned record list so parents precede children: DIAL before
///     INFO, WRLD before CELL, CELL before placed REFRs, NAVM before NAVI references. Within
///     the ready-set we prefer insertion order — this matches legacy <c>BuildGrupForType</c>'s
///     <c>foreach (var model in models)</c> emission so Tier 1 / 2 parity is byte-exact.
/// </summary>
public static class ContainmentOrderer
{
    public static ImmutableArray<RecordPlan> Order(ImmutableArray<RecordPlan> records)
    {
        if (records.Length == 0)
        {
            return records;
        }

        var indexByFormId = new Dictionary<uint, int>(records.Length);
        for (var i = 0; i < records.Length; i++)
        {
            indexByFormId[records[i].FormId] = i;
        }

        var adj = new List<List<int>>(records.Length);
        var indegree = new int[records.Length];
        for (var i = 0; i < records.Length; i++)
        {
            adj.Add([]);
        }

        for (var i = 0; i < records.Length; i++)
        {
            foreach (var edge in records[i].ContainedBy)
            {
                if (!indexByFormId.TryGetValue(edge.ParentFormId, out var parentIndex))
                {
                    continue;
                }

                adj[parentIndex].Add(i);
                indegree[i]++;
            }
        }

        // Insertion order tiebreaker, NOT FormID. Legacy emits records in catalog order
        // (the order RecordCollection lists yield them, see EnumerateModelsByType); switching
        // to FormID order would break byte-exact parity. Type still comes first because
        // separate top-level GRUPs are formatted per type.
        var ready = new SortedSet<int>(Comparer<int>.Create((a, b) =>
        {
            var ta = string.CompareOrdinal(records[a].Type, records[b].Type);
            return ta != 0 ? ta : a.CompareTo(b);
        }));

        for (var i = 0; i < records.Length; i++)
        {
            if (indegree[i] == 0)
            {
                ready.Add(i);
            }
        }

        var ordered = ImmutableArray.CreateBuilder<RecordPlan>(records.Length);
        while (ready.Count > 0)
        {
            var next = ready.Min;
            ready.Remove(next);
            ordered.Add(records[next]);

            foreach (var child in adj[next])
            {
                indegree[child]--;
                if (indegree[child] == 0)
                {
                    ready.Add(child);
                }
            }
        }

        if (ordered.Count != records.Length)
        {
            throw new InvalidOperationException(
                "ContainmentOrderer detected a cycle in record containment edges.");
        }

        return ordered.ToImmutable();
    }
}
