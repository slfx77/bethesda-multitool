namespace FalloutXbox360Utils;

internal sealed record LoadOrderDialogOptions
{
    public required string Title { get; init; }
    public required string IntroText { get; init; }
    public string[] AllowedExtensions { get; init; } = [".esm", ".esp", ".dmp"];
    public bool AllowSubtitleCsv { get; init; }
    public string? SubtitleLabel { get; init; }
    public string? SubtitlePlaceholder { get; init; }
    public string? SubtitleCsvPath { get; init; }
    public string? PrimaryFilePath { get; init; }
}
