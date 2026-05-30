using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu;

/// <summary>
///     Owns a Direct3D 11 device + immediate context for headless offscreen rendering.
///     Created once and reused across renders. Thread-safe for sequential use only.
/// </summary>
internal sealed class GpuDevice : IDisposable
{
    private static readonly Logger Log = Logger.Instance;

    private GpuDevice(ID3D11Device device, ID3D11DeviceContext context, string deviceName)
    {
        Device = device;
        Context = context;
        DeviceName = deviceName;
    }

    /// <summary>The underlying D3D11 device (resource factory).</summary>
    public ID3D11Device Device { get; }

    /// <summary>Immediate context (command recorder + CPU readback).</summary>
    public ID3D11DeviceContext Context { get; }

    /// <summary>Adapter description string, for logging.</summary>
    public string DeviceName { get; }

    /// <summary>Backend identifier kept for compatibility with the prior Veldrid surface.</summary>
    public string Backend => "Direct3D11";

    public void Dispose()
    {
        Context.Dispose();
        Device.Dispose();
    }

    /// <summary>
    ///     Creates a headless D3D11 device (no swapchain).
    /// </summary>
    /// <returns>A new GpuDevice, or null if no D3D11 backend is available.</returns>
    public static GpuDevice? Create()
    {
        if (!OperatingSystem.IsWindows())
        {
            Log.Warn("D3D11 is Windows-only — no GPU backend available");
            return null;
        }

        FeatureLevel[] featureLevels =
        [
            FeatureLevel.Level_11_1,
            FeatureLevel.Level_11_0,
            FeatureLevel.Level_10_1,
            FeatureLevel.Level_10_0
        ];

        try
        {
            var result = D3D11.D3D11CreateDevice(
                adapter: null,
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                featureLevels,
                out ID3D11Device? device,
                out FeatureLevel _,
                out ID3D11DeviceContext? context);

            if (result.Failure || device is null || context is null)
            {
                Log.Debug("D3D11 device creation failed: {0}", result);
                device?.Dispose();
                context?.Dispose();
                return null;
            }

            var deviceName = QueryAdapterDescription(device);
            Log.Info("GPU device created: Direct3D 11 ({0})", deviceName);
            return new GpuDevice(device, context, deviceName);
        }
        catch (SharpGenException ex)
        {
            Log.Warn("D3D11 device creation threw: {0}", ex.Message);
            return null;
        }
    }

    private static string QueryAdapterDescription(ID3D11Device device)
    {
        try
        {
            using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            return adapter.Description.Description;
        }
        catch (SharpGenException)
        {
            return "Unknown D3D11 adapter";
        }
    }
}
