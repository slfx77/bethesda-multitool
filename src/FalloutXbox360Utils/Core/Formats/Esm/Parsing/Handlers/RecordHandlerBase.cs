namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing;

/// <summary>
///     Base class for ESM record parsing handlers.
///     Provides shared access to the parsing context.
/// </summary>
internal abstract class RecordHandlerBase(RecordParserContext context)
{
    protected readonly RecordParserContext Context = context;
}
