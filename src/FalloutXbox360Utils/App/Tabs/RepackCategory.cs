using System.Collections.ObjectModel;
using System.ComponentModel;
using FalloutXbox360Utils.Repack;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace FalloutXbox360Utils;

/// <summary>
///     Data model for repack category entries in the list.
/// </summary>
public sealed class RepackCategory : INotifyPropertyChanged
{
    private static SolidColorBrush? _grayBrush;
    private static SolidColorBrush? _greenBrush;
    private static SolidColorBrush? _blueBrush;
    private static SolidColorBrush? _redBrush;
    private int _fileCount;

    private bool _isEnabled = true;
    private bool _isExpanded;
    private RepackCategoryStatus _status = RepackCategoryStatus.Pending;
    private string? _statusMessage;

    private static SolidColorBrush GrayBrush => _grayBrush ??= new SolidColorBrush(Colors.Gray);
    private static SolidColorBrush GreenBrush => _greenBrush ??= new SolidColorBrush(Colors.Green);
    private static SolidColorBrush BlueBrush => _blueBrush ??= new SolidColorBrush(Colors.DodgerBlue);
    private static SolidColorBrush RedBrush => _redBrush ??= new SolidColorBrush(Colors.OrangeRed);

    public required string Name { get; init; }
    public required string Description { get; init; }
    public required RepackPhase Phase { get; init; }
    public bool IsAvailable { get; init; } = true;

    /// <summary>Sub-items for this category (e.g., individual BSA files).</summary>
    public ObservableCollection<RepackBsaEntry> SubItems { get; } = [];

    /// <summary>Whether this category has expandable sub-items.</summary>
    public bool HasSubItems => SubItems.Count > 0;

    /// <summary>Visibility for sub-items based on expansion state.</summary>
    public Visibility SubItemsVisibility =>
        _isExpanded && SubItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded != value)
            {
                _isExpanded = value;
                OnPropertyChanged(nameof(IsExpanded));
                OnPropertyChanged(nameof(SubItemsVisibility));
                OnPropertyChanged(nameof(ExpanderGlyph));
            }
        }
    }

    /// <summary>Glyph for expand/collapse indicator.</summary>
    public string ExpanderGlyph => _isExpanded ? "\uE70E" : "\uE70D"; // ChevronDown : ChevronRight

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled != value)
            {
                _isEnabled = value;
                OnPropertyChanged(nameof(IsEnabled));
            }
        }
    }

    public int FileCount
    {
        get => _fileCount;
        set
        {
            if (_fileCount != value)
            {
                _fileCount = value;
                OnPropertyChanged(nameof(FileCount));
                OnPropertyChanged(nameof(FileCountDisplay));
            }
        }
    }

    public string FileCountDisplay => _fileCount > 0 ? $"{_fileCount:N0}" : "0";

    public RepackCategoryStatus Status
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
        RepackCategoryStatus.Pending => "",
        RepackCategoryStatus.Processing => _statusMessage ?? "Processing...",
        RepackCategoryStatus.Complete => _statusMessage ?? "Complete",
        RepackCategoryStatus.Skipped => "Skipped",
        RepackCategoryStatus.Failed => _statusMessage ?? "Failed",
        _ => ""
    };

    public SolidColorBrush StatusColor => _status switch
    {
        RepackCategoryStatus.Pending => GrayBrush,
        RepackCategoryStatus.Processing => BlueBrush,
        RepackCategoryStatus.Complete => GreenBrush,
        RepackCategoryStatus.Skipped => GrayBrush,
        RepackCategoryStatus.Failed => RedBrush,
        _ => GrayBrush
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
