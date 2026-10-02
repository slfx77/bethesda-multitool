using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Slfx77.Multitool.WinUI.Rendering;

namespace BethesdaMultitool;

/// <summary>Preserves the actual Bethesda tonemap/water/depth-copy surface behind generic lifetime operations.</summary>
internal sealed class BethesdaNativeViewportSurface(GpuSwapChainSurface12 surface) : INativeViewportSurface
{
    /// <summary>Gets the borrowed native policy surface for the Bethesda backend.</summary>
    internal GpuSwapChainSurface12 Surface => surface;
    /// <summary>Gets the current physical buffer width.</summary>
    public uint PixelWidth => surface.Width;
    /// <summary>Gets the current physical buffer height.</summary>
    public uint PixelHeight => surface.Height;
    /// <summary>Resizes after the shared session proves the previous GPU use retired.</summary>
    /// <param name="width">New physical width.</param>
    /// <param name="height">New physical height.</param>
    public void Resize(uint width, uint height) => surface.Resize(width, height);
    /// <summary>Detaches and releases the surface, retaining native partial failure for retry.</summary>
    public void Dispose() => surface.Dispose();
}
