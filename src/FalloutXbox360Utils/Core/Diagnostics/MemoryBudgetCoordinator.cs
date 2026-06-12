using System.Globalization;

namespace FalloutXbox360Utils.Core.Diagnostics;

/// <summary>
///     Watches the total bytes registered under <see cref="ResourceCategory.CpuCache" /> and asks
///     <see cref="IMemoryPressureParticipant" />s to trim, priority-ordered, when the budget is
///     exceeded. Deliberately simple — on a 32 GB machine with an observed ~5 GB peak this is
///     hygiene, not survival.
///     <para>
///         Triggers: a periodic timer (default 5 s, <c>FALLOUT_MEMORY_CHECK_INTERVAL_S</c>) plus
///         explicit <see cref="CheckNow" /> calls on big load paths. The budget
///         (<c>FALLOUT_MEMORY_BUDGET_MB</c>, default 3072) applies to CPU caches only — GPU residency
///         is structurally bounded by refcounts/LRU caps, mapped files are file-backed, and disk
///         caches enforce their own caps. A GC memory-load ratio above 0.9 forces an
///         <see cref="TrimLevel.Aggressive" /> pass over every participant regardless of budget.
///     </para>
/// </summary>
internal sealed class MemoryBudgetCoordinator : IDisposable
{
    private const double GcPressureRatio = 0.9;
    private const double BudgetTargetFraction = 0.9;

    private static MemoryBudgetCoordinator? _instance;
    private static readonly Lock InstanceLock = new();

    private readonly ResourceRegistry _registry;
    private readonly long _budgetBytes;
    private readonly Func<double> _memoryLoadRatio;
    private readonly bool _disabled;
    private Timer? _timer;
    private int _passRunning;

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
                        memoryLoadRatio: null,
                        disabled: EnvironmentVariables.IsEnabled(EnvironmentVariables.Memory.Disable));
                }
            }

            return _instance;
        }
    }

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

    /// <summary>Starts the periodic check timer. Idempotent; a no-op when disabled.</summary>
    public void Start()
    {
        if (_disabled || _timer != null)
        {
            return;
        }

        var interval = TimeSpan.FromSeconds(EnvironmentVariables.GetClampedInt(
            EnvironmentVariables.Memory.CheckIntervalSeconds, defaultValue: 5, min: 1, max: 3600));
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

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
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
            var gcRatio = _memoryLoadRatio();
            var gcPressure = gcRatio > GcPressureRatio;
            var overBudget = cpuBytes > _budgetBytes;
            if (!gcPressure && !overBudget)
            {
                return;
            }

            var level = gcPressure ? TrimLevel.Aggressive : TrimLevel.Gentle;
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
                // Under GC pressure every participant trims; under a plain budget breach stop once
                // the projected total is comfortably below target.
                if (!gcPressure && cpuBytes - released <= target)
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
        const long defaultMb = 3072;
        var raw = EnvironmentVariables.Get(EnvironmentVariables.Memory.BudgetMegabytes);
        var mb = long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : defaultMb;
        return mb * 1024L * 1024L;
    }
}
