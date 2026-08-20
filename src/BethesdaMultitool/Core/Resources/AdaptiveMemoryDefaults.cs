namespace BethesdaMultitool.Core.Resources;

/// <summary>
///     Machine-scaled DEFAULTS for the viewer's memory budgets — one shipped size fits nobody
///     (a 64 GB workstation was starving its caches at laptop-safe sizes; a laptop would pay
///     workstation-sized commitments). Each budget scales against the pool it actually consumes:
///     the SYSTEM-RAM-backed budgets here (upload-heap ring, decoded CPU mesh cache); the
///     VRAM-backed shadow cascades scale in <c>ShadowMapRenderer12.ResolveResolution</c> against
///     the DXGI local-memory budget. Env knobs still pin any value explicitly — the eviction
///     stress gates rely on that, and the sizes below only apply when the knob is unset.
/// </summary>
internal static class AdaptiveMemoryDefaults
{
    /// <summary>
    ///     Physical memory visible to this process, in MB — the GC's view, which respects
    ///     job-object/container limits rather than reporting bare-metal RAM the process can't use.
    /// </summary>
    public static long SystemMemoryMb { get; } =
        GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024L * 1024L);

    /// <summary>
    ///     Upload-heap ring size per frame slot, in MB (× frames-in-flight slots committed).
    ///     Upload heaps are system-memory-backed; a bigger ring means dense whole-map frames stop
    ///     soft-dropping draws on TryAllocate failure. 128 is the long-shipped default (kept for
    ///     the mid tier); big-RAM machines double it, small ones halve it.
    /// </summary>
    public static int RingBufferMegabytes(long systemMemoryMb)
    {
        return systemMemoryMb switch
        {
            >= 24_000 => 256,
            >= 12_000 => 128,
            _ => 64
        };
    }

    /// <summary>
    ///     Decoded CPU mesh payload cache, in MB (NIF decode results awaiting/backing GPU
    ///     residency — a bigger cache means fewer re-decodes when the camera revisits areas).
    ///     256 is the long-shipped default (kept for the mid tier).
    /// </summary>
    public static int DecodedMeshCacheMegabytes(long systemMemoryMb)
    {
        return systemMemoryMb switch
        {
            >= 48_000 => 1024,
            >= 24_000 => 512,
            >= 12_000 => 256,
            _ => 128
        };
    }
}
