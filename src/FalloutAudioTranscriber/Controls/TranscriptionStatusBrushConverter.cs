using FalloutAudioTranscriber.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace FalloutAudioTranscriber.Controls;

/// <summary>
///     Converts a TranscriptionStatus to a colored status indicator brush.
/// </summary>
public class TranscriptionStatusBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush GreenBrush = new(Colors.LimeGreen);
    private static readonly SolidColorBrush BlueBrush = new(Colors.DodgerBlue);
    private static readonly SolidColorBrush GoldBrush = new(Colors.Gold);
    private static readonly SolidColorBrush OrangeBrush = new(Colors.Orange);

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value is TranscriptionStatus status ? status switch
        {
            TranscriptionStatus.EsmSubtitle => GreenBrush,
            TranscriptionStatus.Accepted => BlueBrush,
            TranscriptionStatus.Automatic => GoldBrush,
            _ => OrangeBrush
        } : OrangeBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
