using Xbox360MemoryCarver.Core.Parsers;

namespace Xbox360MemoryCarver.Core.FileTypes;

/// <summary>
///     Provides parsers for file types based on their definitions.
/// </summary>
public static class ParserRegistry
{
    private static readonly Dictionary<Type, IFileParser> _parserInstances = [];
    private static readonly object _lock = new();

    /// <summary>
    ///     Gets the parser for a file type definition.
    /// </summary>
    public static IFileParser? GetParser(FileTypeDefinition? typeDef)
    {
        if (typeDef?.ParserType == null) return null;

        return GetOrCreateParser(typeDef.ParserType);
    }

    /// <summary>
    ///     Gets the parser for a signature ID.
    /// </summary>
    public static IFileParser? GetParserForSignature(string signatureId)
    {
        var typeDef = FileTypeRegistry.GetBySignatureId(signatureId);
        return GetParser(typeDef);
    }

    private static IFileParser? GetOrCreateParser(Type parserType)
    {
        lock (_lock)
        {
            if (_parserInstances.TryGetValue(parserType, out var existing)) return existing;

            if (Activator.CreateInstance(parserType) is IFileParser parser)
            {
                _parserInstances[parserType] = parser;
                return parser;
            }

            return null;
        }
    }
}
