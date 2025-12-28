using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;

namespace Xbox360MemoryCarver.App;

/// <summary>
///     Represents a carved file entry in the results table.
/// </summary>
public sealed class CarvedFileEntry : INotifyPropertyChanged
{
    private ExtractionStatus _status = ExtractionStatus.NotExtracted;

    public long Offset { get; set; }
    public long Length { get; set; }
    public string FileType { get; set; } = "";
    public string? FileName { get; set; }

    /// <summary>
    ///     Gets a display name - filename if available, otherwise the file type.
    /// </summary>
    public string DisplayName => !string.IsNullOrEmpty(FileName) ? FileName : FileType;

    /// <summary>
    ///     Gets the filename for display, or empty string if none.
    /// </summary>
    public string FileNameDisplay => FileName ?? "";

    public ExtractionStatus Status
    {
        get => _status;
        set
        {
            if (_status != value)
            {
                _status = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExtractedGlyph)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExtractedColor)));
            }
        }
    }

    // Legacy property for compatibility
    public bool IsExtracted
    {
        get => _status == ExtractionStatus.Extracted;
        set => Status = value ? ExtractionStatus.Extracted : ExtractionStatus.NotExtracted;
    }

    public string OffsetHex => $"0x{Offset:X8}";

    public string LengthFormatted
    {
        get
        {
            if (Length >= 1024 * 1024)
            {
                return $"{Length / (1024.0 * 1024.0):F2} MB";
            }

            if (Length >= 1024)
            {
                return $"{Length / 1024.0:F2} KB";
            }

            return $"{Length} B";
        }
    }

    public string ExtractedGlyph => _status switch
    {
        ExtractionStatus.Extracted => "\uE73E", // Checkmark
        ExtractionStatus.Failed => "\uE711", // X
        _ => "\uE8FB" // More (horizontal dots) - pending/not extracted
    };

    public Brush ExtractedColor => _status switch
    {
        ExtractionStatus.Extracted => new SolidColorBrush(Colors.Green),
        ExtractionStatus.Failed => new SolidColorBrush(Colors.Red),
        _ => new SolidColorBrush(Colors.Gray)
    };

    public event PropertyChangedEventHandler? PropertyChanged;
}

public enum ExtractionStatus
{
    NotExtracted,
    Extracted,
    Failed
}
