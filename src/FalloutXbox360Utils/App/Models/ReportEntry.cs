namespace FalloutXbox360Utils;

/// <summary>
///     Represents a generated report file in the Reports tab.
/// </summary>
public sealed class ReportEntry
{
    public string FileName { get; init; } = "";
    public string Content { get; init; } = "";
    public string ReportType { get; init; } = "";

    public string SizeFormatted => Content.Length switch
    {
        >= 1024 * 1024 => $"{Content.Length / (1024.0 * 1024.0):F1} MB",
        >= 1024 => $"{Content.Length / 1024.0:F1} KB",
        _ => $"{Content.Length} B"
    };
}
