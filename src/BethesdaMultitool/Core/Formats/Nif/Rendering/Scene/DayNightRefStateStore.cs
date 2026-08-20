using BethesdaMultitool.Core.WorldData.DayNight;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Scene;

/// <summary>
///     Current-hour snapshot of every scripted day/night reference: FormID → effective disabled.
///     Re-evaluated only when the game hour crosses a schedule slot; <see cref="Version" /> joins
///     <see cref="ReferenceVisibilityKey" /> so cull caches, retained placed-water content, and
///     stationary shadow maps refresh exactly when a light network flips. Explicit per-instance
///     On/Off previews still win — this store only replaces the AUTHORED initial state for gated
///     refs inside <see cref="ReferenceEnabledStatePolicy" /> evaluation.
/// </summary>
internal sealed class DayNightRefStateStore
{
    private readonly Dictionary<uint, bool> _disabledNow = [];
    private int _lastSlot = -1;
    private DayNightRefSchedule? _schedule;

    internal int Version { get; private set; }
    internal int Count => _disabledNow.Count;

    /// <summary>Re-evaluates gated states for the given hour; cheap no-op within a slot.</summary>
    internal void Apply(DayNightRefSchedule? schedule, float hour)
    {
        var slot = schedule is null ? -1 : HourSchedule.SlotOf(hour);
        if (ReferenceEquals(schedule, _schedule) && slot == _lastSlot) return;

        _schedule = schedule;
        _lastSlot = slot;

        if (schedule is null)
        {
            if (_disabledNow.Count == 0) return;
            _disabledNow.Clear();
            Version++;
            return;
        }

        var changed = false;
        foreach (var (formId, _) in schedule.GatedRefs)
        {
            if (!schedule.TryGetDisabledAt(formId, hour, out var disabled)) continue;
            if (_disabledNow.TryGetValue(formId, out var current) && current == disabled) continue;
            _disabledNow[formId] = disabled;
            changed = true;
        }

        if (changed) Version++;
    }

    /// <summary>Hour-driven disabled state; false = not gated (use the authored state).</summary>
    internal bool TryGetDisabled(uint formId, out bool disabled)
    {
        return _disabledNow.TryGetValue(formId, out disabled);
    }

    /// <summary>Authored-vs-scheduled merge used by every visibility consumer.</summary>
    internal bool EffectiveDisabled(uint formId, bool authoredDisabled)
    {
        return _disabledNow.TryGetValue(formId, out var disabled) ? disabled : authoredDisabled;
    }
}
