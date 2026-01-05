using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;

namespace Xbox360MemoryCarver.App;

/// <summary>
///     Represents a dump file in the batch list.
/// </summary>
public sealed class DumpFileEntry : INotifyPropertyChanged
{
    private bool _isSelected;
    private string _status = "Pending";

    public string FilePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public long Size { get; set; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
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
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusColor)));
            }
        }
    }

    public string SizeFormatted => Size switch
    {
        >= 1024 * 1024 * 1024 => $"{Size / (1024.0 * 1024.0 * 1024.0):F2} GB",
        >= 1024 * 1024 => $"{Size / (1024.0 * 1024.0):F2} MB",
        >= 1024 => $"{Size / 1024.0:F2} KB",
        _ => $"{Size} B"
    };

    public Brush StatusColor => Status switch
    {
        "Complete" => new SolidColorBrush(Colors.Green),
        "Skipped" => new SolidColorBrush(Colors.Gray),
        "Processing..." => new SolidColorBrush(Colors.Blue),
        _ when Status.StartsWith("Error", StringComparison.Ordinal) => new SolidColorBrush(Colors.Red),
        _ => new SolidColorBrush(Colors.Gray)
    };

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
///     Sort columns for the batch dump files list.
/// </summary>
public enum BatchSortColumn
{
    None,
    Filename,
    Size,
    Status
}
