using Microsoft.UI.Xaml.Data;

namespace BethesdaAudioTranscriber.Controls;

/// <summary>
///     Formats a slider value in seconds as a m:ss (or h:mm:ss) timestamp.
///     Used as the seek slider's thumb tooltip converter.
/// </summary>
public sealed class SecondsToTimestampConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var seconds = value is double d && !double.IsNaN(d) ? d : 0;
        return Format(TimeSpan.FromSeconds(seconds));
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }

    /// <summary>Format a duration as m:ss, or h:mm:ss for hour-long audio.</summary>
    public static string Format(TimeSpan ts)
    {
        return ts.Hours > 0
            ? $"{ts.Hours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes}:{ts.Seconds:D2}";
    }
}
