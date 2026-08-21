using System.Globalization;
using BethesdaMultitool.Core.Diagnostics;
using SharpGen.Runtime;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Direct3D12.Debug;
using Vortice.DXGI;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     v3 Pass 4 — D3D12 device + direct (graphics) command queue. The D3D12 analog of
///     the old <c>GpuDevice</c>, but with no immediate context (D3D12 records into command
///     lists that are submitted to a queue).
///     <para>
///         Per-frame state (command allocator, fence values, ring buffer offsets) lives in
///         <c>GpuCommandRecorder12</c> — created elsewhere and bound to this device. This
///         class owns only the long-lived objects: device, queue, fence, adapter description.
///     </para>
/// </summary>
internal sealed class GpuDevice12 : IDisposable
{
    /// <summary>
    ///     Process-scoped verification override. Set to <c>1</c> before device creation to exercise
    ///     the renderer's single-sample resource/state path on hardware that normally selects 4x.
    ///     Unset and unsupported values retain the normal 4x capability probe.
    /// </summary>
    internal const string SceneSampleCountEnvironmentVariable = "FALLOUT_VIEWER_SCENE_SAMPLES";

    private static readonly Logger Log = Logger.Instance;

    private readonly ID3D12InfoQueue? _infoQueue;
    private IDXGIAdapter3? _videoMemoryAdapter;
    private bool _videoMemoryAdapterResolved;

    private GpuDevice12(
        ID3D12Device device,
        ID3D12CommandQueue directQueue,
        ID3D12Fence frameFence,
        string deviceName,
        FeatureLevel featureLevel,
        ID3D12InfoQueue? infoQueue,
        bool dredEnabled,
        int sceneSampleCount,
        bool isSoftwareAdapter)
    {
        Device = device;
        DirectQueue = directQueue;
        FrameFence = frameFence;
        DeviceName = deviceName;
        FeatureLevel = featureLevel;
        _infoQueue = infoQueue;
        DredEnabled = dredEnabled;
        SceneSampleCount = sceneSampleCount;
        IsSoftwareAdapter = isSoftwareAdapter;
    }

    /// <summary>The underlying D3D12 device (resource + descriptor factory).</summary>
    public ID3D12Device Device { get; }

    /// <summary>
    ///     The direct (graphics + compute + copy) command queue. Used by both the
    ///     swap-chain swap (presents from this queue) and the per-frame command recorder.
    /// </summary>
    public ID3D12CommandQueue DirectQueue { get; }

    /// <summary>
    ///     Fence used for CPU↔GPU sync at frame boundaries. The command recorder
    ///     owns the per-frame fence values; this object is just the underlying fence handle.
    /// </summary>
    public ID3D12Fence FrameFence { get; }

    /// <summary>Adapter description string, for logging + HUD.</summary>
    public string DeviceName { get; }

    /// <summary>Highest feature level the device was created at (≥ 12_0).</summary>
    public FeatureLevel FeatureLevel { get; }

    /// <summary>
    ///     True when the device was created on the WARP software rasterizer because no hardware
    ///     adapter reached feature level 12_0 (only possible under
    ///     <see cref="GpuAdapterPolicy.PreferHardwareThenWarp" /> / <see cref="GpuAdapterPolicy.WarpOnly" />).
    ///     Callers surface this to the user — WARP renders correctly but slowly.
    /// </summary>
    public bool IsSoftwareAdapter { get; }

    /// <summary>
    ///     True when DRED (Device Removed Extended Data) was force-enabled at device
    ///     creation, so <see cref="LogDeviceRemovedDiagnostics" /> can dump GPU auto-breadcrumbs +
    ///     the page-fault VA after a device removal. Enabled by FALLOUT_VIEWER_DRED=1 (or the debug
    ///     layer). Off by default — breadcrumbs add per-command GPU overhead.
    /// </summary>
    public bool DredEnabled { get; }

    /// <summary>
    ///     Sample count for the live 3D scene's MSAA render target (and every PSO that draws into
    ///     it, plus the top-down overlay target). 4 when 4x MSAA is supported for the scene's color
    ///     (<see cref="Format.B8G8R8A8_UNorm" />) + depth (<see cref="Format.D32_Float" />) formats —
    ///     the norm at feature level 12_0+ — else 1 (no MSAA). A process may also force 1x for
    ///     deterministic verification with <c>FALLOUT_VIEWER_SCENE_SAMPLES=1</c>. Decided ONCE at
    ///     device creation so the PSOs (built before any surface exists) and every scene render
    ///     target agree on the count; a mismatch is a D3D12 validation failure on every draw.
    /// </summary>
    public int SceneSampleCount { get; }

    /// <summary>True when <see cref="SceneSampleCount" /> &gt; 1 (scene MSAA active).</summary>
    public bool SceneMsaaEnabled => SceneSampleCount > 1;

    /// <summary>Backend identifier for HUD + log lines. Mirrors the old <c>GpuDevice.Backend</c>.</summary>
    public static string Backend => "Direct3D12";

    public void Dispose()
    {
        _videoMemoryAdapter?.Dispose();
        _infoQueue?.Dispose();
        FrameFence.Dispose();
        DirectQueue.Dispose();
        Device.Dispose();
    }

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
                MessageSeverity.Corruption => "CORRUPTION",
                MessageSeverity.Error => "ERROR",
                MessageSeverity.Warning => "WARN",
                MessageSeverity.Info => "INFO",
                _ => "MSG"
            };
            Log.Warn("[D3D12 {0}] {1}: {2}", severity, msg.Id, msg.Description);
        }

        _infoQueue.ClearStoredMessages();
    }

    /// <summary>
    ///     Call from a render/upload failure handler to attribute a GPU device removal. Logs the
    ///     device-removed reason (HUNG / RESET / DRIVER_INTERNAL_ERROR / page-fault), and — when
    ///     DRED was enabled (<see cref="DredEnabled" />) — the page-fault VA plus the offending
    ///     allocations and the last GPU auto-breadcrumb op per command queue. No-op (returns
    ///     quietly) when the device has NOT actually been removed, so it's safe to call from any
    ///     catch block. Multiple calls are harmless.
    /// </summary>
    public void LogDeviceRemovedDiagnostics(string context)
    {
        Result reason;
        try
        {
            reason = Device.DeviceRemovedReason;
        }
        catch (SharpGenException ex)
        {
            Log.Warn("GpuDevice12: could not query DeviceRemovedReason ({0}): {1}", context, ex.Message);
            return;
        }

        // S_OK → the device is fine; whatever threw was not a device removal.
        if (reason.Success)
        {
            return;
        }

        Log.Error("GpuDevice12: GPU DEVICE REMOVED [{0}] reason=0x{1:X8} ({2})",
            context, reason.Code, DescribeDeviceRemovedReason(reason.Code));

        if (!DredEnabled)
        {
            Log.Error(
                "GpuDevice12: set {0}=1 and reproduce to capture the GPU breadcrumb + page-fault VA.",
                EnvironmentVariables.Viewer.Dred);
            return;
        }

        try
        {
            using var dred = Device.QueryInterfaceOrNull<ID3D12DeviceRemovedExtendedData>();
            if (dred is null)
            {
                Log.Warn("GpuDevice12: DRED interface unavailable on this device.");
                return;
            }

            var foundPageFault = dred.GetPageFaultAllocationOutput(out var pageFault).Success &&
                                 pageFault.PageFaultVA != 0;
            if (foundPageFault)
            {
                Log.Error("GpuDevice12: DRED page-fault GPU VA = 0x{0:X}", pageFault.PageFaultVA);
                LogAllocationNodes("live-at-fault", pageFault.HeadExistingAllocationNode);
                LogAllocationNodes("recently-freed", pageFault.HeadRecentFreedAllocationNode);
            }

            var breadcrumbNodes = 0;
            if (dred.GetAutoBreadcrumbsOutput(out var breadcrumbs).Success)
            {
                var node = breadcrumbs.HeadAutoBreadcrumbNode;
                while (node is not null && breadcrumbNodes < 32)
                {
                    var lastCompleted = node.LastBreadcrumbValue;
                    Log.Error("GpuDevice12: DRED breadcrumb queue='{0}' list='{1}' completed={2}/{3} ops",
                        node.CommandQueueDebugName ?? "(unnamed)",
                        node.CommandListDebugName ?? "(unnamed)",
                        lastCompleted.HasValue ? lastCompleted.Value.ToString(CultureInfo.InvariantCulture) : "?",
                        node.BreadcrumbCount);

                    // The GPU stopped at the last completed breadcrumb — log the op there + neighbors.
                    var history = node.CommandHistory;
                    if (history is { Length: > 0 } && lastCompleted is { } idx && idx < history.Length)
                    {
                        var from = Math.Max(0, idx - 2);
                        var to = Math.Min(history.Length - 1, idx + 2);
                        for (var i = from; i <= to; i++)
                        {
                            Log.Error("GpuDevice12:   op[{0}]{1} = {2}", i,
                                i == idx ? " <-- GPU stopped here" : string.Empty, history[i]);
                        }
                    }

                    node = node.Next;
                    breadcrumbNodes++;
                }
            }

            if (!foundPageFault && breadcrumbNodes == 0)
            {
                Log.Error(
                    "GpuDevice12: DRED produced no page-fault VA and no auto-breadcrumb history — consistent with a " +
                    "TDR timeout (a GPU op ran too long) rather than a memory fault. Reproduce with " +
                    $"{EnvironmentVariables.Viewer.D3D12Debug}=1 (add {EnvironmentVariables.Viewer.D3D12GpuBasedValidation}=1 to instrument shaders) to capture " +
                    "the offending operation in the [D3D12 ...] validation log.");
            }
        }
        catch (SharpGenException ex)
        {
            Log.Warn("GpuDevice12: DRED dump failed ({0}): {1}", context, ex.Message);
        }
    }

    private static void LogAllocationNodes(string label, DredAllocationNode? head)
    {
        var node = head;
        var count = 0;
        while (node is not null && count < 16)
        {
            Log.Error("GpuDevice12:   {0} allocation '{1}' ({2})", label, node.ObjectName ?? "(unnamed)",
                node.AllocationType);
            node = node.Next;
            count++;
        }
    }

    private static string DescribeDeviceRemovedReason(int code)
    {
        return (uint)code switch
        {
            0x887A0006 => "DXGI_ERROR_DEVICE_HUNG — the app's own GPU commands hung/faulted (TDR)",
            0x887A0005 => "DXGI_ERROR_DEVICE_REMOVED",
            0x887A0007 => "DXGI_ERROR_DEVICE_RESET — unexpected device reset",
            0x887A0020 => "DXGI_ERROR_DRIVER_INTERNAL_ERROR",
            0x887A0001 => "DXGI_ERROR_INVALID_CALL",
            0x80070057 => "E_INVALIDARG",
            _ => "see DXGI/D3D12 HRESULT reference"
        };
    }

    /// <summary>
    ///     Queries the adapter's LOCAL (dedicated VRAM) memory usage + OS-assigned budget, in MB.
    ///     Returns false if the adapter doesn't expose <see cref="IDXGIAdapter3" /> or the query
    ///     fails. Used by the profiler/diagnostic logging to spot GPU-memory exhaustion (which
    ///     surfaces as a device removal / "crash with no cause") before it happens.
    /// </summary>
    public bool TryQueryLocalVideoMemoryMb(out long currentUsageMb, out long budgetMb)
    {
        currentUsageMb = 0;
        budgetMb = 0;
        var adapter = ResolveVideoMemoryAdapter();
        if (adapter is null)
        {
            return false;
        }

        try
        {
            var info = adapter.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local);
            currentUsageMb = (long)(info.CurrentUsage / (1024UL * 1024UL));
            budgetMb = (long)(info.Budget / (1024UL * 1024UL));
            return true;
        }
        catch (SharpGenException)
        {
            return false;
        }
    }

    private IDXGIAdapter3? ResolveVideoMemoryAdapter()
    {
        if (_videoMemoryAdapterResolved)
        {
            return _videoMemoryAdapter;
        }

        _videoMemoryAdapterResolved = true;
        try
        {
            var luid = new Luid((uint)(Device.AdapterLuid & 0xFFFFFFFF), (int)(Device.AdapterLuid >> 32));
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory4>();
            _videoMemoryAdapter = factory.EnumAdapterByLuid<IDXGIAdapter3>(luid);
        }
        catch (SharpGenException)
        {
            _videoMemoryAdapter = null;
        }

        return _videoMemoryAdapter;
    }

    /// <summary>
    ///     Creates a headless D3D12 device + direct queue + frame fence. Returns null when
    ///     no D3D12 backend is available (non-Windows, no compatible adapter, or D3D12 not
    ///     enabled in the OS feature set).
    ///     <para>
    ///         Feature levels probed in descending order: 12_2, 12_1, 12_0 — the whole 12.x
    ///         family. We require 12_0 minimum — bindless SRVs (Resource Binding Tier 2),
    ///         ExecuteIndirect, and root signatures all land at 12_0. 12_1 adds conservative
    ///         rasterization (not used); 12_2 adds mesh shaders (Pass 4 Step 4 candidate).
    ///         Older Windows 10 runtimes that do not know the 12_2 enum reject it with
    ///         E_INVALIDARG, which falls through the ladder like any other failure.
    ///     </para>
    ///     <para>
    ///         Hardware adapters are enumerated explicitly, best-first (IDXGIFactory6
    ///         performance ordering when the OS has it, plain DXGI order otherwise) — a plain
    ///         <c>D3D12CreateDevice(null, …)</c> only ever probes the DEFAULT adapter, which on
    ///         hybrid-graphics machines is routinely an iGPU below 12_0 even though a capable
    ///         discrete GPU is present. Under
    ///         <see cref="GpuAdapterPolicy.PreferHardwareThenWarp" /> the WARP software
    ///         rasterizer is the last resort so those machines still render.
    ///     </para>
    /// </summary>
    public static GpuDevice12? Create(
        bool enableDebugLayer = false,
        GpuAdapterPolicy adapterPolicy = GpuAdapterPolicy.HardwareOnly)
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

                    // GPU-based validation (FALLOUT_VIEWER_D3D12_GBV=1): instruments shaders to catch
                    // out-of-bounds descriptor/bindless access, resource-state mismatches the CPU-side
                    // layer can't see, and similar GPU-side faults — the usual cause of DEVICE_HUNG
                    // (TDR) that DRED can't attribute (no page-fault VA, empty breadcrumbs). VERY slow
                    // (often 10x+), so it's a separate opt-in from the plain debug layer.
                    if (EnvironmentVariables.IsEnabled(EnvironmentVariables.Viewer.D3D12GpuBasedValidation))
                    {
                        using var debug1 = debug.QueryInterfaceOrNull<ID3D12Debug1>();
                        if (debug1 is not null)
                        {
                            debug1.SetEnableGPUBasedValidation(true);
                            Log.Info("GpuDevice12: D3D12 GPU-based validation enabled (slow — diagnostic only)");
                        }
                        else
                        {
                            Log.Warn("GpuDevice12: ID3D12Debug1 unavailable — GPU-based validation not enabled.");
                        }
                    }

                    debug.Dispose();
                    Log.Info("GpuDevice12: D3D12 debug layer enabled");
                }
                else
                {
                    Log.Warn(
                        "GpuDevice12: {0}=1 but D3D12GetDebugInterface failed ({1}). " +
                        "On Windows install \"Graphics Tools\" optional feature: " +
                        "Settings → Apps → Optional features → Add a feature → Graphics Tools. " +
                        "Without it the debug layer can't load and validation messages won't surface.",
                        EnvironmentVariables.Viewer.D3D12Debug,
                        result);
                }
            }
            catch (SharpGenException ex)
            {
                Log.Warn("GpuDevice12: debug layer not available: {0}", ex.Message);
            }
        }

        // DRED (Device Removed Extended Data) must be force-enabled BEFORE device creation, like
        // the debug layer. It captures GPU auto-breadcrumbs + the page-fault VA so a device
        // removal (TDR / GPU page fault — the usual cause of a "crash with no managed exception")
        // can be attributed via LogDeviceRemovedDiagnostics. Opt-in (FALLOUT_VIEWER_DRED=1) because
        // breadcrumbs add per-command overhead; also on whenever the debug layer is on.
        var enableDred = enableDebugLayer || EnvironmentVariables.IsEnabled(EnvironmentVariables.Viewer.Dred);
        if (enableDred)
        {
            try
            {
                var dredResult = Vortice.Direct3D12.D3D12.D3D12GetDebugInterface(
                    out ID3D12DeviceRemovedExtendedDataSettings? dredSettings);
                if (dredResult.Success && dredSettings is not null)
                {
                    dredSettings.SetAutoBreadcrumbsEnablement(DredEnablement.ForcedOn);
                    dredSettings.SetPageFaultEnablement(DredEnablement.ForcedOn);
                    // NOTE: deliberately NOT enabling Watson dump — SetWatsonDumpEnablement(ForcedOn)
                    // raises a __fastfail (0xC0000409) on device removal, which kills the process
                    // before our managed catch can run LogDeviceRemovedDiagnostics (and before the
                    // log flushes). We want the removal to surface as a catchable HRESULT instead.
                    dredSettings.Dispose();
                    Log.Info("GpuDevice12: DRED enabled (auto-breadcrumbs + page-fault).");
                }
                else
                {
                    Log.Warn("GpuDevice12: DRED requested but D3D12GetDebugInterface(DRED settings) failed ({0}).",
                        dredResult);
                    enableDred = false;
                }
            }
            catch (SharpGenException ex)
            {
                Log.Warn("GpuDevice12: DRED enable failed: {0}", ex.Message);
                enableDred = false;
            }
        }

        FeatureLevel[] featureLevels =
        [
            FeatureLevel.Level_12_2,
            FeatureLevel.Level_12_1,
            FeatureLevel.Level_12_0
        ];

        IDXGIFactory4? dxgiFactory = null;
        try
        {
            dxgiFactory = DXGI.CreateDXGIFactory1<IDXGIFactory4>();
        }
        catch (SharpGenException ex)
        {
            Log.Warn("GpuDevice12: DXGI factory creation failed ({0}) — probing the default adapter only.",
                ex.Message);
        }

        var probed = new List<string>();
        try
        {
            if (dxgiFactory is null)
            {
                // No DXGI factory (broken graphics stack): the legacy default-adapter probe is
                // all that is left.
                if (adapterPolicy != GpuAdapterPolicy.WarpOnly)
                {
                    var created = TryCreateOnAdapter(
                        null, adapterName: null, featureLevels, enableDebugLayer, enableDred,
                        isSoftwareAdapter: false, probed);
                    if (created is not null) return created;
                }
            }
            else
            {
                if (adapterPolicy != GpuAdapterPolicy.WarpOnly)
                {
                    // Every enumerated adapter is disposed in the finally, including the ones
                    // after an early return — D3D12CreateDevice AddRefs what it keeps, so
                    // releasing our enumeration references here is correct either way.
                    var hardwareAdapters = CollectHardwareAdapters(dxgiFactory, probed);
                    try
                    {
                        foreach (var adapter in hardwareAdapters)
                        {
                            var created = TryCreateOnAdapter(
                                adapter, adapter.Description1.Description, featureLevels, enableDebugLayer,
                                enableDred, isSoftwareAdapter: false, probed);
                            if (created is not null) return created;
                        }
                    }
                    finally
                    {
                        foreach (var adapter in hardwareAdapters) adapter.Dispose();
                    }
                }

                if (adapterPolicy != GpuAdapterPolicy.HardwareOnly)
                {
                    // Last resort: the WARP software rasterizer (feature level 12_1 on every
                    // supported Windows 10 build). Slow but correct — and visibly reported, so
                    // "renderer never shows up" becomes "renders slowly, says why" on machines
                    // with no 12_0-capable GPU.
                    try
                    {
                        using var warpAdapter = dxgiFactory.EnumWarpAdapter<IDXGIAdapter1>();
                        var created = TryCreateOnAdapter(
                            warpAdapter, warpAdapter.Description1.Description, featureLevels, enableDebugLayer,
                            enableDred, isSoftwareAdapter: true, probed);
                        if (created is not null) return created;
                    }
                    catch (SharpGenException ex)
                    {
                        Log.Warn("GpuDevice12: WARP adapter unavailable: {0}", ex.Message);
                        probed.Add("WARP: unavailable");
                    }
                }
            }
        }
        finally
        {
            dxgiFactory?.Dispose();
        }

        Log.Warn("GpuDevice12: no D3D12 device available at feature level 12_0 or higher. Adapters probed: {0}",
            probed.Count == 0 ? "(none found)" : string.Join("; ", probed));
        return null;
    }

    /// <summary>
    ///     Hardware adapters in preference order: IDXGIFactory6's performance ordering when the
    ///     OS provides it (Windows 10 1803+), plain DXGI enumeration order otherwise. Software
    ///     adapters (Microsoft Basic Render Driver) are filtered out here — WARP participation
    ///     is an explicit policy decision, not an enumeration accident. Caller disposes the
    ///     returned adapters.
    /// </summary>
    private static List<IDXGIAdapter1> CollectHardwareAdapters(IDXGIFactory4 factory, List<string> probed)
    {
        var adapters = new List<IDXGIAdapter1>();
        using (var factory6 = factory.QueryInterfaceOrNull<IDXGIFactory6>())
        {
            if (factory6 is not null)
            {
                // uint, not var: EnumAdapterByGpuPreference takes a uint index, and `var` would
                // infer int and fail to bind.
                for (uint index = 0;
                     factory6.EnumAdapterByGpuPreference(index, GpuPreference.HighPerformance,
                         out IDXGIAdapter1? adapter).Success && adapter is not null;
                     index++)
                {
                    adapters.Add(adapter);
                }
            }
        }

        if (adapters.Count == 0)
        {
            // uint, not var: EnumAdapters1 takes a uint index (see above).
            for (uint index = 0;
                 factory.EnumAdapters1(index, out var adapter).Success && adapter is not null;
                 index++)
            {
                adapters.Add(adapter);
            }
        }

        var hardware = new List<IDXGIAdapter1>(adapters.Count);
        foreach (var adapter in adapters)
        {
            var description = adapter.Description1;
            if ((description.Flags & AdapterFlags.Software) != 0)
            {
                probed.Add($"{description.Description}: software adapter — skipped in hardware pass");
                adapter.Dispose();
                continue;
            }

            hardware.Add(adapter);
        }

        return hardware;
    }

    /// <summary>
    ///     Walks the 12.x feature-level ladder on one adapter (null = the runtime's default) and
    ///     builds the full device bundle on the first level that takes. Returns null after
    ///     recording the failure in <paramref name="probed" />.
    /// </summary>
    private static GpuDevice12? TryCreateOnAdapter(
        IDXGIAdapter1? adapter,
        string? adapterName,
        FeatureLevel[] featureLevels,
        bool enableDebugLayer,
        bool enableDred,
        bool isSoftwareAdapter,
        List<string> probed)
    {
        foreach (var minLevel in featureLevels)
        {
            try
            {
                var result = Vortice.Direct3D12.D3D12.D3D12CreateDevice(
                    adapter,
                    minLevel,
                    out ID3D12Device? device);

                if (result.Failure || device is null)
                {
                    Log.Debug("GpuDevice12: D3D12CreateDevice({0}) failed at feature level {1}: {2}",
                        adapterName ?? "default adapter", minLevel, result);
                    device?.Dispose();
                    continue;
                }

                ID3D12CommandQueue? queue = null;
                ID3D12Fence? fence = null;
                try
                {
                    var queueDesc = new CommandQueueDescription(CommandListType.Direct, CommandQueuePriority.Normal);
                    queue = device.CreateCommandQueue<ID3D12CommandQueue>(queueDesc);
                    fence = device.CreateFence<ID3D12Fence>();

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
                                Log.Info(
                                    "GpuDevice12: ID3D12InfoQueue attached — validation messages will surface via PumpDebugMessages()");
                            }
                        }
                        catch (SharpGenException ex)
                        {
                            Log.Warn("GpuDevice12: failed to attach info queue: {0}", ex.Message);
                        }
                    }

                    var deviceName = adapterName ?? QueryAdapterDescription(device);
                    var sceneSampleCount = ProbeSceneSampleCount(device);
                    Log.Info("GpuDevice12: created Direct3D 12 device at {0} ({1}{2}); scene MSAA = {3}x",
                        minLevel, deviceName, isSoftwareAdapter ? ", WARP software rasterizer" : "",
                        sceneSampleCount);
                    return new GpuDevice12(device, queue, fence, deviceName, minLevel, infoQueue, enableDred,
                        sceneSampleCount, isSoftwareAdapter);
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
                Log.Warn("GpuDevice12: device init threw at feature level {0} on {1}: {2}",
                    minLevel, adapterName ?? "default adapter", ex.Message);
            }
        }

        probed.Add($"{adapterName ?? "default adapter"}: no 12_2/12_1/12_0 feature level accepted");
        return null;
    }

    /// <summary>
    ///     Probes 4x MSAA support for the scene's color + depth formats by attempting to create
    ///     tiny multisampled committed resources (the only API-certain check across Vortice
    ///     versions). Returns 4 on success, 1 on failure or when the process-scoped verification
    ///     override requests 1x. 4x MSAA on standard RT/depth formats is mandatory at D3D feature
    ///     level 11_0+ (this device requires 12_0), so 4 is the norm; the fallback covers exotic
    ///     adapters. One-time at device creation.
    /// </summary>
    private static int ProbeSceneSampleCount(ID3D12Device device)
    {
        var requested = ResolveRequestedSceneSampleCount(
            Environment.GetEnvironmentVariable(SceneSampleCountEnvironmentVariable));
        if (requested == 1)
        {
            Log.Info("GpuDevice12: scene MSAA disabled by {0}=1 for this process.",
                SceneSampleCountEnvironmentVariable);
            return 1;
        }

        const int desired = 4;
        try
        {
            using var colorProbe = device.CreateCommittedResource<ID3D12Resource>(
                HeapProperties.DefaultHeapProperties, HeapFlags.None,
                ResourceDescription.Texture2D(Format.B8G8R8A8_UNorm, 1, 1,
                    1, 1, desired, 0,
                    ResourceFlags.AllowRenderTarget),
                ResourceStates.RenderTarget);
            using var depthProbe = device.CreateCommittedResource<ID3D12Resource>(
                HeapProperties.DefaultHeapProperties, HeapFlags.None,
                ResourceDescription.Texture2D(Format.D32_Float, 1, 1,
                    1, 1, desired, 0,
                    ResourceFlags.AllowDepthStencil),
                ResourceStates.DepthWrite);
            return desired;
        }
        catch (SharpGenException ex)
        {
            Log.Warn("GpuDevice12: {0}x MSAA unsupported for scene formats ({1}); scene MSAA disabled.",
                desired, ex.Message);
            return 1;
        }
    }

    /// <summary>
    ///     Pure parser kept separate from device creation so the verification override is covered on
    ///     machines without a D3D12 adapter. Only the exact supported sample counts have meaning;
    ///     every other value preserves the normal 4x capability probe.
    /// </summary>
    internal static int ResolveRequestedSceneSampleCount(string? raw)
    {
        return string.Equals(raw?.Trim(), "1", StringComparison.Ordinal) ? 1 : 4;
    }

    private static string QueryAdapterDescription(ID3D12Device device)
    {
        try
        {
            var luid = new Luid((uint)(device.AdapterLuid & 0xFFFFFFFF), (int)(device.AdapterLuid >> 32));
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
