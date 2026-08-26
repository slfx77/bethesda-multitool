using System.Runtime.InteropServices;
using BethesdaMultitool.Core.Diagnostics;
using Microsoft.Win32.SafeHandles;
using SharpGen.Runtime;
using Vortice.DXGI;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     Holds a DXGI video-memory budget-change registration and lets the render thread ask, without
///     blocking, whether the OS has changed our budget since it last asked.
///     <para>
///         The notification answers a question polling cannot: our budget is the OS's to reassign,
///         and it is cut the moment another application wants the adapter. Nothing about our own
///         allocations changes at that instant, so a usage poll sees nothing — the denominator moved,
///         not the numerator.
///     </para>
///     <para>
///         DXGI's contract here is a Win32 event handle rather than a callback, which removes the
///         hazard the callback form would carry: no OS-owned thread ever runs our code, so there is
///         no cross-thread flag to publish and no reentrancy to reason about. Consuming a
///         notification is a zero-timeout native wait on that handle — see
///         <see cref="TryConsumeBudgetChanged" /> for why it has to be the native one.
///     </para>
/// </summary>
internal sealed class GpuVideoMemoryBudgetWatcher : IDisposable
{
    private const uint WaitObject0 = 0;

    private static readonly Logger Log = Logger.Instance;

    private readonly IDXGIAdapter3 _adapter;
    private readonly AutoResetEvent _signal;
    private readonly SafeWaitHandle _handle;
    private readonly IntPtr _rawHandle;
    private readonly int _cookie;
    private bool _disposed;

    private GpuVideoMemoryBudgetWatcher(
        IDXGIAdapter3 adapter, AutoResetEvent signal, SafeWaitHandle handle, IntPtr rawHandle, int cookie)
    {
        _adapter = adapter;
        _signal = signal;
        _handle = handle;
        _rawHandle = rawHandle;
        _cookie = cookie;
    }

    /// <summary>Budget changes observed since creation. Diagnostics only.</summary>
    public long NotificationCount { get; private set; }

    /// <summary>
    ///     True exactly once per budget change. Auto-reset, so a caller that consumes the
    ///     notification owns it — two consumers would each see half of them.
    ///     <para>
    ///         A raw <c>WaitForSingleObject</c> with a zero timeout, never
    ///         <see cref="WaitHandle.WaitOne()" />, for the same reason as
    ///         <see cref="D3D12FenceWaiter" />: this runs inside the CompositionTarget.Rendering tick
    ///         on the STA UI thread, where the CLR turns any managed wait into a COM pumping wait —
    ///         <b>including a zero-timeout one</b>. The pump can dispatch a pointer message into
    ///         XAML mid-tick and <c>CXcpDispatcher::CheckReentrancy</c> fail-fasts the process
    ///         (0xc000027b), bypassing every managed handler. A zero timeout makes this cheap; it
    ///         does not make it safe.
    ///     </para>
    /// </summary>
    public bool TryConsumeBudgetChanged()
    {
        if (_disposed)
        {
            return false;
        }

        // non-pumping: raw WaitForSingleObject, zero timeout — see the remarks above.
        if (WaitForSingleObject(_rawHandle, 0) != WaitObject0)
        {
            return false;
        }

        NotificationCount++;
        return true;
    }

    /// <summary>
    ///     Registers <paramref name="adapter" /> for budget-change notifications. The adapter stays
    ///     owned by <see cref="GpuDevice12" />; this type borrows it for the lifetime of the
    ///     registration and never disposes it.
    /// </summary>
    public static bool TryCreate(IDXGIAdapter3 adapter, out GpuVideoMemoryBudgetWatcher? watcher)
    {
        watcher = null;
        AutoResetEvent? signal = null;
        SafeWaitHandle? handle = null;
        var addedRef = false;
        try
        {
            signal = new AutoResetEvent(false);
            handle = signal.SafeWaitHandle;

            // Hold a reference count for as long as DXGI holds the raw handle. Without it, anything
            // that disposed the event — including finalization if this object were ever dropped
            // without Dispose — would close a handle the OS is still registered to signal, which is
            // a use-after-free on a kernel object rather than anything the CLR would catch. The
            // matching DangerousRelease is in Dispose, after the unregister.
            handle.DangerousAddRef(ref addedRef);
#pragma warning disable S3869 // DXGI takes a raw HANDLE; the DangerousAddRef above is the mitigation
            var rawHandle = handle.DangerousGetHandle();
#pragma warning restore S3869
            var cookie = adapter.RegisterVideoMemoryBudgetChangeNotificationEvent(rawHandle);
            watcher = new GpuVideoMemoryBudgetWatcher(adapter, signal, handle, rawHandle, cookie);
            addedRef = false; // ownership of the reference count moves to the watcher
            return true;
        }
        catch (SharpGenException ex)
        {
            // Not fatal and not rare: the registration is unavailable on the same adapters that
            // cannot report a budget at all. The signal degrades to polling, which is why polling
            // is not optional.
            Log.Debug("GpuVideoMemoryBudgetWatcher: registration unavailable ({0}).", ex.Message);
            return false;
        }
        finally
        {
            // Only runs when the registration did NOT take ownership.
            if (addedRef)
            {
                handle!.DangerousRelease();
            }

            if (watcher is null)
            {
                signal?.Dispose();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            // Unregister BEFORE the handle is closed. The other order leaves DXGI holding a handle
            // it may signal after we have freed it, which is a use-after-free in the kernel object
            // table rather than anything the CLR would catch.
            _adapter.UnregisterVideoMemoryBudgetChangeNotification(_cookie);
        }
        catch (SharpGenException ex)
        {
            Log.Debug("GpuVideoMemoryBudgetWatcher: unregister failed ({0}).", ex.Message);
        }

        // Release the count taken in TryCreate, then the event itself. DXGI no longer holds the
        // handle by this point, so the order here is unregister -> release -> dispose.
        _handle.DangerousRelease();
        _signal.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
}
