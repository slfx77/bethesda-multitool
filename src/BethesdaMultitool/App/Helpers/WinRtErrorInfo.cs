using System.Runtime.InteropServices;

namespace BethesdaMultitool;

/// <summary>
///     Formats the COM/WinRT failure detail the GUI crash writers append to their output. A stowed
///     0xC000027B crash carries its real cause in the COM HRESULT and the WinRT restricted-error
///     info, not in <see cref="Exception.Message" /> (which is often "The text associated with this
///     error code could not be found."). Every method here must be crash-handler safe: never throw,
///     never allocate more than trivially, never call back into WinRT.
/// </summary>
internal static class WinRtErrorInfo
{
    /// <summary>
    ///     One-line add-on for crash writers that do NOT already print a per-layer HRESULT:
    ///     "COMException HResult=0x…" plus the restricted-error description when present. Null
    ///     when the exception carries neither, so callers can skip the line entirely.
    /// </summary>
    public static string? Describe(Exception? ex)
    {
        if (ex is null)
        {
            return null;
        }

        try
        {
            string? comPart = ex is COMException com
                ? $"COMException HResult=0x{com.HResult:X8}"
                : null;
            var restricted = RestrictedDescription(ex);
            var restrictedPart = restricted is null ? null : $"RestrictedError: {restricted}";

            if (comPart is not null && restrictedPart is not null)
            {
                return comPart + " | " + restrictedPart;
            }

            return comPart ?? restrictedPart;
        }
        catch
        {
            // Crash writers must never throw; missing detail beats a recursive fault.
            return null;
        }
    }

    /// <summary>
    ///     The WinRT restricted-error info description attached to a marshaled WinRT failure, or
    ///     null. The CLR / C#-WinRT projection stashes IRestrictedErrorInfo's strings on the
    ///     exception's <see cref="Exception.Data" /> bag under well-known keys when a WinRT call
    ///     fails; reading the bag avoids re-querying COM from inside a crash handler.
    /// </summary>
    public static string? RestrictedDescription(Exception? ex)
    {
        if (ex is null)
        {
            return null;
        }

        try
        {
            // Description first (human text), error-reference second (correlation id) — either is
            // better than nothing when matching a log line to a WER stowed-exception record.
            foreach (var key in new[] { "RestrictedDescription", "RestrictedErrorReference" })
            {
                if (ex.Data.Contains(key) && ex.Data[key] is string value
                    && !string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return null;
        }
        catch
        {
            // Data can be a hostile custom IDictionary; a crash writer must survive it.
            return null;
        }
    }
}
