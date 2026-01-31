namespace FalloutXbox360Utils.Core.Json;

/// <summary>
///     Information about a carved file.
/// </summary>
public sealed class JsonCarvedFileInfo
{
    public string? FileType { get; set; }
    public long Offset { get; set; }
    public long Length { get; set; }
    public string? FileName { get; set; }
}
