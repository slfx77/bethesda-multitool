using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;

namespace BethesdaMultitool;

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
    ///     ESM record type (e.g., "NPC_", "WEAP", "PERK") for ESM data records.
    ///     Null for non-ESM carved files.
    /// </summary>
    public string? EsmRecordType { get; set; }

    /// <summary>
    ///     FormID for ESM records. Null for non-ESM carved files.
    /// </summary>
    public uint? FormId { get; set; }

    /// <summary>
    ///     Whether this entry represents an ESM record.
    /// </summary>
    public bool IsEsmRecord => EsmRecordType != null;

    /// <summary>
    ///     Set during analysis when the dump does not contain the file's whole declared length —
    ///     the run of memory starting at the match ends before the file does. Extraction promotes
    ///     this to <see cref="ExtractionStatus.Partial" /> rather than a plain success.
    /// </summary>
    public bool IsAnalysisTruncated { get; set; }

    /// <summary>
    ///     Display type - shows "ESM: NPC_" for ESM records, otherwise FileType.
    /// </summary>
    public string DisplayType => EsmRecordType != null ? $"ESM: {EsmRecordType}" : FileType;

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
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusDescription)));
            }
        }
    }

    // Legacy property for compatibility
    /// <summary>Legacy boolean view of <see cref="Status" />: true only when fully extracted.</summary>
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
            if (Length >= 1024 * 1024) return $"{Length / (1024.0 * 1024.0):F2} MB";

            if (Length >= 1024) return $"{Length / 1024.0:F2} KB";

            return $"{Length} B";
        }
    }

    /// <summary>Segoe MDL2 icon glyph reflecting the current extraction status.</summary>
    public string ExtractedGlyph => _status switch
    {
        ExtractionStatus.Extracted => "\uE73E", // Checkmark
        ExtractionStatus.Partial => "\uE7BA", // Warning - extracted but not fully resident
        ExtractionStatus.Failed => "\uE711", // X
        ExtractionStatus.Skipped => "\uE738", // Emdash - ESM record not in report
        _ => "\uE8FB" // More (horizontal dots) - pending/not extracted
    };

    /// <summary>Status indicator color (green extracted, amber partial, red failed, gray pending/skipped).</summary>
    public Brush ExtractedColor => _status switch
    {
        ExtractionStatus.Extracted => new SolidColorBrush(Colors.Green),
        ExtractionStatus.Partial => new SolidColorBrush(Colors.Orange),
        ExtractionStatus.Failed => new SolidColorBrush(Colors.Red),
        ExtractionStatus.Skipped => new SolidColorBrush(Colors.DarkGray),
        _ => new SolidColorBrush(Colors.Gray)
    };

    /// <summary>Screen-reader / tooltip text for the status glyph.</summary>
    public string StatusDescription => _status switch
    {
        ExtractionStatus.Extracted => "Extracted",
        ExtractionStatus.Partial => "Extracted, but part of the file was not captured in the dump",
        ExtractionStatus.Failed => "Extraction failed",
        ExtractionStatus.Skipped => "Skipped",
        _ => "Not extracted"
    };

    public event PropertyChangedEventHandler? PropertyChanged;
}
