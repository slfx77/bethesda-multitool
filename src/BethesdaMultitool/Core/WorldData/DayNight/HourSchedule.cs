namespace BethesdaMultitool.Core.WorldData.DayNight;

/// <summary>
///     A 24-hour on/off cycle for one script target, sampled at 3-minute resolution. Built from the
///     three-valued reachability of the target's Enable/Disable statements: at each slot the
///     shallowest reachable statement wins (a top-level "Enable at night" beats the deeply nested
///     flicker-show Disable calls inside it), undefined stretches carry the previous state around
///     the clock, and a target whose cycle never contains both states — or that ever ties — yields
///     no schedule at all, so broken retail scripts (Disable-only bonfires) and non-hour scene
///     scripts stay on their authored initial state.
/// </summary>
internal sealed class HourSchedule
{
    internal const int SlotsPerDay = 480; // 0.05h = 3 minutes
    private const float HoursPerSlot = 24f / SlotsPerDay;

    private readonly bool[] _enabled;

    private HourSchedule(bool[] enabled)
    {
        _enabled = enabled;
    }

    internal bool IsEnabledAt(float hour)
    {
        var wrapped = hour % 24f;
        if (wrapped < 0f) wrapped += 24f;
        var slot = (int)(wrapped / HoursPerSlot);
        return _enabled[Math.Clamp(slot, 0, SlotsPerDay - 1)];
    }

    internal static int SlotOf(float hour)
    {
        var wrapped = hour % 24f;
        if (wrapped < 0f) wrapped += 24f;
        return Math.Clamp((int)(wrapped / HoursPerSlot), 0, SlotsPerDay - 1);
    }

    /// <summary>
    ///     Builds the cycle from every action addressing one target. Returns null when the actions
    ///     do not describe an unambiguous day/night cycle (see class remarks). At least one guard
    ///     must contain a real hour comparison — scripts whose Enable/Disable is purely
    ///     variable-driven never qualify.
    /// </summary>
    internal static HourSchedule? Build(IReadOnlyList<HourScheduleAction> actions)
    {
        if (actions.Count == 0) return null;
        if (!actions.Any(action => action.Guard.ContainsHourComparison)) return null;

        var states = new bool?[SlotsPerDay];
        for (var slot = 0; slot < SlotsPerDay; slot++)
        {
            // Evaluate at the slot center so authored boundaries (20.00, 23.20, …) never land on
            // an exact comparison value and flip on strict-vs-inclusive operator differences.
            var hour = (slot + 0.5f) * HoursPerSlot;
            var enableDepth = int.MaxValue;
            var disableDepth = int.MaxValue;
            foreach (var action in actions)
            {
                if (action.Guard.Evaluate(hour) == HourTruth.False) continue;
                if (action.IsEnable)
                {
                    enableDepth = Math.Min(enableDepth, action.Depth);
                }
                else
                {
                    disableDepth = Math.Min(disableDepth, action.Depth);
                }
            }

            if (enableDepth == int.MaxValue && disableDepth == int.MaxValue)
            {
                continue; // undefined — carry-filled below
            }

            if (enableDepth == disableDepth)
            {
                return null; // ambiguous steady state — refuse to guess
            }

            states[slot] = enableDepth < disableDepth;
        }

        // Carry-fill undefined stretches from the previous defined slot, wrapping around midnight.
        var firstDefined = Array.FindIndex(states, state => state.HasValue);
        if (firstDefined < 0) return null;
        var carry = states[firstDefined]!.Value;
        // Walk one full circle starting after the first defined slot so the carry entering slot 0
        // comes from the end of the previous day.
        for (var step = 1; step <= SlotsPerDay; step++)
        {
            var slot = (firstDefined + step) % SlotsPerDay;
            if (states[slot].HasValue)
            {
                carry = states[slot]!.Value;
            }
            else
            {
                states[slot] = carry;
            }
        }

        var resolved = new bool[SlotsPerDay];
        var hasOn = false;
        var hasOff = false;
        for (var slot = 0; slot < SlotsPerDay; slot++)
        {
            resolved[slot] = states[slot]!.Value;
            hasOn |= resolved[slot];
            hasOff |= !resolved[slot];
        }

        return hasOn && hasOff ? new HourSchedule(resolved) : null;
    }
}
