namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     Which adapters <see cref="GpuDevice12.Create" /> may bind. Hardware adapters are always
///     probed best-first across the whole 12.x feature-level family (12_2 → 12_1 → 12_0); the
///     policy decides what happens when none of them yields a device.
/// </summary>
internal enum GpuAdapterPolicy
{
    /// <summary>
    ///     Hardware adapters only. Callers with their own software fallback (the CLI sprite
    ///     rasterizer) or whose numbers are meaningless off real silicon (the renderer profiler)
    ///     use this.
    /// </summary>
    HardwareOnly,

    /// <summary>
    ///     Hardware adapters first, then the WARP software rasterizer as a last resort so the
    ///     3D view renders SOMETHING (slowly) instead of a blank panel on machines with no
    ///     12_0-capable GPU. <see cref="GpuDevice12.IsSoftwareAdapter" /> reports the outcome.
    /// </summary>
    PreferHardwareThenWarp,

    /// <summary>WARP only — exercises the software path on hardware-capable machines (tests, triage).</summary>
    WarpOnly
}
