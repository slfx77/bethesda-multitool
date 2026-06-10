using SharpGen.Runtime;
using Vortice.Direct3D12;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     Blocks the calling thread until a D3D12 <see cref="ID3D12Fence" /> reaches a target value,
///     using a Win32 auto-reset event as the completion signal.
///     <para>
///         Centralizes the <c>SetEventOnCompletion</c> + <c>WaitOne</c> dance every D3D12 renderer
///         needs. The event's <c>SafeWaitHandle</c> is AddRef'd around the native call so the raw
///         handle handed to <c>SetEventOnCompletion</c> cannot be reclaimed mid-wait — the
///         by-the-book <see cref="System.Runtime.InteropServices.SafeHandle" /> contract that a bare
///         <c>DangerousGetHandle()</c> skips.
///     </para>
/// </summary>
internal static class D3D12FenceWaiter
{
    /// <summary>
    ///     Returns immediately when <paramref name="value" /> is 0 or already reached; otherwise
    ///     registers <paramref name="waitEvent" /> with the fence and blocks until it signals.
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
            fence.SetEventOnCompletion(value, safeHandle.DangerousGetHandle()).CheckError();
#pragma warning restore S3869
            waitEvent.WaitOne();
        }
        finally
        {
            if (refAdded)
            {
                safeHandle.DangerousRelease();
            }
        }
    }
}
