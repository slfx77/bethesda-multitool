using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu;

namespace FalloutXbox360Utils.CLI.Rendering;

internal sealed class SpriteRenderBackendSelection : IDisposable
{
    internal SpriteRenderBackendSelection(
        GpuDevice? device,
        GpuSpriteRenderer? renderer,
        bool shouldAbort)
    {
        Device = device;
        Renderer = renderer;
        ShouldAbort = shouldAbort;
    }

    internal GpuDevice? Device { get; }

    internal GpuSpriteRenderer? Renderer { get; }

    internal bool ShouldAbort { get; }

    public void Dispose()
    {
        Renderer?.Dispose();
        Device?.Dispose();
    }
}