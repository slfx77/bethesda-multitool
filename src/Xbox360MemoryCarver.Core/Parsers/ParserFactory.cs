namespace Xbox360MemoryCarver.Core.Parsers;

/// <summary>
///     Factory for getting appropriate parser for a file type.
/// </summary>
public static class ParserFactory
{
    private static readonly Dictionary<string, IFileParser> Parsers = new()
    {
        ["dds"] = new DdsParser(),
        ["ddx_3xdo"] = new DdxParser(),
        ["ddx_3xdr"] = new DdxParser(),
        ["xma"] = new XmaParser(),
        ["nif"] = new NifParser(),
        ["png"] = new PngParser(),
        ["xex"] = new XexParser(),
        ["script_scn"] = new ScriptParser()
    };

    public static IFileParser? GetParser(string fileType)
    {
        return Parsers.TryGetValue(fileType, out var parser) ? parser : null;
    }
}
