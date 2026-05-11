using FalloutXbox360Utils.Core.Formats.Esm.Reporting;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;

namespace FalloutXbox360Utils;

/// <summary>
///     ListView row view-model for an entry in the DMP→ESP conversion progress log.
///     Immutable: a new entry is created per emitted event; the ListView's ItemsSource is
///     replaced wholesale on each batch tick rather than mutated in place.
/// </summary>
public sealed class ConversionEventEntry
{
    private static SolidColorBrush? _grayBrush;
    private static SolidColorBrush? _yellowBrush;
    private static SolidColorBrush? _redBrush;
    private static SolidColorBrush? _blueBrush;

    private static SolidColorBrush GrayBrush => _grayBrush ??= new SolidColorBrush(Colors.Gray);
    private static SolidColorBrush YellowBrush => _yellowBrush ??= new SolidColorBrush(Colors.Goldenrod);
    private static SolidColorBrush RedBrush => _redBrush ??= new SolidColorBrush(Colors.IndianRed);
    private static SolidColorBrush BlueBrush => _blueBrush ??= new SolidColorBrush(Colors.DodgerBlue);

    public required DateTimeOffset Timestamp { get; init; }
    public required ConversionEventSeverity Severity { get; init; }
    public required string Phase { get; init; }
    public string? FormType { get; init; }
    public uint? FormId { get; init; }
    public required string Message { get; init; }

    public string TimeDisplay => Timestamp.ToString("HH:mm:ss.fff");

    public string SeverityLabel => Severity switch
    {
        ConversionEventSeverity.Info => "INFO",
        ConversionEventSeverity.Decision => "DEC",
        ConversionEventSeverity.Warning => "WARN",
        ConversionEventSeverity.Error => "ERR",
        _ => Severity.ToString()
    };

    public string SeverityGlyph => Severity switch
    {
        ConversionEventSeverity.Info => "",      // information
        ConversionEventSeverity.Decision => "",  // shapes (path)
        ConversionEventSeverity.Warning => "",   // warning triangle
        ConversionEventSeverity.Error => "",     // error circle
        _ => ""
    };

    public SolidColorBrush SeverityColor => Severity switch
    {
        ConversionEventSeverity.Warning => YellowBrush,
        ConversionEventSeverity.Error => RedBrush,
        ConversionEventSeverity.Decision => BlueBrush,
        _ => GrayBrush
    };

    public string FormIdDisplay => FormId.HasValue ? $"0x{FormId.Value:X8}" : "";

    public string FormTypeDisplay => FormType ?? "";

    public static ConversionEventEntry FromDomain(ConversionProgressEvent evt)
    {
        return new ConversionEventEntry
        {
            Timestamp = evt.Timestamp,
            Severity = evt.Severity,
            Phase = evt.Phase,
            FormType = evt.FormType,
            FormId = evt.FormId,
            Message = evt.Message
        };
    }
}
