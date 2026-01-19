namespace Xbox360MemoryCarver.Core.Converters;

/// <summary>
///     Result of a file format conversion operation.
/// </summary>
public class ConversionResult
{
    public bool Success { get; init; }
    public byte[]? OutputData { get; init; }
    public byte[]? AtlasData { get; init; }
    public bool IsPartial { get; init; }
    public string? Notes { get; init; }
    public string? ConsoleOutput { get; init; }

    public static ConversionResult Failure(string notes)
    {
        return new ConversionResult { Success = false, Notes = notes };
    }
}
