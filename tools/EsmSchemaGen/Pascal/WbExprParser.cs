namespace EsmSchemaGen.Pascal;

/// <summary>
///     Recursive-descent parser over the token stream for a single xEdit builder expression — a
///     <c>wb*</c> call, a bracketed list, or a literal. It does not understand Pascal control flow;
///     callers feed it one self-contained <c>wbExpr</c> (e.g. a whole <c>wbRecord(...)</c> or the RHS
///     of an <c>ident := …</c> assignment). The grammar:
///     <code>
///     value     := list | str | num | '-' num | ident-or-call
///     idOrCall  := IDENT ('(' args ')')? modifier*        // call/symbol with optional fluent chain
///                | IDENT                                   // bare identifier value (itU32, nil, …)
///     list      := '[' (value (',' value)*)? ']'
///     args      := (value (',' value)*)?
///     modifier  := '.' IDENT ('(' args ')')?
///     </code>
/// </summary>
public sealed class WbExprParser
{
    private readonly List<Token> _tokens;
    private int _pos;

    private WbExprParser(List<Token> tokens) => _tokens = tokens;

    /// <summary>Parse a single expression from raw Pascal source text.</summary>
    public static WbValue Parse(string source)
    {
        var parser = new WbExprParser(PascalLexer.Tokenize(source));
        var value = parser.ParseValue();
        return value;
    }

    /// <summary>Parse from an existing token list, advancing a shared cursor (used by the file walker).</summary>
    public static WbValue ParseValue(List<Token> tokens, ref int pos)
    {
        var parser = new WbExprParser(tokens) { _pos = pos };
        var value = parser.ParseValue();
        pos = parser._pos;
        return value;
    }

    private Token Peek => _tokens[_pos];

    private Token Next() => _tokens[_pos++];

    private bool Is(TokenKind kind) => Peek.Kind == kind;

    private Token Expect(TokenKind kind)
    {
        if (Peek.Kind != kind)
        {
            throw new FormatException($"Expected {kind} but found {Peek.Kind} '{Peek.Text}' at token {_pos}.");
        }

        return Next();
    }

    private WbValue ParseValue()
    {
        switch (Peek.Kind)
        {
            case TokenKind.LBracket:
                return ParseList();
            case TokenKind.Str:
                return new WbStr(Next().Text);
            case TokenKind.Int:
            {
                var t = Next();
                return new WbNum(t.IntValue, false, t.IntValue);
            }
            case TokenKind.Float:
            {
                var t = Next();
                return new WbNum((long)t.FloatValue, true, t.FloatValue);
            }
            case TokenKind.Minus:
            {
                Next();
                var inner = ParseValue();
                return inner switch
                {
                    WbNum num => new WbNum(-num.IntValue, num.IsFloat, -num.FloatValue),
                    _ => inner
                };
            }
            case TokenKind.Ident:
                return ParseIdentOrCall();
            default:
                throw new FormatException($"Unexpected token {Peek.Kind} '{Peek.Text}' at {_pos}.");
        }
    }

    private WbValue ParseIdentOrCall()
    {
        var name = Next().Text;

        switch (name)
        {
            case "nil":
                return new WbNil();
            case "True":
                return new WbBool(true);
            case "False":
                return new WbBool(false);
        }

        IReadOnlyList<WbValue>? args = null;
        if (Is(TokenKind.LParen))
        {
            args = ParseArgs();
        }

        var modifiers = ParseModifiers();

        if (args is not null || modifiers.Count > 0)
        {
            return new WbCall(name, args ?? [], modifiers);
        }

        return new WbIdent(name);
    }

    private List<WbValue> ParseArgs()
    {
        Expect(TokenKind.LParen);
        var args = new List<WbValue>();
        if (!Is(TokenKind.RParen))
        {
            args.Add(ParseValue());
            while (Is(TokenKind.Comma))
            {
                Next();
                if (Is(TokenKind.RParen))
                {
                    break; // tolerate a trailing comma
                }

                args.Add(ParseValue());
            }
        }

        Expect(TokenKind.RParen);
        return args;
    }

    private List<WbModifier> ParseModifiers()
    {
        var modifiers = new List<WbModifier>();
        while (Is(TokenKind.Dot))
        {
            Next();
            var name = Expect(TokenKind.Ident).Text;
            IReadOnlyList<WbValue> args = Is(TokenKind.LParen) ? ParseModifierArgs() : [];
            modifiers.Add(new WbModifier(name, args));
        }

        return modifiers;
    }

    /// <summary>
    ///     Parse a fluent modifier's argument list, tolerating xEdit's procedural content — inline
    ///     anonymous <c>function(...) begin … end</c> callbacks (e.g. <c>.SetGetFormIDCallback</c>) and
    ///     operator expressions (e.g. <c>.IncludeFlag(dfX, not wbAllowEditHEDRVersion)</c>). Modifier
    ///     arguments are display/conflict/callback metadata the schema does not need, so on any parse
    ///     difficulty the args are discarded via a balanced-paren skip rather than failing the whole
    ///     record. Simple cases (e.g. <c>SetDefaultNativeValue(1)</c>) still parse cleanly, preserving
    ///     their values. Record/struct/member argument lists stay strict, so genuine bugs surface there.
    /// </summary>
    private List<WbValue> ParseModifierArgs()
    {
        var save = _pos;
        try
        {
            return ParseArgs();
        }
        catch (FormatException)
        {
            _pos = save;
            SkipBalancedParens();
            return [];
        }
    }

    private void SkipBalancedParens()
    {
        Expect(TokenKind.LParen);
        var depth = 1;
        while (depth > 0 && !Is(TokenKind.Eof))
        {
            switch (Next().Kind)
            {
                case TokenKind.LParen:
                    depth++;
                    break;
                case TokenKind.RParen:
                    depth--;
                    break;
            }
        }
    }

    private WbValue ParseList()
    {
        Expect(TokenKind.LBracket);
        var items = new List<WbValue>();
        if (!Is(TokenKind.RBracket))
        {
            items.Add(ParseValue());
            while (Is(TokenKind.Comma))
            {
                Next();
                if (Is(TokenKind.RBracket))
                {
                    break; // tolerate a trailing comma
                }

                items.Add(ParseValue());
            }
        }

        Expect(TokenKind.RBracket);
        return new WbList(items);
    }
}
