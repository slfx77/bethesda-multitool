using Veldrid;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu;

/// <summary>
///     Manages a Veldrid <see cref="GraphicsDevice" /> for headless offscreen rendering.
///     Creates the device once and reuses it across renders. Thread-safe for sequential use.
/// </summary>
internal sealed class GpuDevice : IDisposable
{
    private static readonly Logger Log = Logger.Instance;

    private GpuDevice(GraphicsDevice device, GraphicsBackend backend)
    {
        Device = device;
        Backend = backend;
    }

    public GraphicsDevice Device { get; }
    public GraphicsBackend Backend { get; }
    public ResourceFactory Factory => Device.ResourceFactory;

    public void Dispose()
    {
        Device.Dispose();
    }

    /// <summary>
    ///     Creates a headless GPU device (no swapchain).
    ///     Tries Vulkan first (cross-platform), then D3D11 (Windows fallback).
    /// </summary>
    /// <returns>A new GpuDevice, or null if no GPU backend is available.</returns>
    public static GpuDevice? Create()
    {
        var options = new GraphicsDeviceOptions
        {
            PreferStandardClipSpaceYDirection = true,
            PreferDepthRangeZeroToOne = true,
            SwapchainDepthFormat = null // no swapchain
        };

        // Try Vulkan first (works on Windows + Linux)
        try
        {
            var device = GraphicsDevice.CreateVulkan(options);
            Log.Info("GPU device created: Vulkan ({0})", device.DeviceName);
            return new GpuDevice(device, GraphicsBackend.Vulkan);
        }
        catch (Exception ex)
        {
            Log.Debug("Vulkan not available: {0}", ex.Message);
        }

        // Try D3D11 (Windows fallback)
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var device = GraphicsDevice.CreateD3D11(options);
                Log.Info("GPU device created: Direct3D 11 ({0})", device.DeviceName);
                return new GpuDevice(device, GraphicsBackend.Direct3D11);
            }
            catch (Exception ex)
            {
                Log.Debug("D3D11 not available: {0}", ex.Message);
            }
        }

        Log.Warn("No GPU backend available — falling back to CPU rendering");
        return null;
    }
}
