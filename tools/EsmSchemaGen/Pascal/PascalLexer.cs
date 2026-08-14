using System.Globalization;
using System.Text;

namespace EsmSchemaGen.Pascal;

public enum TokenKind
{
    Ident,
    Str,
    Int,
    Float,
    LParen,
    RParen,
    LBracket,
    RBracket,
    Comma,
    Dot,
    Semicolon,
    Assign,
    Minus,
    Slash,
    Eof
}

/// <summary>One lexical token. <see cref="IntValue" />/<see cref="FloatValue" /> are set for numbers.</summary>
public readonly record struct Token(TokenKind Kind, string Text, long IntValue = 0, double FloatValue = 0)
{
    public override string ToString() => $"{Kind}:{Text}";
}

/// <summary>
///     Tokenizer for the constrained subset of Object Pascal used by xEdit's <c>wbDefinitions*.pas</c>
///     builder DSL. It is NOT a full Pascal lexer — it only recognizes what the <c>wb*</c> call tree
///     needs: identifiers, single-quoted strings (<c>''</c> escape), decimal/hex (<c>$</c>) integers,
///     floats, and the handful of punctuation tokens. Comments (<c>// …</c>, <c>{ … }</c> incl.
///     <c>{$…}</c> directives, <c>(* … *)</c>) are skipped. Unary <c>-</c> and the numeric division
///     expressions used as xEdit display metadata (for example <c>1/24</c> and <c>180/pi</c>) are modeled;
///     other Pascal operators are outside this constrained grammar.
/// </summary>
public static class PascalLexer
{
    public static List<Token> Tokenize(string src)
    {
        var tokens = new List<Token>();
        var i = 0;
        var n = src.Length;

        while (i < n)
        {
            var c = src[i];

            // Whitespace
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            // Line comment
            if (c == '/' && i + 1 < n && src[i + 1] == '/')
            {
                while (i < n && src[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            // Brace comment / compiler directive { ... }
            if (c == '{')
            {
                while (i < n && src[i] != '}')
                {
                    i++;
                }

                i++; // consume '}'
                continue;
            }

            // Paren-star comment (* ... *)
            if (c == '(' && i + 1 < n && src[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < n && !(src[i] == '*' && src[i + 1] == ')'))
                {
                    i++;
                }

                i += 2; // consume '*)'
                continue;
            }

            switch (c)
            {
                case '(':
                    tokens.Add(new Token(TokenKind.LParen, "("));
                    i++;
                    continue;
                case ')':
                    tokens.Add(new Token(TokenKind.RParen, ")"));
                    i++;
                    continue;
                case '[':
                    tokens.Add(new Token(TokenKind.LBracket, "["));
                    i++;
                    continue;
                case ']':
                    tokens.Add(new Token(TokenKind.RBracket, "]"));
                    i++;
                    continue;
                case ',':
                    tokens.Add(new Token(TokenKind.Comma, ","));
                    i++;
                    continue;
                case ';':
                    tokens.Add(new Token(TokenKind.Semicolon, ";"));
                    i++;
                    continue;
                case '-':
                    tokens.Add(new Token(TokenKind.Minus, "-"));
                    i++;
                    continue;
                case '/':
                    tokens.Add(new Token(TokenKind.Slash, "/"));
                    i++;
                    continue;
            }

            // Assignment ':='  (a lone ':' is not used in the DSL we parse)
            if (c == ':' && i + 1 < n && src[i + 1] == '=')
            {
                tokens.Add(new Token(TokenKind.Assign, ":="));
                i += 2;
                continue;
            }

            // '.' — but only as member access; we never see float starting with '.'
            if (c == '.')
            {
                tokens.Add(new Token(TokenKind.Dot, "."));
                i++;
                continue;
            }

            // String literal 'like ''this'''
            if (c == '\'')
            {
                i++;
                var sb = new StringBuilder();
                while (i < n)
                {
                    if (src[i] == '\'')
                    {
                        if (i + 1 < n && src[i + 1] == '\'')
                        {
                            sb.Append('\'');
                            i += 2;
                            continue;
                        }

                        i++; // closing quote
                        break;
                    }

                    sb.Append(src[i]);
                    i++;
                }

                tokens.Add(new Token(TokenKind.Str, sb.ToString()));
                continue;
            }

            // Hex literal $1A2B
            if (c == '$')
            {
                var start = ++i;
                while (i < n && Uri.IsHexDigit(src[i]))
                {
                    i++;
                }

                var hex = src[start..i];
                var val = hex.Length == 0 ? 0 : long.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                tokens.Add(new Token(TokenKind.Int, "$" + hex, val));
                continue;
            }

            // Number (decimal int or float)
            if (char.IsDigit(c))
            {
                var start = i;
                var isFloat = false;
                while (i < n && (char.IsDigit(src[i]) || src[i] == '.'))
                {
                    // Stop at '..' range or a '.' that begins a member access on an integer (rare here).
                    if (src[i] == '.')
                    {
                        if (i + 1 < n && src[i + 1] == '.')
                        {
                            break;
                        }

                        if (isFloat)
                        {
                            break;
                        }

                        isFloat = true;
                    }

                    i++;
                }

                var text = src[start..i];
                if (isFloat)
                {
                    tokens.Add(new Token(TokenKind.Float, text, 0,
                        double.Parse(text, CultureInfo.InvariantCulture)));
                }
                else
                {
                    tokens.Add(new Token(TokenKind.Int, text, long.Parse(text, CultureInfo.InvariantCulture)));
                }

                continue;
            }

            // Identifier
            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < n && (char.IsLetterOrDigit(src[i]) || src[i] == '_'))
                {
                    i++;
                }

                tokens.Add(new Token(TokenKind.Ident, src[start..i]));
                continue;
            }

            // Unknown character — skip it (operators we don't model, stray punctuation).
            i++;
        }

        tokens.Add(new Token(TokenKind.Eof, ""));
        return tokens;
    }
}
