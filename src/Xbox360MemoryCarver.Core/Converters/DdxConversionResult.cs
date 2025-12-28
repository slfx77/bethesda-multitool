namespace Xbox360MemoryCarver.Core.Converters;

/// <summary>
///     Result of DDX to DDS conversion.
/// </summary>
public class DdxConversionResult
{
    public bool Success { get; init; }
    public byte[]? DdsData { get; init; }
    public byte[]? AtlasData { get; init; }
    public bool IsPartial { get; init; }
    public string? Notes { get; init; }
    public string? ConsoleOutput { get; init; }

    public static DdxConversionResult Failure(string notes)
    {
        return new DdxConversionResult { Success = false, Notes = notes };
    }
}
