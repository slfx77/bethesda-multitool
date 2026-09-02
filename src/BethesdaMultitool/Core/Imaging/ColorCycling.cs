namespace BethesdaMultitool.Core.Imaging;

/// <summary>
///     One animated palette-index range: indices <see cref="Start" />..<see cref="End" />
///     inclusive rotate as a group. <see cref="TicksPerStep" /> is the engine's frame duration
///     for the range in milliseconds (one tick = 1 ms); callers derive their own step count
///     from it — this type holds data only.
/// </summary>
internal readonly record struct ColorCycleRange(string Name, int Start, int End, int TicksPerStep);

/// <summary>
///     The classic Fallout PAL animated index ranges (per fodev.net/files/fo2/pal.html):
///     the last 27 palette entries before the interface colours cycle in six fixed groups
///     (slime, monitors, slow/fast fire, shoreline, alarm). Pure data plus a pure rotation
///     helper — no timers; animation cadence is the caller's job.
/// </summary>
internal static class ColorCycling
{
    /// <summary>Green slime pools, indices 229..232, 200 ms per step.</summary>
    public static readonly ColorCycleRange Slime = new("Slime", 229, 232, 200);

    /// <summary>Computer monitor glow, indices 233..237, 100 ms per step.</summary>
    public static readonly ColorCycleRange Monitors = new("Monitors", 233, 237, 100);

    /// <summary>Slow fire, indices 238..242, 200 ms per step.</summary>
    public static readonly ColorCycleRange FireSlow = new("FireSlow", 238, 242, 200);

    /// <summary>Fast fire, indices 243..247, 142 ms per step.</summary>
    public static readonly ColorCycleRange FireFast = new("FireFast", 243, 247, 142);

    /// <summary>Shoreline water, indices 248..253, 200 ms per step.</summary>
    public static readonly ColorCycleRange Shoreline = new("Shoreline", 248, 253, 200);

    /// <summary>
    ///     Alarm light, the single index 254, 33 ms per step. The engine pulses this entry's red
    ///     intensity rather than rotating indices, so <see cref="CycleIndex" /> leaves it fixed.
    /// </summary>
    public static readonly ColorCycleRange Alarm = new("Alarm", 254, 254, 33);

    /// <summary>All Fallout cycling ranges, in palette-index order.</summary>
    public static readonly IReadOnlyList<ColorCycleRange> FalloutRanges =
    [
        Slime, Monitors, FireSlow, FireFast, Shoreline, Alarm
    ];

    /// <summary>
    ///     Rotates <paramref name="index" /> forward within its cycling range by
    ///     <paramref name="tick" /> steps, wrapping from the range end back to its start.
    ///     An index outside every range (and any single-entry range) returns unchanged;
    ///     negative ticks rotate backward.
    /// </summary>
    public static int CycleIndex(int index, int tick)
    {
        foreach (var range in FalloutRanges)
        {
            if (index < range.Start || index > range.End)
            {
                continue;
            }

            var length = range.End - range.Start + 1;
            var offset = (index - range.Start + tick) % length;
            if (offset < 0)
            {
                offset += length;
            }

            return range.Start + offset;
        }

        return index;
    }
}
