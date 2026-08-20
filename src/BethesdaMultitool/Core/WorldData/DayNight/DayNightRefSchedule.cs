using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;

namespace BethesdaMultitool.Core.WorldData.DayNight;

/// <summary>
///     The scanned day/night state machine for one loaded plugin: which placed references are
///     toggled by hour-of-day scripts, and what state each has at a given game hour. Roots are the
///     references a script Enables/Disables directly (named persistent refs, self-toggling scripted
///     instances, and GetLinkedRef targets); every other reference whose XESP enable-parent chain
///     reaches a root follows it with the chain's opposite-state parity applied.
/// </summary>
internal sealed class DayNightRefSchedule
{
    private readonly Dictionary<uint, (uint Root, bool Invert)> _gatedRefs;
    private readonly Dictionary<uint, HourSchedule> _rootSchedules;

    private DayNightRefSchedule(
        Dictionary<uint, HourSchedule> rootSchedules,
        Dictionary<uint, (uint Root, bool Invert)> gatedRefs)
    {
        _rootSchedules = rootSchedules;
        _gatedRefs = gatedRefs;
    }

    internal int RootCount => _rootSchedules.Count;
    internal int GatedRefCount => _gatedRefs.Count;
    internal IReadOnlyDictionary<uint, (uint Root, bool Invert)> GatedRefs => _gatedRefs;

    /// <summary>
    ///     Hour-driven disabled state for a gated reference; false return = not schedule-driven
    ///     (callers keep the authored initial state).
    /// </summary>
    internal bool TryGetDisabledAt(uint refFormId, float hour, out bool disabled)
    {
        disabled = false;
        if (!_gatedRefs.TryGetValue(refFormId, out var gate) ||
            !_rootSchedules.TryGetValue(gate.Root, out var schedule))
        {
            return false;
        }

        var enabled = schedule.IsEnabledAt(hour) ^ gate.Invert;
        disabled = !enabled;
        return true;
    }

    /// <summary>Convenience wrapper for callers without a prebuilt index (tests, small worlds).</summary>
    internal static DayNightRefSchedule? Build(
        IReadOnlyList<ScriptRecord> scripts,
        IReadOnlyList<CellRecord> cells,
        IReadOnlyList<ActivatorRecord> activators,
        IReadOnlyList<LightRecord> lights)
    {
        return Build(scripts, cells, activators, lights, PlacedRefIndex.Build(cells));
    }

    /// <summary>
    ///     Builds the schedule from parsed scripts plus the world's placements. Pure computation —
    ///     no UI or renderer types — so it is unit-testable with synthetic records. The shared
    ///     <paramref name="placedRefs" /> replaces the private full-population byId dictionary this
    ///     used to build per call (5.1M entries on FO76); the EditorId and BaseFormId lookups stay
    ///     local raw-cell passes because their key spaces (and duplicate handling) differ.
    /// </summary>
    internal static DayNightRefSchedule? Build(
        IReadOnlyList<ScriptRecord> scripts,
        IReadOnlyList<CellRecord> cells,
        IReadOnlyList<ActivatorRecord> activators,
        IReadOnlyList<LightRecord> lights,
        PlacedRefIndex placedRefs)
    {
        if (scripts.Count == 0) return null;

        // Script FormID -> base object FormIDs carrying it via SCRI (self/linked-ref patterns).
        var basesByScript = new Dictionary<uint, List<uint>>();
        foreach (var activator in activators)
        {
            if (activator.Script is { } scriptId and > 0)
            {
                AddMulti(basesByScript, scriptId, activator.FormId);
            }
        }

        foreach (var light in lights)
        {
            if (light.Script is { } scriptId and > 0)
            {
                AddMulti(basesByScript, scriptId, light.FormId);
            }
        }

        // Placement lookups: instance editor IDs (named targets), instances by base (self/linked).
        var refsByEditorId = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        var refsByBase = new Dictionary<uint, List<PlacedReference>>();
        foreach (var cell in cells)
        {
            foreach (var placement in cell.PlacedObjects)
            {
                if (!string.IsNullOrEmpty(placement.EditorId))
                {
                    refsByEditorId.TryAdd(placement.EditorId, placement.FormId);
                }

                if (placement.BaseFormId > 0)
                {
                    AddMulti(refsByBase, placement.BaseFormId, placement);
                }
            }
        }

        var rootSchedules = new Dictionary<uint, HourSchedule>();
        var conflicted = new HashSet<uint>();
        foreach (var script in scripts)
        {
            var text = script.SourceText ?? script.DecompiledText;
            if (string.IsNullOrWhiteSpace(text)) continue;

            var actions = HourScheduleScriptParser.Parse(text);
            if (actions.Count == 0) continue;

            foreach (var group in actions.GroupBy(action => (action.TargetKind, Name: action.TargetName),
                         ValueTupleTargetComparer.Instance))
            {
                var schedule = HourSchedule.Build([.. group]);
                if (schedule is null) continue;

                switch (group.Key.TargetKind)
                {
                    case HourScheduleTargetKind.NamedReference:
                        if (refsByEditorId.TryGetValue(group.Key.Name, out var namedId))
                        {
                            AddRoot(rootSchedules, conflicted, namedId, schedule);
                        }

                        break;

                    case HourScheduleTargetKind.SelfInstance:
                    case HourScheduleTargetKind.LinkedReference:
                        if (!basesByScript.TryGetValue(script.FormId, out var scriptedBases)) break;
                        foreach (var baseId in scriptedBases)
                        {
                            if (!refsByBase.TryGetValue(baseId, out var instances)) continue;
                            foreach (var instance in instances)
                            {
                                var targetId = group.Key.TargetKind == HourScheduleTargetKind.SelfInstance
                                    ? instance.FormId
                                    : instance.LinkedRefFormId ?? 0;
                                if (targetId > 0)
                                {
                                    AddRoot(rootSchedules, conflicted, targetId, schedule);
                                }
                            }
                        }

                        break;
                }
            }
        }

        foreach (var formId in conflicted)
        {
            rootSchedules.Remove(formId);
        }

        if (rootSchedules.Count == 0) return null;

        // Every placed ref whose XESP chain reaches a scheduled root follows that root, with the
        // chain's accumulated opposite-state parity. Roots gate themselves (parity false).
        var gatedRefs = new Dictionary<uint, (uint Root, bool Invert)>();
        foreach (var rootId in rootSchedules.Keys)
        {
            gatedRefs[rootId] = (rootId, false);
        }

        foreach (var cell in cells)
        {
            foreach (var placement in cell.PlacedObjects)
            {
                if (gatedRefs.ContainsKey(placement.FormId) ||
                    placement.EnableParentFormId is not > 0) continue;

                var visited = new HashSet<uint>();
                var invert = false;
                var current = placement;
                while (visited.Add(current.FormId))
                {
                    if (current.EnableParentFormId is not { } parentId || parentId == 0) break;
                    if (((current.EnableParentFlags ?? 0) & 0x01) != 0)
                    {
                        invert = !invert;
                    }

                    if (rootSchedules.ContainsKey(parentId))
                    {
                        gatedRefs[placement.FormId] = (parentId, invert);
                        break;
                    }

                    if (!placedRefs.TryGetRef(parentId, out var parent)) break;
                    current = parent;
                }
            }
        }

        return new DayNightRefSchedule(rootSchedules, gatedRefs);
    }

    private static void AddRoot(
        Dictionary<uint, HourSchedule> rootSchedules,
        HashSet<uint> conflicted,
        uint formId,
        HourSchedule schedule)
    {
        if (rootSchedules.TryGetValue(formId, out var existing))
        {
            // Two scripts disagreeing about one ref is a scan ambiguity — drop the ref entirely
            // rather than pick a winner. Identical re-registration (multi-instance bases sharing
            // one script) is harmless.
            if (!ReferenceEquals(existing, schedule))
            {
                conflicted.Add(formId);
            }

            return;
        }

        rootSchedules[formId] = schedule;
    }

    private static void AddMulti<TKey, TValue>(Dictionary<TKey, List<TValue>> map, TKey key, TValue value)
        where TKey : notnull
    {
        if (!map.TryGetValue(key, out var list))
        {
            map[key] = list = [];
        }

        list.Add(value);
    }

    private sealed class ValueTupleTargetComparer
        : IEqualityComparer<(HourScheduleTargetKind TargetKind, string Name)>
    {
        internal static readonly ValueTupleTargetComparer Instance = new();

        public bool Equals(
            (HourScheduleTargetKind TargetKind, string Name) x,
            (HourScheduleTargetKind TargetKind, string Name) y)
        {
            return x.TargetKind == y.TargetKind &&
                   string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode((HourScheduleTargetKind TargetKind, string Name) obj)
        {
            return HashCode.Combine(obj.TargetKind, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name));
        }
    }
}
