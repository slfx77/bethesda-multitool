namespace Xbox360MemoryCarver.Core.Parsers;

/// <summary>
///     Base interface for file parsers.
/// </summary>
public interface IFileParser
{
    ParseResult? ParseHeader(ReadOnlySpan<byte> data, int offset = 0);
}

/// <summary>
///     Result from parsing a file header.
/// </summary>
public class ParseResult
{
    public required string Format { get; init; }
    public int EstimatedSize { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int MipCount { get; init; }
    public string? FourCc { get; init; }
    public bool IsXbox360 { get; init; }
    public Dictionary<string, object> Metadata { get; init; } = [];
}
