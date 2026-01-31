namespace FalloutXbox360Utils.Core.Formats.Bsa;

/// <summary>
///     Result of extracting a single file from BSA.
/// </summary>
public record BsaExtractResult
{
    public required string SourcePath { get; init; }
    public required string OutputPath { get; init; }
    public required bool Success { get; init; }
    public required long OriginalSize { get; init; }
    public required long ExtractedSize { get; init; }
    public required bool WasCompressed { get; init; }
    public bool WasConverted { get; init; }
    public string? ConversionType { get; init; }
    public string? Error { get; init; }
}
