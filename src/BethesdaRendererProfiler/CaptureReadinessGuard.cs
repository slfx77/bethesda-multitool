namespace BethesdaRendererProfiler;

/// <summary>Acceptance decision for an unattended one-shot capture after its settle window.</summary>
internal static class CaptureReadinessGuard
{
    internal static bool TryValidateStreamingQuiesced(
        bool quiesced,
        TimeSpan timeout,
        out string? error)
    {
        if (quiesced)
        {
            error = null;
            return true;
        }

        error = $"[Capture] FAILED: reference streaming did not quiesce within " +
                $"{timeout.TotalSeconds:0.###} seconds; no acceptance image was saved.";
        return false;
    }
}
