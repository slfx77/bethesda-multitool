namespace BethesdaRendererProfiler;

/// <summary>
///     Strict selector validation for unattended captures. A requested selector must round-trip to
///     the control's actual selection; silently capturing a climate/default scene produces plausible
///     but invalid parity evidence.
/// </summary>
internal static class CaptureSelectionGuard
{
    internal static bool TryValidate(
        string selectorKind,
        string? requested,
        string? actual,
        out string? error)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            error = null;
            return true;
        }

        var normalizedRequest = requested.Trim();
        if (string.Equals(normalizedRequest, actual?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            error = null;
            return true;
        }

        error = $"[Capture] FAILED: requested {selectorKind} '{normalizedRequest}', " +
                $"but the actual {selectorKind} is '{ActualLabel(actual)}'.";
        return false;
    }

    private static string ActualLabel(string? actual) =>
        string.IsNullOrWhiteSpace(actual) ? "(none)" : actual.Trim();
}
