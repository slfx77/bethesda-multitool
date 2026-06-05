using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12;

namespace FalloutXbox360Utils.CLI.Rendering;

/// <summary>
///     CLI sprite render backend handle. Null <see cref="Device" /> + <see cref="Renderer" />
///     means the caller should fall back to the CPU software renderer.
/// </summary>
internal sealed class SpriteRenderBackendSelection : IDisposable
{
    internal SpriteRenderBackendSelection(
        GpuDevice12? device,
        GpuSpriteRenderer12? renderer,
        bool shouldAbort)
    {
        Device = device;
        Renderer = renderer;
        ShouldAbort = shouldAbort;
    }

    internal GpuDevice12? Device { get; }

    internal GpuSpriteRenderer12? Renderer { get; }

    internal bool ShouldAbort { get; }

    public void Dispose()
    {
        Renderer?.Dispose();
        Device?.Dispose();
    }
}
