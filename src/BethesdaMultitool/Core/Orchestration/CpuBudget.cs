namespace BethesdaMultitool.Core.Orchestration;

/// <summary>The renderer's background CPU consumers, each of which sizes its own worker pool.</summary>
internal enum CpuWorkload
{
    /// <summary>NIF parse + geometry conversion for placed references. BSA read, then heavy CPU.</summary>
    ReferenceMeshDecode,

    /// <summary>DDS/DDX decode for reference and terrain textures. BSA read, then heavy CPU.</summary>
    TextureResolve,

    /// <summary>LAND heightmap + blend-table build for a terrain cell. Pure CPU.</summary>
    TerrainCellBuild
}

/// <summary>
///     Divides the machine's cores between the renderer's background workers, so that the pools stop
///     being sized in ignorance of each other.
///     <para>
///         <b>The problem this fixes.</b> Three pools each sized themselves from
///         <c>Environment.ProcessorCount</c> with independently-tuned clamps and no knowledge of the
///         others. On a 20-core machine that summed to <b>25 workers</b>, plus the UI thread and the
///         render thread, for 20 cores — and the reserve for those two threads was zero by
///         construction, because no code anywhere held the total. Oversubscription of that shape does
///         not show up as lower throughput; it shows up as frame-time jitter, because the render
///         thread is descheduled mid-frame by a decode worker.
///     </para>
///     <para>
///         <b>Advisory, never a blocking token pool.</b> Callers ask for a worker count and size
///         their own pool; nothing here hands out permits or waits. A blocking pool over these
///         particular workloads would be a genuine deadlock surface — a terrain build holds a token
///         while waiting on a texture resolve that needs one — and the failure would be a hung
///         viewer rather than a slow one.
///     </para>
///     <para>
///         Pure and GUI-free: it reads a core count passed in (defaulting to
///         <see cref="Environment.ProcessorCount" />) and computes integers, so every share is
///         testable at every core count without a machine to run it on.
///     </para>
/// </summary>
internal readonly record struct CpuBudget
{
    /// <summary>
    ///     Cores held back for the UI thread and the render thread while a frame loop is running.
    ///     These two are latency-critical and neither is counted among the workloads below.
    /// </summary>
    public const int InteractiveReserveCores = 2;

    /// <summary>
    ///     Worker-to-core ratio for bulk (non-interactive) work. Two, not one, because these
    ///     workloads all begin by blocking on an archive read: with no frame to protect, a worker
    ///     parked in I/O is a core going to waste. This is the same reasoning as
    ///     <see cref="ConcurrencyPolicy.DoubleCores" />, and it is what keeps the top-down batch
    ///     capture at the throughput it has today.
    /// </summary>
    public const int BulkOversubscription = 2;

    private CpuBudget(int totalWorkers, bool interactive)
    {
        TotalWorkers = totalWorkers;
        IsInteractive = interactive;
    }

    /// <summary>Workers this budget may hand out in total, before per-workload floors and ceilings.</summary>
    public int TotalWorkers { get; }

    /// <summary>True when a frame loop is running and the interactive reserve applies.</summary>
    public bool IsInteractive { get; }

    /// <summary>
    ///     The budget for a running frame loop: <c>cores − reserve</c>. The reserve shrinks on
    ///     machines too small to pay it — on a 4-core box, holding 2 cores back from 3 workloads
    ///     would starve one of them outright, and a workload that never runs is worse than a
    ///     descheduled render thread.
    /// </summary>
    public static CpuBudget Interactive(int? cores = null)
    {
        var count = ResolveCores(cores);
        var workloads = Enum.GetValues<CpuWorkload>().Length;
        var reserve = Math.Min(InteractiveReserveCores, Math.Max(0, count - workloads));
        return new CpuBudget(Math.Max(1, count - reserve), interactive: true);
    }

    /// <summary>
    ///     The budget for bulk work with no frame to protect — the top-down overlay and the batch
    ///     capture, both of which deliberately clear <c>StreamingThrottled</c> to fill in bulk. The
    ///     reserve is zero here (there is no interactive frame to reserve for) and the total is
    ///     oversubscribed by <see cref="BulkOversubscription" /> because the work blocks on I/O.
    /// </summary>
    public static CpuBudget Bulk(int? cores = null) =>
        new(Math.Max(1, ResolveCores(cores) * BulkOversubscription), interactive: false);

    /// <summary>
    ///     Picks the budget matching a renderer's <c>StreamingThrottled</c> flag: throttled means a
    ///     live frame loop, so interactive; unthrottled means a bulk fill.
    /// </summary>
    public static CpuBudget For(bool streamingThrottled, int? cores = null) =>
        streamingThrottled ? Interactive(cores) : Bulk(cores);

    /// <summary>
    ///     Workers <paramref name="workload" /> may run, as its weighted share of
    ///     <see cref="TotalWorkers" />, floored at 1 and capped at the workload's own ceiling.
    ///     <para>
    ///         The ceilings are the ones the hand-tuned constants already carried and are kept for
    ///         the reason they were introduced: past a point, more decode workers buy allocation
    ///         rate rather than throughput, and the GC pays for it.
    ///     </para>
    /// </summary>
    public int Claim(CpuWorkload workload)
    {
        var (weight, ceiling) = Shape(workload);
        var totalWeight = 0;
        foreach (var candidate in Enum.GetValues<CpuWorkload>())
        {
            totalWeight += Shape(candidate).Weight;
        }

        var share = (int)((long)TotalWorkers * weight / totalWeight);
        return Math.Clamp(share, 1, ceiling);
    }

    /// <summary>Total workers actually handed out — the sum of every <see cref="Claim" />.</summary>
    public int TotalClaimed()
    {
        var sum = 0;
        foreach (var workload in Enum.GetValues<CpuWorkload>())
        {
            sum += Claim(workload);
        }

        return sum;
    }

    /// <summary>
    ///     Relative weight and hard ceiling per workload.
    ///     <para>
    ///         Decode and terrain build are weighted above texture resolve because both are the
    ///         measured binding constraint on a cold worldspace fill, while texture resolve is
    ///         partly served from the persistent disk cache. The ceilings (16/12/16) are exactly the
    ///         maxima the previous per-pool constants used, so a large machine keeps the behaviour
    ///         those were tuned against; the change is entirely to what SMALL and mid-size machines
    ///         get, which is where the oversubscription actually bit.
    ///     </para>
    /// </summary>
    private static (int Weight, int Ceiling) Shape(CpuWorkload workload) => workload switch
    {
        CpuWorkload.ReferenceMeshDecode => (3, 16),
        CpuWorkload.TextureResolve => (2, 12),
        CpuWorkload.TerrainCellBuild => (3, 16),
        _ => (1, 8)
    };

    private static int ResolveCores(int? cores) => Math.Max(1, cores ?? Environment.ProcessorCount);
}
