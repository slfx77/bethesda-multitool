namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Export;

/// <summary>
///     Controls when an export render pass pays for the resolve, tonemap, GPU-to-CPU copy, fence wait,
///     and CPU readback. A strictly-settled pass is eligible under every mode; the broader modes also
///     admit bounded fallbacks after the loose-complete grace or overall settle timeout expires.
/// </summary>
internal enum ExportTileCapturePolicy
{
    FullySettledOnly,
    CompleteOrFullySettled,
    Always
}

/// <summary>Pure capture decision shared by the UI render path and its non-Windows tests.</summary>
internal static class ExportTileCaptureDecision
{
    public static bool ShouldCapture(
        ExportTileCapturePolicy policy,
        bool isComplete,
        bool isFullySettled)
    {
        // Keep strict settlement independent of IsComplete so an inconsistent stats producer cannot
        // make a strictly-settled frame miss its only readback. Switch on policy first so an invalid
        // enum is rejected even when the status would otherwise be eligible.
        return policy switch
        {
            ExportTileCapturePolicy.FullySettledOnly => isFullySettled,
            ExportTileCapturePolicy.CompleteOrFullySettled => isFullySettled || isComplete,
            ExportTileCapturePolicy.Always => true,
            _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unknown export capture policy.")
        };
    }
}
