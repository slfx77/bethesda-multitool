using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Direct3D12.Debug;
using Vortice.DXGI;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     v3 Pass 4 — D3D12 device + direct (graphics) command queue. The D3D12 analog of
///     <see cref="GpuDevice" />, but with no immediate context (D3D12 records into command
///     lists that are submitted to a queue).
///     <para>
///         Per-frame state (command allocator, fence values, ring buffer offsets) lives in
///         <c>GpuCommandRecorder12</c> — created elsewhere and bound to this device. This
///         class owns only the long-lived objects: device, queue, fence, adapter description.
///     </para>
/// </summary>
internal sealed class GpuDevice12 : IDisposable
{
    private static readonly Logger Log = Logger.Instance;

    private GpuDevice12(
        ID3D12Device device,
        ID3D12CommandQueue directQueue,
        ID3D12Fence frameFence,
        string deviceName,
        FeatureLevel featureLevel,
        ID3D12InfoQueue? infoQueue)
    {
        Device = device;
        DirectQueue = directQueue;
        FrameFence = frameFence;
        DeviceName = deviceName;
        FeatureLevel = featureLevel;
        _infoQueue = infoQueue;
    }

    private readonly ID3D12InfoQueue? _infoQueue;

    /// <summary>The underlying D3D12 device (resource + descriptor factory).</summary>
    public ID3D12Device Device { get; }

    /// <summary>The direct (graphics + compute + copy) command queue. Used by both the
    /// swap-chain swap (presents from this queue) and the per-frame command recorder.</summary>
    public ID3D12CommandQueue DirectQueue { get; }

    /// <summary>Fence used for CPU↔GPU sync at frame boundaries. The command recorder
    /// owns the per-frame fence values; this object is just the underlying fence handle.</summary>
    public ID3D12Fence FrameFence { get; }

    /// <summary>Adapter description string, for logging + HUD.</summary>
    public string DeviceName { get; }

    /// <summary>Highest feature level the device was created at (≥ 12_0).</summary>
    public FeatureLevel FeatureLevel { get; }

    /// <summary>Backend identifier for HUD + log lines. Mirrors <see cref="GpuDevice.Backend" />.</summary>
    public static string Backend => "Direct3D12";

    /// <summary>
    ///     Drains messages from the D3D12 info queue (debug layer) and logs each one.
    ///     No-op when the debug layer wasn't enabled at <see cref="Create" /> time. Call
    ///     once per frame; messages accumulate fast under stress.
    /// </summary>
    public void PumpDebugMessages()
    {
        if (_infoQueue is null) return;
        var count = _infoQueue.NumStoredMessages;
        for (ulong i = 0; i < count; i++)
        {
            var msg = _infoQueue.GetMessage(i);
            var severity = msg.Severity switch
            {
                Vortice.Direct3D12.Debug.MessageSeverity.Corruption => "CORRUPTION",
                Vortice.Direct3D12.Debug.MessageSeverity.Error => "ERROR",
                Vortice.Direct3D12.Debug.MessageSeverity.Warning => "WARN",
                Vortice.Direct3D12.Debug.MessageSeverity.Info => "INFO",
                _ => "MSG"
            };
            Log.Warn("[D3D12 {0}] {1}: {2}", severity, msg.Id, msg.Description);
        }
        _infoQueue.ClearStoredMessages();
    }

    public void Dispose()
    {
        _infoQueue?.Dispose();
        FrameFence.Dispose();
        DirectQueue.Dispose();
        Device.Dispose();
    }

    /// <summary>
    ///     Creates a headless D3D12 device + direct queue + frame fence. Returns null when
    ///     no D3D12 backend is available (non-Windows, no compatible adapter, or D3D12 not
    ///     enabled in the OS feature set).
    ///     <para>
    ///         Feature levels probed in descending order: 12_2, 12_1, 12_0. We require 12_0
    ///         minimum — bindless SRVs (Resource Binding Tier 2), ExecuteIndirect, and root
    ///         signatures all land at 12_0. 12_1 adds conservative rasterization (not used);
    ///         12_2 adds mesh shaders (Pass 4 Step 4 candidate).
    ///     </para>
    /// </summary>
    public static GpuDevice12? Create(bool enableDebugLayer = false)
    {
        if (!OperatingSystem.IsWindows())
        {
            Log.Warn("D3D12 is Windows-only — no GPU backend available");
            return null;
        }

        // Optional debug layer — turns on the D3D12 validation layer (GPU breaks on bad API
        // use; messages to the Output window). Off by default — adds ~30% CPU overhead per
        // draw on validation alone. Enable only when chasing a D3D12 correctness bug.
        if (enableDebugLayer)
        {
            try
            {
                var result = Vortice.Direct3D12.D3D12.D3D12GetDebugInterface(out ID3D12Debug? debug);
                if (result.Success && debug is not null)
                {
                    debug.EnableDebugLayer();
                    debug.Dispose();
                    Log.Info("GpuDevice12: D3D12 debug layer enabled");
                }
                else
                {
                    Log.Warn(
                        "GpuDevice12: FALLOUT_VIEWER_D3D12_DEBUG=1 but D3D12GetDebugInterface failed ({0}). " +
                        "On Windows install \"Graphics Tools\" optional feature: " +
                        "Settings → Apps → Optional features → Add a feature → Graphics Tools. " +
                        "Without it the debug layer can't load and validation messages won't surface.",
                        result);
                }
            }
            catch (SharpGenException ex)
            {
                Log.Warn("GpuDevice12: debug layer not available: {0}", ex.Message);
            }
        }

        FeatureLevel[] featureLevels =
        [
            FeatureLevel.Level_12_2,
            FeatureLevel.Level_12_1,
            FeatureLevel.Level_12_0
        ];

        foreach (var minLevel in featureLevels)
        {
            try
            {
                var result = Vortice.Direct3D12.D3D12.D3D12CreateDevice(
                    adapter: null,
                    minLevel,
                    out ID3D12Device? device);

                if (result.Failure || device is null)
                {
                    Log.Debug("GpuDevice12: D3D12CreateDevice failed at feature level {0}: {1}", minLevel, result);
                    device?.Dispose();
                    continue;
                }

                ID3D12CommandQueue? queue = null;
                ID3D12Fence? fence = null;
                try
                {
                    var queueDesc = new CommandQueueDescription(CommandListType.Direct, CommandQueuePriority.Normal);
                    queue = device.CreateCommandQueue<ID3D12CommandQueue>(queueDesc);
                    fence = device.CreateFence<ID3D12Fence>(0, FenceFlags.None);

                    // If the debug layer is enabled, query ID3D12InfoQueue so PumpDebugMessages
                    // can route validation messages to the app logger. Without this, messages
                    // go to OutputDebugString (invisible to the app).
                    ID3D12InfoQueue? infoQueue = null;
                    if (enableDebugLayer)
                    {
                        try
                        {
                            infoQueue = device.QueryInterfaceOrNull<ID3D12InfoQueue>();
                            if (infoQueue is not null)
                            {
                                Log.Info("GpuDevice12: ID3D12InfoQueue attached — validation messages will surface via PumpDebugMessages()");
                            }
                        }
                        catch (SharpGenException ex)
                        {
                            Log.Warn("GpuDevice12: failed to attach info queue: {0}", ex.Message);
                        }
                    }

                    var deviceName = QueryAdapterDescription(device);
                    Log.Info("GpuDevice12: created Direct3D 12 device at {0} ({1})", minLevel, deviceName);
                    return new GpuDevice12(device, queue, fence, deviceName, minLevel, infoQueue);
                }
                catch
                {
                    fence?.Dispose();
                    queue?.Dispose();
                    device.Dispose();
                    throw;
                }
            }
            catch (SharpGenException ex)
            {
                Log.Warn("GpuDevice12: device init threw at feature level {0}: {1}", minLevel, ex.Message);
            }
        }

        Log.Warn("GpuDevice12: no D3D12 device available at feature level 12_0 or higher");
        return null;
    }

    private static string QueryAdapterDescription(ID3D12Device device)
    {
        try
        {
            var luid = new Vortice.Luid((uint)(device.AdapterLuid & 0xFFFFFFFF), (int)(device.AdapterLuid >> 32));
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory4>();
            using var adapter = factory.EnumAdapterByLuid<IDXGIAdapter1>(luid);
            return adapter.Description1.Description;
        }
        catch (SharpGenException)
        {
            return "Unknown D3D12 adapter";
        }
    }
}
