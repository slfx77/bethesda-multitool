using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     Blocks the calling thread until a D3D12 <see cref="ID3D12Fence" /> reaches a target value,
///     using a Win32 auto-reset event as the completion signal.
///     <para>
///         Centralizes the <c>SetEventOnCompletion</c> + wait dance every D3D12 renderer
///         needs. The event's <c>SafeWaitHandle</c> is AddRef'd around the native call so the raw
///         handle handed to <c>SetEventOnCompletion</c> cannot be reclaimed mid-wait — the
///         by-the-book <see cref="System.Runtime.InteropServices.SafeHandle" /> contract that a bare
///         <c>DangerousGetHandle()</c> skips.
///     </para>
///     <para>
///         The block itself MUST be a raw <c>WaitForSingleObject</c>, never
///         <see cref="WaitHandle.WaitOne()" />: on an STA thread (the XAML UI thread, which calls
///         this from the CompositionTarget.Rendering tick) the CLR turns managed waits into a COM
///         pumping wait (<c>CoWaitForMultipleHandles</c>). The pump can dispatch a pending pointer
///         message into XAML while the dispatcher is still inside the protected render tick, and
///         <c>CXcpDispatcher::CheckReentrancy</c> fail-fasts the process with a stowed exception
///         (0xc000027b / 0x8000FFFF). Confirmed in BethesdaMultitool.exe.31500.dmp.
///     </para>
/// </summary>
internal static class D3D12FenceWaiter
{
    private const uint Infinite = 0xFFFFFFFF;
    private const uint WaitObject0 = 0;

    /// <summary>
    ///     Returns immediately when <paramref name="value" /> is 0 or already reached; otherwise
    ///     registers <paramref name="waitEvent" /> with the fence and blocks (without pumping
    ///     messages) until it signals.
    /// </summary>
    public static void WaitForFence(ID3D12Fence fence, ulong value, AutoResetEvent waitEvent)
    {
        if (value == 0 || fence.CompletedValue >= value)
        {
            return;
        }

        var safeHandle = waitEvent.SafeWaitHandle;
        var refAdded = false;
        try
        {
            safeHandle.DangerousAddRef(ref refAdded);
            // The native SetEventOnCompletion takes a raw HANDLE (IntPtr), so DangerousGetHandle is
            // unavoidable. It's made safe by the DangerousAddRef above (released in the finally), which
            // pins the handle for the duration of the call — the contract S3869 exists to enforce. This
            // single, audited site replaces the seven bare DangerousGetHandle calls it used to wrap.
#pragma warning disable S3869
            var rawHandle = safeHandle.DangerousGetHandle();
            fence.SetEventOnCompletion(value, rawHandle).CheckError();
#pragma warning restore S3869
            var result = WaitForSingleObject(rawHandle, Infinite);
            if (result != WaitObject0)
            {
                throw new InvalidOperationException(
                    $"D3D12 fence wait failed: WaitForSingleObject returned 0x{result:X8} (last error {Marshal.GetLastWin32Error()}).");
            }
        }
        finally
        {
            if (refAdded)
            {
                safeHandle.DangerousRelease();
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
}
