namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     Selects where <see cref="GpuGeometryArena12" /> keeps its long-lived vertex and index data.
///     The zero/default value deliberately preserves the established persistently-mapped UPLOAD-heap
///     path; DEFAULT heap is opt-in because it adds a copy command and staging lifetime contract.
/// </summary>
internal enum GpuGeometryArenaBackingMode
{
    UploadHeap = 0,
    DefaultHeap = 1
}

/// <summary>Fail-closed parsing for the opt-in backing experiment.</summary>
internal static class GpuGeometryArenaBackingModePolicy
{
    /// <summary>
    ///     Only the explicit, trimmed, case-insensitive token <c>default</c> enables DEFAULT heap.
    ///     Unset, <c>upload</c>, and unknown values retain the established UPLOAD path.
    /// </summary>
    public static GpuGeometryArenaBackingMode Parse(string? value) =>
        string.Equals(value?.Trim(), "default", StringComparison.OrdinalIgnoreCase)
            ? GpuGeometryArenaBackingMode.DefaultHeap
            : GpuGeometryArenaBackingMode.UploadHeap;
}
