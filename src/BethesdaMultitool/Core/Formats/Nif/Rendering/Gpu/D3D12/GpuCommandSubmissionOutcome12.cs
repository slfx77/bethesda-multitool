using System.Runtime.ExceptionServices;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     Exact CPU-side outcome of ending one command list. A failed outcome can still require GPU
///     lifetime retention when ExecuteCommandLists was reached before the failure.
/// </summary>
internal readonly record struct GpuCommandSubmissionOutcome12(
    bool Succeeded,
    bool CommandListMayHaveReachedQueue,
    ulong FenceValue,
    Exception? Error)
{
    /// <summary>Records successful execution and the exact signal covering its resource use.</summary>
    /// <param name="fenceValue">Positive native completion signal associated with this frame.</param>
    internal static GpuCommandSubmissionOutcome12 Success(ulong fenceValue)
    {
        return new GpuCommandSubmissionOutcome12(true, true, fenceValue, null);
    }

    /// <summary>Preserves whether failed native submission may have consumed the recorded resources.</summary>
    /// <param name="commandListMayHaveReachedQueue">Whether execution cannot safely be ruled out.</param>
    /// <param name="error">Original submission error retained for the caller.</param>
    internal static GpuCommandSubmissionOutcome12 Failure(
        bool commandListMayHaveReachedQueue,
        Exception error)
    {
        return new GpuCommandSubmissionOutcome12(false, commandListMayHaveReachedQueue, 0, error);
    }

    /// <summary>Rethrows the original submission error with its captured dispatch information.</summary>
    internal void ThrowIfFailed()
    {
        if (Error is not null)
        {
            ExceptionDispatchInfo.Capture(Error).Throw();
        }
    }
}
