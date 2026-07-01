using EsmSchemaGen.Ir;
using EsmSchemaGen.Pascal;

namespace EsmSchemaGen;

/// <summary>
///     Walks a whole <c>wbDefinitions*.pas</c> file, lowering its top-level statements into IR. Two
///     statement shapes matter (everything else — interface decls, function bodies, control flow — is
///     skipped): a builder-symbol assignment <c>wbName := wbExpr;</c> (recorded in the
///     <see cref="IrBuilder" /> symbol table so later references resolve) and a record declaration
///     <c>wbRecord(...);</c>. Only paren-depth-0 statements are considered, so procedural content nested
///     inside a record's modifier callbacks (e.g. <c>.SetAfterLoad(function … begin … end)</c>) is
///     consumed by the record sub-parse and never mistaken for a top-level statement.
/// </summary>
public sealed class DefinitionsFileParser
{
    /// <summary>The IR builder, carrying the accumulated symbol table and unknown-builder tally.</summary>
    public IrBuilder Builder { get; } = new();

    /// <summary>Records lowered from this file, in declaration order.</summary>
    public List<RecordDef> Records { get; } = [];

    /// <summary>Top-level <c>wbRecord</c> statements that failed to parse (should trend to zero).</summary>
    public int ParseFailures { get; private set; }

    /// <summary>
    ///     Build a parser for a per-game definitions file, first loading the sibling
    ///     <c>wbDefinitionsCommon.pas</c> (when present) so the game file's references to shared symbols
    ///     resolve. Calling <see cref="ParseFile" /> repeatedly accumulates into one symbol table.
    /// </summary>
    public static DefinitionsFileParser LoadWithCommon(string definitionsPath)
    {
        var parser = new DefinitionsFileParser();
        var dir = Path.GetDirectoryName(Path.GetFullPath(definitionsPath));
        var fileName = Path.GetFileName(definitionsPath);

        // Some engine structs changed shape at Fallout 4 (e.g. LEVELED_OBJECT's tail), which the xEdit source
        // encodes as IsFO4Plus(...). The definitions filename carries the game (wbDefinitionsFO4.pas /
        // wbDefinitionsFO76.pas), so derive the FO4+ bit once here and let CommonHelpers pick the right variant.
        var suffix = Path.GetFileNameWithoutExtension(fileName)
            .Replace("wbDefinitions", "", StringComparison.OrdinalIgnoreCase);
        parser.Builder.IsFo4Plus = suffix is "FO4" or "FO76";
        if (dir is not null && !fileName.Equals("wbDefinitionsCommon.pas", StringComparison.OrdinalIgnoreCase))
        {
            var common = Path.Combine(dir, "wbDefinitionsCommon.pas");
            if (File.Exists(common))
            {
                parser.ParseFile(File.ReadAllText(common));
            }
        }

        parser.ParseFile(File.ReadAllText(definitionsPath));
        return parser;
    }

    public void ParseFile(string source)
    {
        var tokens = PascalLexer.Tokenize(source);
        var pos = 0;
        var depth = 0;

        while (tokens[pos].Kind != TokenKind.Eof)
        {
            var token = tokens[pos];

            switch (token.Kind)
            {
                case TokenKind.LParen:
                case TokenKind.LBracket:
                    depth++;
                    pos++;
                    continue;
                case TokenKind.RParen:
                case TokenKind.RBracket:
                    if (depth > 0)
                    {
                        depth--;
                    }

                    pos++;
                    continue;
            }

            if (depth == 0 && token.Kind == TokenKind.Ident)
            {
                if (token.Text == "wbRecord" && tokens[pos + 1].Kind == TokenKind.LParen)
                {
                    var p = pos;
                    try
                    {
                        if (WbExprParser.ParseValue(tokens, ref p) is WbCall { Name: "wbRecord" } call)
                        {
                            Records.Add(Builder.BuildRecord(call));
                        }

                        pos = p;
                        continue;
                    }
                    catch (FormatException)
                    {
                        ParseFailures++;
                    }
                }
                else if (token.Text.StartsWith("wb", StringComparison.Ordinal) &&
                         tokens[pos + 1].Kind == TokenKind.Assign)
                {
                    var p = pos + 2; // skip the identifier and ':='
                    try
                    {
                        var rhs = WbExprParser.ParseValue(tokens, ref p);
                        Builder.DefineSymbol(token.Text, rhs);
                        pos = p;
                        continue;
                    }
                    catch (FormatException)
                    {
                        // Not a builder assignment we can model (e.g. an expression RHS) — leave it.
                    }
                }
            }

            pos++;
        }
    }
}
