using System.Globalization;

namespace BethesdaMultitool.Core.Diagnostics;

/// <summary>
///     Optional safety valve over the <see cref="ResourceRegistry" />.
///     <b>
///         Trimming is OFF by
///         default
///     </b>
///     : the app may use as much RAM as it needs. The registry, the diagnostics panel,
///     and the CLI stats are the point of centralization — a shared usage pattern plus visibility
///     for hunting leaks — not a RAM limiter. This coordinator only sheds caches when a caller
///     opts in by setting a positive <c>FALLOUT_MEMORY_BUDGET_MB</c>.
///     <para>
///         When a budget IS set, it watches the total bytes registered under
///         <see cref="ResourceCategory.CpuCache" /> and asks <see cref="IMemoryPressureParticipant" />s
///         to trim, priority-ordered, when that budget is exceeded. The budget applies to CPU caches
///         only — mapped files are file-backed, disk caches enforce their own caps, and GPU residency
///         is governed on the render thread instead (this coordinator runs on a timer thread, and the
///         GPU LRUs are single-threaded by contract). The CpuCache budget is the sole
///         trigger; the system GC memory-load ratio only escalates an over-budget pass from
///         <see cref="TrimLevel.Gentle" /> to <see cref="TrimLevel.Aggressive" /> (it is machine-wide
///         and chronically &gt;0.9 on a workstation, so triggering on it alone would perpetually wipe
///         rebuildable rendering caches for ~0 MB released).
///     </para>
///     <para>
///         The periodic timer (default 5 s, <c>FALLOUT_MEMORY_CHECK_INTERVAL_S</c>) and explicit
///         <see cref="CheckNow" /> calls run a pass; with no budget set, a pass is a cheap no-op
///         (plus an optional registry snapshot to the log under <c>FALLOUT_MEMORY_LOG</c>).
///     </para>
/// </summary>
internal sealed class MemoryBudgetCoordinator : IDisposable
{
    private const double GcPressureRatio = 0.9;
    private const double BudgetTargetFraction = 0.9;

    private static MemoryBudgetCoordinator? _instance;
    private static readonly Lock InstanceLock = new();
    private readonly long _budgetBytes;
    private readonly bool _disabled;
    private readonly Func<double> _memoryLoadRatio;

    private readonly ResourceRegistry _registry;
    private int _passRunning;
    private Timer? _timer;

    internal MemoryBudgetCoordinator(
        ResourceRegistry registry,
        long budgetBytes,
        Func<double>? memoryLoadRatio = null,
        bool disabled = false)
    {
        _registry = registry;
        _budgetBytes = Math.Max(1, budgetBytes);
        _memoryLoadRatio = memoryLoadRatio ?? ReadGcMemoryLoadRatio;
        _disabled = disabled;
    }

    /// <summary>Process-wide coordinator over <see cref="ResourceRegistry.Instance" />, configured from environment.</summary>
    public static MemoryBudgetCoordinator Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (InstanceLock)
                {
                    _instance ??= new MemoryBudgetCoordinator(
                        ResourceRegistry.Instance,
                        ResolveBudgetBytes(),
                        null,
                        EnvironmentVariables.IsEnabled(EnvironmentVariables.Memory.Disable));
                }
            }

            return _instance;
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>Starts the periodic check timer. Idempotent; a no-op when disabled.</summary>
    public void Start()
    {
        if (_disabled || _timer != null)
        {
            return;
        }

        var interval = TimeSpan.FromSeconds(EnvironmentVariables.GetClampedInt(
            EnvironmentVariables.Memory.CheckIntervalSeconds, 5, 1, 3600));
        _timer = new Timer(static state => ((MemoryBudgetCoordinator)state!).OnTimerTick(), this, interval, interval);
    }

    /// <summary>Runs a budget pass immediately (e.g. right after a large world/session load).</summary>
    public void CheckNow(string reason)
    {
        if (!_disabled)
        {
            RunPass(reason);
        }
    }

    private void OnTimerTick()
    {
        if (EnvironmentVariables.IsEnabled(EnvironmentVariables.Memory.Log))
        {
            _registry.LogSnapshot();
        }

        RunPass("timer");
    }

    private void RunPass(string reason)
    {
        // A slow pass must never stack on itself.
        if (Interlocked.Exchange(ref _passRunning, 1) != 0)
        {
            return;
        }

        try
        {
            var cpuBytes = _registry.TotalTrackedBytes(ResourceCategory.CpuCache);
            var overBudget = cpuBytes > _budgetBytes;

            // The CpuCache budget is the ONLY trigger. System-wide GC memory load is NOT a trigger:
            // GC.GetGCMemoryInfo().MemoryLoadBytes is a machine-wide figure that sits chronically
            // above 0.9 on a workstation (other processes + a multi-GB dataset held in mapped files
            // and GPU memory, neither of which a CpuCache trim can release). Triggering on it ran an
            // Aggressive pass every tick that perpetually wiped the rebuildable rendering caches
            // (landscape palette + texture resolvers feeding the 2D/3D map) for ~0 MB released —
            // terrain and statics could never stay resident in the GUI (the profiler, which never
            // starts the coordinator, was unaffected). The GC ratio still escalates the LEVEL once
            // we are genuinely over the CpuCache budget — that is its only sound role here.
            if (!overBudget)
            {
                return;
            }

            var gcRatio = _memoryLoadRatio();
            var level = gcRatio > GcPressureRatio ? TrimLevel.Aggressive : TrimLevel.Gentle;
            var aggressive = level == TrimLevel.Aggressive;
            var target = (long)(_budgetBytes * BudgetTargetFraction);

            var participants = new List<(IMemoryPressureParticipant Participant, ResourceRegistration Registration)>();
            foreach (var registration in _registry.GetRegistrations())
            {
                if (registration.Resource is IMemoryPressureParticipant participant)
                {
                    participants.Add((participant, registration));
                }
            }

            participants.Sort(static (a, b) => a.Participant.TrimPriority.CompareTo(b.Participant.TrimPriority));

            long released = 0;
            var posted = 0;
            foreach (var (participant, registration) in participants)
            {
                // Over budget AND under real GC pressure: shed every participant hard. Over budget
                // but the machine is fine: stop once the projected total is comfortably below target.
                if (!aggressive && cpuBytes - released <= target)
                {
                    break;
                }

                if (participant.TrimAffinity == TrimAffinity.AnyThread)
                {
                    try
                    {
                        released += participant.Trim(level);
                    }
                    catch (Exception ex)
                    {
                        Logger.Instance.Warn(
                            "MemoryBudgetCoordinator: {0}.Trim threw: {1}", participant.ResourceName, ex.Message);
                    }
                }
                else
                {
                    registration.PostTrim(level);
                    posted++;
                }
            }

            Logger.Instance.Info(
                "MemoryBudgetCoordinator: {0} pass ({1}) — cpu caches {2:N0} MB (budget {3:N0} MB, gc load {4:P0}); released {5:N0} MB, posted {6} owner-thread trims.",
                level, reason, cpuBytes / (1024 * 1024), _budgetBytes / (1024 * 1024), gcRatio,
                released / (1024 * 1024), posted);
        }
        finally
        {
            Volatile.Write(ref _passRunning, 0);
        }
    }

    private static double ReadGcMemoryLoadRatio()
    {
        var info = GC.GetGCMemoryInfo();
        return info.HighMemoryLoadThresholdBytes <= 0
            ? 0
            : info.MemoryLoadBytes / (double)info.HighMemoryLoadThresholdBytes;
    }

    private static long ResolveBudgetBytes()
    {
        // No cap by default. Centralizing caches is about a shared usage pattern + leak visibility
        // (the registry, the diagnostics panel, the CLI stats), NOT about limiting RAM — the app is
        // expected to use as much as it needs. Trimming activates ONLY when a positive
        // FALLOUT_MEMORY_BUDGET_MB is set explicitly; otherwise the coordinator just tracks (and
        // optionally logs snapshots under FALLOUT_MEMORY_LOG for leak hunting) and never evicts.
        var raw = EnvironmentVariables.Get(EnvironmentVariables.Memory.BudgetMegabytes);
        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed * 1024L * 1024L
            : long.MaxValue;
    }
}
