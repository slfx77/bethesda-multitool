using System.Globalization;
using System.Threading;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core;

namespace BethesdaMultitool;

/// <summary>
///     Env-var-gated cache + viewport telemetry for the 2D world map. When
///     <c>FALLOUT_MAP2D_TRACE=1</c> is set in the process environment, every cache
///     mutation, viewport rebuild, version/cache-gen bump, and per-stream lifecycle
///     event in <see cref="WorldMapControl" /> writes a structured single-line event
///     to <see cref="Logger.Instance" />. The <c>BethesdaMap2DProfiler</c>
///     companion app sets this flag before launching the WinUI host so its log
///     captures a deterministic trace of the disappearing-cells repro.
/// </summary>
internal static class Map2DProfilerTrace
{
    private static readonly bool s_enabled =
        EnvironmentVariables.IsEnabled(EnvironmentVariables.Map2D.Trace);

    private static int s_frameCounter;

    public static bool IsEnabled => s_enabled;

    public static int Frame => Volatile.Read(ref s_frameCounter);

    public static int IncrementFrame() => Interlocked.Increment(ref s_frameCounter);

    /// <summary>Writes a single trace event with formatted detail to the log when tracing is enabled.</summary>
    public static void Event(string evt, FormattableString detail)
    {
        if (!s_enabled) return;
        Logger.Instance.Info(
            "[Map2D] f={0} {1} {2}",
            Frame.ToString(CultureInfo.InvariantCulture),
            evt,
            detail.ToString(CultureInfo.InvariantCulture));
    }

    public static void Event(string evt, string detail)
    {
        if (!s_enabled) return;
        Logger.Instance.Info(
            "[Map2D] f={0} {1} {2}",
            Frame.ToString(CultureInfo.InvariantCulture),
            evt,
            detail);
    }

    public static void Event(string evt)
    {
        if (!s_enabled) return;
        Logger.Instance.Info(
            "[Map2D] f={0} {1}",
            Frame.ToString(CultureInfo.InvariantCulture),
            evt);
    }
}
