namespace FalloutXbox360Utils.Core.Formats.Subtitles;

/// <summary>
///     A single subtitle entry parsed from a transcriber CSV export.
/// </summary>
public sealed record SubtitleEntry(
    string? Text,
    string? Speaker,
    string? Quest,
    string? VoiceType,
    string? Source);