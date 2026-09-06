using System.Globalization;
using System.Text.RegularExpressions;

namespace BethesdaMultitool.Core.Media;

/// <summary>
///     Reads the elapsed-time marker out of one line of ffmpeg's stderr chatter.
///     <para>
///         ffmpeg has no machine-readable progress on stderr; it rewrites a status line that
///         contains <c>time=HH:MM:SS.ss</c>. Parsing that is the only way to drive a progress bar
///         without a second process. Kept separate from the runner so it can be tested against
///         real ffmpeg output without starting anything.
///     </para>
/// </summary>
internal static partial class FfmpegProgressParser
{
    [GeneratedRegex(@"time=(\d+):(\d{2}):(\d{2})\.(\d{1,3})", RegexOptions.CultureInvariant)]
    private static partial Regex TimeMarker();

    /// <summary>
    ///     The elapsed seconds reported by <paramref name="line" />, or <c>null</c> when the line
    ///     carries no time marker (most of them do not — banners, stream summaries, warnings).
    /// </summary>
    public static double? TryReadElapsedSeconds(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return null;
        }

        var match = TimeMarker().Match(line);
        if (!match.Success)
        {
            return null;
        }

        var hours = int.Parse(match.Groups[1].ValueSpan, CultureInfo.InvariantCulture);
        var minutes = int.Parse(match.Groups[2].ValueSpan, CultureInfo.InvariantCulture);
        var seconds = int.Parse(match.Groups[3].ValueSpan, CultureInfo.InvariantCulture);

        // The fraction is written with a variable number of digits, so scale by its own width
        // rather than assuming hundredths.
        var fractionText = match.Groups[4].ValueSpan;
        var fraction = int.Parse(fractionText, CultureInfo.InvariantCulture)
                       / Math.Pow(10, fractionText.Length);

        return (hours * 3600) + (minutes * 60) + seconds + fraction;
    }

    /// <summary>
    ///     Converts an elapsed time into a 0..1 fraction of <paramref name="totalSeconds" />,
    ///     or <c>null</c> when the total is unknown. Clamped, because ffmpeg's reported time can
    ///     overshoot a container's declared duration slightly.
    /// </summary>
    public static double? ToFraction(double elapsedSeconds, double totalSeconds)
    {
        if (totalSeconds <= 0 || double.IsNaN(totalSeconds) || double.IsInfinity(totalSeconds))
        {
            return null;
        }

        return Math.Clamp(elapsedSeconds / totalSeconds, 0d, 1d);
    }
}
