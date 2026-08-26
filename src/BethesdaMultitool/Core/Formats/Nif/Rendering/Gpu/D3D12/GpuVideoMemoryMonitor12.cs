using System.Diagnostics;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Profiling;
using BethesdaMultitool.Core.Resources;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     The renderer's per-frame VRAM feedback loop: polls both memory segments, folds each into a
///     <see cref="VramBudgetSignal" />, consumes OS budget-change notifications, and keeps this
///     process's memory reservation honest.
///     <para>
///         Until this existed the viewer had <b>no runtime VRAM feedback at all</b>. The adapter was
///         queried twice in the whole program — once to pick a shadow-map resolution at construction
///         and once to format a log line — and nothing anywhere reacted to the answer. Every byte
///         budget downstream of it was therefore planned once and never revisited.
///     </para>
///     <para>
///         <b>Reads only; decides nothing.</b> This publishes a zone and never evicts, trims, or
///         resizes anything — the governor that acts on it is a separate, later piece. The split is
///         deliberate: a signal that also acted could not be observed in isolation, and observing it
///         in isolation is the only way to know the thresholds are right before anything depends on
///         them.
///     </para>
///     <para>
///         Render-thread only. <see cref="Tick" /> must run after the frame's fence wait and deletion
///         queue, or it reads usage that includes resources already proven dead.
///     </para>
/// </summary>
internal sealed class GpuVideoMemoryMonitor12 : IDisposable
{
    /// <summary>
    ///     Smallest change in the desired reservation worth a DXGI round-trip. Streaming moves
    ///     residency by a few MB constantly; re-reserving on every one of those would be a syscall
    ///     per frame to tell the OS something it already believes.
    /// </summary>
    public const long ReservationUpdateThresholdBytes = 64L * 1024L * 1024L;

    /// <summary>
    ///     Frames between reservation refreshes, absent a budget-change notification. A notification
    ///     forces one immediately — that is exactly the moment the OS is deciding whose memory to
    ///     take.
    /// </summary>
    public const int ReservationRefreshFrames = 240;

    private static readonly Logger Log = Logger.Instance;

    private readonly GpuDevice12 _gpu;
    private readonly GpuVideoMemoryBudgetWatcher? _watcher;
    private long _reservedBytes = -1;
    private int _framesSinceReservation;
    private bool _reservationUnavailableLogged;
    private bool _disposed;

    public GpuVideoMemoryMonitor12(GpuDevice12 gpu)
    {
        _gpu = gpu;
        _gpu.TryCreateBudgetChangeWatcher(out _watcher);
        Supported = _gpu.TryQueryVideoMemory(GpuMemorySegment.Local, out _);
    }

    /// <summary>
    ///     Whether the adapter answered a video-memory query at construction. False on WARP, on
    ///     adapters without <c>IDXGIAdapter3</c>, and in headless test contexts — in which case both
    ///     signals stay <see cref="VramPressureZone.Inert" /> forever and every consumer must do
    ///     nothing rather than assume the worst.
    /// </summary>
    public bool Supported { get; }

    /// <summary>Whether OS budget-change notifications are available. Polling continues regardless.</summary>
    public bool NotificationsAvailable => _watcher is not null;

    /// <summary>Device-local VRAM pressure — the one that ends in a device removal when ignored.</summary>
    public VramBudgetSignal Local { get; } = new();

    /// <summary>
    ///     Host-visible (UPLOAD/READBACK heap) pressure. Tracked separately because the geometry
    ///     arena lives here, so shedding reference geometry relieves this segment and not VRAM —
    ///     a governor steering both from one number would evict the wrong pool.
    /// </summary>
    public VramBudgetSignal NonLocal { get; } = new();

    /// <summary>Budget-change notifications consumed since startup.</summary>
    public long BudgetChangeCount { get; private set; }

    /// <summary>Wall-clock cost of the most recent <see cref="Tick" />'s DXGI queries, in microseconds.</summary>
    public double LastPollMicroseconds { get; private set; }

    /// <summary>Bytes currently reserved with the OS on the local segment, or -1 if none was set.</summary>
    public long ReservedBytes => _reservedBytes;

    /// <summary>
    ///     Polls both segments and advances both signals. Cheap enough for every frame — the two
    ///     DXGI calls are a user-mode read of a shared structure, not a kernel transition — and
    ///     per-frame is the correct rate because the peak-hold exists to catch a ONE-frame burst,
    ///     which any coarser sampling would step over.
    /// </summary>
    public void Tick()
    {
        if (_disposed)
        {
            return;
        }

        var started = Stopwatch.GetTimestamp();
        var budgetChanged = _watcher?.TryConsumeBudgetChanged() ?? false;
        if (budgetChanged)
        {
            BudgetChangeCount++;
        }

        var localOk = _gpu.TryQueryVideoMemory(GpuMemorySegment.Local, out var local);
        var nonLocalOk = _gpu.TryQueryVideoMemory(GpuMemorySegment.NonLocal, out var nonLocal);
        LastPollMicroseconds = Stopwatch.GetElapsedTime(started).TotalMicroseconds;

        // A failed query and an unusable reading both mean "no signal"; Update treats a default
        // reading as exactly that, so the two collapse to one path here.
        var previousZone = Local.Zone;
        Local.Update(localOk ? local : GpuVideoMemoryInfo.Unavailable);
        NonLocal.Update(nonLocalOk ? nonLocal : GpuVideoMemoryInfo.Unavailable);

        if (Local.Zone != previousZone)
        {
            Log.Info(
                "GpuVideoMemoryMonitor12: VRAM pressure {0} -> {1} ({2:P1} of {3} MB budget, peak {4} MB).",
                previousZone, Local.Zone, Local.UsageFraction,
                Local.LatchedBudgetBytes / (1024 * 1024), Local.PeakUsageBytes / (1024 * 1024));
            RendererProfilerTrace.Event("vram-zone", new Dictionary<string, object?>
            {
                ["from"] = previousZone.ToString(),
                ["to"] = Local.Zone.ToString(),
                ["usageFraction"] = Local.UsageFraction,
                ["budgetMb"] = Local.LatchedBudgetBytes / (1024 * 1024),
                ["peakUsedMb"] = Local.PeakUsageBytes / (1024 * 1024),
                ["nonLocalZone"] = NonLocal.Zone.ToString()
            });
        }

        UpdateReservation(local, localOk, budgetChanged);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _watcher?.Dispose();
    }

    /// <summary>
    ///     Keeps the OS's picture of our minimum need current.
    ///     <para>
    ///         The reservation is deliberately the memory we are <b>already using</b>, never a
    ///         speculative claim on more. That is the honest number, it is what DXGI's contract asks
    ///         for, and the OS clamps it to <c>AvailableForReservation</c> regardless — so the effect
    ///         is confined to who gets trimmed first when budgets are cut, which is precisely the
    ///         intent. Claiming more than we hold would take budget from the desktop to keep memory
    ///         we are not using.
    ///     </para>
    /// </summary>
    private void UpdateReservation(GpuVideoMemoryInfo local, bool localOk, bool budgetChanged)
    {
        _framesSinceReservation++;
        if (!localOk || !local.IsUsable)
        {
            return;
        }

        var due = budgetChanged || _framesSinceReservation >= ReservationRefreshFrames;
        var desired = Math.Min(local.CurrentUsageBytes, Math.Max(0, local.AvailableForReservationBytes));
        if (!due && _reservedBytes >= 0 && Math.Abs(desired - _reservedBytes) < ReservationUpdateThresholdBytes)
        {
            return;
        }

        _framesSinceReservation = 0;
        if (_gpu.TrySetVideoMemoryReservation(GpuMemorySegment.Local, desired))
        {
            _reservedBytes = desired;
            return;
        }

        if (!_reservationUnavailableLogged)
        {
            _reservationUnavailableLogged = true;
            Log.Debug("GpuVideoMemoryMonitor12: video-memory reservation unavailable on this adapter.");
        }
    }
}
