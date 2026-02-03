using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;

namespace FalloutXbox360Utils;

/// <summary>
///     Data model for NIF file entries in the list.
/// </summary>
public sealed class NifFileEntry : INotifyPropertyChanged
{
    // Brushes are created lazily on first access (which happens on UI thread via binding)
    private static SolidColorBrush? _grayBrush;
    private static SolidColorBrush? _greenBrush;
    private static SolidColorBrush? _orangeBrush;
    private static SolidColorBrush? _redBrush;
    private static SolidColorBrush? _yellowBrush;

    private string _formatDescription = "";
    private bool _isSelected = true;
    private string _status = "Pending";

    private static SolidColorBrush GrayBrush => _grayBrush ??= new SolidColorBrush(Colors.Gray);
    private static SolidColorBrush GreenBrush => _greenBrush ??= new SolidColorBrush(Colors.Green);
    private static SolidColorBrush OrangeBrush => _orangeBrush ??= new SolidColorBrush(Colors.Orange);
    private static SolidColorBrush RedBrush => _redBrush ??= new SolidColorBrush(Colors.Red);
    private static SolidColorBrush YellowBrush => _yellowBrush ??= new SolidColorBrush(Colors.Yellow);

    public required string FullPath { get; init; }
    public required string RelativePath { get; init; }
    public required long FileSize { get; init; }

    public string FileSizeDisplay => FileSize switch
    {
        < 1024 => $"{FileSize} B",
        < 1024 * 1024 => $"{FileSize / 1024.0:F1} KB",
        _ => $"{FileSize / (1024.0 * 1024.0):F2} MB"
    };

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged(nameof(IsSelected));
            }
        }
    }

    public string Status
    {
        get => _status;
        set
        {
            if (_status != value)
            {
                _status = value;
                OnPropertyChanged(nameof(Status));
                OnPropertyChanged(nameof(StatusColor)); // Computed from Status
            }
        }
    }

    /// <summary>
    ///     Computed color based on Status. Evaluated on UI thread when binding reads it.
    /// </summary>
    public SolidColorBrush StatusColor => _status switch
    {
        "Converted" => GreenBrush,
        "Converting..." => YellowBrush,
        "Cancelled" => OrangeBrush,
        "Skipped (exists)" or "Pending" => GrayBrush,
        _ when _status.StartsWith("Error", StringComparison.Ordinal) => RedBrush,
        _ when _status.StartsWith("Failed", StringComparison.Ordinal) => RedBrush,
        _ => GrayBrush
    };

    public string FormatDescription
    {
        get => _formatDescription;
        set
        {
            if (_formatDescription != value)
            {
                _formatDescription = value;
                OnPropertyChanged(nameof(FormatDescription));
                OnPropertyChanged(nameof(FormatColor)); // Computed from FormatDescription
            }
        }
    }

    /// <summary>
    ///     Computed color based on FormatDescription. Evaluated on UI thread when binding reads it.
    /// </summary>
    public SolidColorBrush FormatColor => _formatDescription switch
    {
        "Xbox 360 (BE)" => OrangeBrush,
        "PC (LE)" => GreenBrush,
        "Invalid" or "Error" => RedBrush,
        _ => GrayBrush
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
