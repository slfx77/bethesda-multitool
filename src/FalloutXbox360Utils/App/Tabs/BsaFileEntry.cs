using System.ComponentModel;
using FalloutXbox360Utils.Core.Formats.Bsa;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;

namespace FalloutXbox360Utils;

/// <summary>
///     Data model for BSA file entries in the list.
/// </summary>
public sealed class BsaFileEntry : INotifyPropertyChanged
{
    private static SolidColorBrush? _grayBrush;
    private static SolidColorBrush? _greenBrush;
    private static SolidColorBrush? _yellowBrush;
    private static SolidColorBrush? _blueBrush;
    private static SolidColorBrush? _redBrush;

    private bool _isSelected = true;
    private BsaExtractionStatus _status = BsaExtractionStatus.Pending;
    private string? _statusMessage;

    private static SolidColorBrush GrayBrush => _grayBrush ??= new SolidColorBrush(Colors.Gray);
    private static SolidColorBrush GreenBrush => _greenBrush ??= new SolidColorBrush(Colors.Green);
    private static SolidColorBrush YellowBrush => _yellowBrush ??= new SolidColorBrush(Colors.Yellow);
    private static SolidColorBrush BlueBrush => _blueBrush ??= new SolidColorBrush(Colors.DodgerBlue);
    private static SolidColorBrush RedBrush => _redBrush ??= new SolidColorBrush(Colors.OrangeRed);

    public required BsaFileRecord Record { get; init; }

    public string FullPath => Record.FullPath;
    public string FileName => Record.Name ?? $"unknown_{Record.NameHash:X16}";
    public string FolderPath => Record.Folder?.Name ?? "";
    public long Size => Record.Size;
    public bool IsCompressed { get; init; }

    public string SizeDisplay => Size switch
    {
        < 1024 => $"{Size} B",
        < 1024 * 1024 => $"{Size / 1024.0:F1} KB",
        _ => $"{Size / (1024.0 * 1024.0):F2} MB"
    };

    public string CompressedDisplay => IsCompressed ? "Yes" : "";
    public SolidColorBrush CompressedColor => IsCompressed ? GreenBrush : GrayBrush;

    public string Extension => Path.GetExtension(FileName).ToLowerInvariant();

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

    public BsaExtractionStatus Status
    {
        get => _status;
        set
        {
            if (_status != value)
            {
                _status = value;
                OnPropertyChanged(nameof(Status));
                OnPropertyChanged(nameof(StatusDisplay));
                OnPropertyChanged(nameof(StatusColor));
            }
        }
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        set
        {
            if (_statusMessage != value)
            {
                _statusMessage = value;
                OnPropertyChanged(nameof(StatusMessage));
                OnPropertyChanged(nameof(StatusDisplay));
            }
        }
    }

    public string StatusDisplay => _status switch
    {
        BsaExtractionStatus.Pending => "",
        BsaExtractionStatus.Extracting => "Extracting...",
        BsaExtractionStatus.Converting => "Converting...",
        BsaExtractionStatus.Done => _statusMessage ?? "Done",
        BsaExtractionStatus.Skipped => _statusMessage ?? "Skipped",
        BsaExtractionStatus.Failed => _statusMessage ?? "Failed",
        _ => ""
    };

    public SolidColorBrush StatusColor => _status switch
    {
        BsaExtractionStatus.Pending => GrayBrush,
        BsaExtractionStatus.Extracting => BlueBrush,
        BsaExtractionStatus.Converting => YellowBrush,
        BsaExtractionStatus.Done => GreenBrush,
        BsaExtractionStatus.Skipped => GrayBrush,
        BsaExtractionStatus.Failed => RedBrush,
        _ => GrayBrush
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
