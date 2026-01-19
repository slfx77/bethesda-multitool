// Version condition expression parser and evaluator for nif.xml
// Handles expressions like: ((#BSVER# #GTE# 130) #AND# (#BSVER# #LTE# 159))
// Uses recursive descent parsing for clean, maintainable code

using System.Collections.Concurrent;
using System.Globalization;

namespace Xbox360MemoryCarver.Core.Formats.Nif;

/// <summary>
///     Context for evaluating version expressions.
/// </summary>
public sealed record NifVersionContext
{
    /// <summary>NIF file version (e.g., 0x14020007 for 20.2.0.7)</summary>
    public uint Version { get; init; }

    /// <summary>User version (game-specific)</summary>
    public uint UserVersion { get; init; }

    /// <summary>Bethesda stream version (e.g., 34 for FO3/NV)</summary>
    public int BsVersion { get; init; }

    // Common presets
    public static NifVersionContext FalloutNV => new()
    {
        Version = 0x14020007, // 20.2.0.7
        UserVersion = 0,
        BsVersion = 34
    };

    public static NifVersionContext Fallout4 => new()
    {
        Version = 0x14020007,
        UserVersion = 12,
        BsVersion = 130
    };

    public static NifVersionContext Skyrim => new()
    {
        Version = 0x14020007,
        UserVersion = 12,
        BsVersion = 83
    };
}

/// <summary>
///     Parses and evaluates nif.xml version condition expressions.
///     Grammar:
///     expr     -> or_expr
///     or_expr  -> and_expr (('#OR#' | '||') and_expr)*
///     and_expr -> compare (('#AND#' | '&amp;&amp;') compare)*
///     compare  -> '(' expr ')' | variable op value | '!' compare
///     variable -> '#VER#' | '#BSVER#' | '#USER#' | identifier
///     op       -> '#GT#' | '#GTE#' | '#LT#' | '#LTE#' | '#EQ#' | '#NEQ#'
///     value    -> number | hex_number
/// </summary>
public sealed partial class NifVersionExpr
{
    /// <summary>
    ///     Token mappings from nif.xml verexpr definitions.
    ///     These are expanded before parsing.
    /// </summary>
    private static readonly Dictionary<string, string> TokenExpansions = new(StringComparer.OrdinalIgnoreCase)
    {
        // NiStream vs Bethesda stream
        ["#NISTREAM#"] = "(#BSVER# #EQ# 0)",
        ["#BSSTREAM#"] = "(#BSVER# #GT# 0)",

        // Less than / Less than or equal expressions (apply to NI + BS)
        ["#NI_BS_LTE_16#"] = "(#BSVER# #LTE# 16)",
        ["#NI_BS_LT_FO3#"] = "(#BSVER# #LT# 34)",
        ["#NI_BS_LTE_FO3#"] = "(#BSVER# #LTE# 34)",
        ["#NI_BS_LT_SSE#"] = "(#BSVER# #LT# 100)",
        ["#NI_BS_LT_FO4#"] = "(#BSVER# #LT# 130)",
        ["#NI_BS_LTE_FO4#"] = "(#BSVER# #LTE# 139)",
        ["#NI_BS_LT_STF#"] = "(#BSVER# #LT# 170)",

        // Greater than expressions (Bethesda only)
        ["#BS_GT_FO3#"] = "(#BSVER# #GT# 34)",
        ["#BS_GTE_FO3#"] = "(#BSVER# #GTE# 34)",
        ["#BS_GTE_SKY#"] = "(#BSVER# #GTE# 83)",
        ["#BS_GTE_SSE#"] = "(#BSVER# #GTE# 100)",
        ["#BS_GTE_F76#"] = "(#BSVER# #GTE# 155)",
        ["#BS_GTE_STF#"] = "(#BSVER# #GTE# 170)",

        // Exact match expressions
        ["#BS_SSE#"] = "(#BSVER# #EQ# 100)",
        ["#BS_SKY_SSE#"] = "((#BSVER# #GTE# 83) #AND# (#BSVER# #LTE# 100))",
        ["#BS_FO4#"] = "(#BSVER# #EQ# 130)",
        ["#BS_FO4_2#"] = "((#BSVER# #GTE# 130) #AND# (#BSVER# #LTE# 139))",
        ["#BS_GT_130#"] = "(#BSVER# #GT# 130)",
        ["#BS_GTE_130#"] = "(#BSVER# #GTE# 130)",
        ["#BS_GTE_132#"] = "(#BSVER# #GTE# 132)",
        ["#BS_132_139#"] = "((#BSVER# #GTE# 132) #AND# (#BSVER# #LTE# 139))",
        ["#BS_GTE_152#"] = "(#BSVER# #GTE# 152)",
        ["#BS_F76#"] = "(#BSVER# #EQ# 155)",
        ["#BS_FO4_F76#"] = "((#BSVER# #GTE# 130) #AND# (#BSVER# #LTE# 159))",
        ["#BS202#"] = "((#VER# #EQ# 20.2.0.7) #AND# (#BSVER# #GT# 0))",
        ["#DIVINITY2#"] = "((#USER# #EQ# 0x20000) #OR# (#USER# #EQ# 0x30000))"
    };

    // Cache for expanded expressions - expressions are constant strings from nif.xml
    private static readonly ConcurrentDictionary<string, string> ExpandedExpressionCache =
        new(StringComparer.OrdinalIgnoreCase);

    // Cache for compiled version expressions - expressions are static from nif.xml
    private static readonly ConcurrentDictionary<string, Func<NifVersionContext, bool>> CompiledExpressionCache =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string _expression;
    private int _pos;

    private NifVersionExpr(string expression)
    {
        // Expand any known tokens before parsing (with caching)
        _expression = ExpandTokensCached(expression.Trim());
        _pos = 0;
    }

    /// <summary>
    ///     Expands tokens like #BS_GT_FO3# to their full expressions (cached).
    /// </summary>
    private static string ExpandTokensCached(string expression)
    {
        return ExpandedExpressionCache.GetOrAdd(expression, ExpandTokensInternal);
    }

    /// <summary>
    ///     Internal implementation for token expansion.
    /// </summary>
    private static string ExpandTokensInternal(string expression)
    {
        // Iterate through all tokens and expand them
        foreach (var (token, expansion) in TokenExpansions)
        {
            if (expression.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                expression = expression.Replace(token, expansion, StringComparison.OrdinalIgnoreCase);
            }
        }

        return expression;
    }

    /// <summary>
    ///     Parses and evaluates a version condition expression.
    /// </summary>
    public static bool Evaluate(string? expression, NifVersionContext context)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return true; // No condition = always include
        }

        // Use cached compiled expression
        var evaluator = Compile(expression);
        return evaluator(context);
    }

    /// <summary>
    ///     Pre-parses an expression into an evaluatable form for repeated use.
    /// </summary>
    public static Func<NifVersionContext, bool> Compile(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return _ => true;
        }

        // Use cached compiled expression or compile and cache
        return CompiledExpressionCache.GetOrAdd(expression, static expr =>
        {
            try
            {
                var parser = new NifVersionExpr(expr);
                var ast = parser.ParseExpr();
                return ctx => ast.Eval(ctx);
            }
            catch
            {
                // On parse error, return a function that always returns true (conservative)
                return _ => true;
            }
        });
    }

    #region Lexer

    private void SkipWhitespace()
    {
        while (_pos < _expression.Length && char.IsWhiteSpace(_expression[_pos]))
        {
            _pos++;
        }
    }

    private bool Match(string s)
    {
        SkipWhitespace();
        if (_pos + s.Length <= _expression.Length &&
            _expression.AsSpan(_pos, s.Length).Equals(s.AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            _pos += s.Length;
            return true;
        }

        return false;
    }

    private bool Peek(char c)
    {
        SkipWhitespace();
        return _pos < _expression.Length && _expression[_pos] == c;
    }

    private void Expect(char c)
    {
        SkipWhitespace();
        if (_pos >= _expression.Length || _expression[_pos] != c)
        {
            throw new FormatException($"Expected '{c}' at position {_pos}");
        }

        _pos++;
    }

    private string ReadToken()
    {
        SkipWhitespace();
        var start = _pos;

        if (TryReadHashToken())
        {
            return _expression[start.._pos];
        }

        if (TryReadComparisonOperator())
        {
            return _expression[start.._pos];
        }

        ReadIdentifierOrNumber();

        return _expression[start.._pos];
    }

    private bool TryReadHashToken()
    {
        if (_pos >= _expression.Length || _expression[_pos] != '#')
        {
            return false;
        }

        _pos++;
        while (_pos < _expression.Length && _expression[_pos] != '#')
        {
            _pos++;
        }

        if (_pos < _expression.Length)
        {
            _pos++; // consume closing #
        }

        return true;
    }

    private bool TryReadComparisonOperator()
    {
        if (_pos >= _expression.Length)
        {
            return false;
        }

        var c = _expression[_pos];
        if (c is not ('=' or '!' or '>' or '<'))
        {
            return false;
        }

        _pos++;
        // Check for two-character operators (==, !=, >=, <=)
        if (_pos < _expression.Length && _expression[_pos] == '=')
        {
            _pos++;
        }

        return true;
    }

    private void ReadIdentifierOrNumber()
    {
        while (_pos < _expression.Length &&
               (char.IsLetterOrDigit(_expression[_pos]) || _expression[_pos] == '_' || _expression[_pos] == '.'))
        {
            _pos++;
        }
    }

    private long ReadNumber()
    {
        SkipWhitespace();
        var start = _pos;

        // Check for hex prefix
        if (_pos + 2 < _expression.Length &&
            _expression[_pos] == '0' &&
            (_expression[_pos + 1] == 'x' || _expression[_pos + 1] == 'X'))
        {
            _pos += 2;
            while (_pos < _expression.Length && IsHexDigit(_expression[_pos]))
            {
                _pos++;
            }

            return long.Parse(_expression[(start + 2).._pos], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        // Decimal number
        while (_pos < _expression.Length && (char.IsDigit(_expression[_pos]) || _expression[_pos] == '.'))
        {
            _pos++;
        }

        var numStr = _expression[start.._pos];
        if (numStr.Contains('.'))
        {
            // Version number like 20.2.0.7 - convert to uint
            var parts = numStr.Split('.');
            if (parts.Length == 4)
            {
                return (long.Parse(parts[0], CultureInfo.InvariantCulture) << 24) |
                       (long.Parse(parts[1], CultureInfo.InvariantCulture) << 16) |
                       (long.Parse(parts[2], CultureInfo.InvariantCulture) << 8) |
                       long.Parse(parts[3], CultureInfo.InvariantCulture);
            }
        }

        return long.Parse(numStr, CultureInfo.InvariantCulture);
    }

    private static bool IsHexDigit(char c)
    {
        return char.IsDigit(c) || c is >= 'a' and <= 'f' or >= 'A' and <= 'F';
    }

    #endregion

    #region Parser (Recursive Descent)

    private IExprNode ParseExpr()
    {
        return ParseOrExpr();
    }

    private IExprNode ParseOrExpr()
    {
        var left = ParseAndExpr();

        while (Match("#OR#") || Match("||"))
        {
            var right = ParseAndExpr();
            left = new OrNode(left, right);
        }

        return left;
    }

    private IExprNode ParseAndExpr()
    {
        var left = ParseUnary();

        while (Match("#AND#") || Match("&&"))
        {
            var right = ParseUnary();
            left = new AndNode(left, right);
        }

        return left;
    }

    private IExprNode ParseUnary()
    {
        if (Match("!") || Match("#NOT#"))
        {
            return new NotNode(ParseUnary());
        }

        return ParsePrimary();
    }

    private IExprNode ParsePrimary()
    {
        SkipWhitespace();

        // Parenthesized expression
        if (Peek('('))
        {
            Expect('(');
            var expr = ParseExpr();
            Expect(')');
            return expr;
        }

        // Comparison: variable op value
        var token = ReadToken();
        var variable = ParseVariable(token);

        // Read operator
        SkipWhitespace();
        var op = ReadToken();
        var cmp = ParseCompareOp(op);

        // Read value
        var value = ReadNumber();

        return new CompareNode(variable, cmp, value);
    }

    private static VariableType ParseVariable(string token)
    {
        return token.ToUpperInvariant() switch
        {
            "#VER#" or "#VERSION#" => VariableType.Version,
            "#BSVER#" or "#BS_VERSION#" => VariableType.BsVersion,
            "#USER#" or "#USER_VERSION#" => VariableType.UserVersion,
            _ => throw new FormatException($"Unknown variable: {token}")
        };
    }

    private static CompareOp ParseCompareOp(string token)
    {
        return token.ToUpperInvariant() switch
        {
            "#GT#" or ">" => CompareOp.Gt,
            "#GTE#" or ">=" => CompareOp.Gte,
            "#LT#" or "<" => CompareOp.Lt,
            "#LTE#" or "<=" => CompareOp.Lte,
            "#EQ#" or "==" or "=" => CompareOp.Eq,
            "#NEQ#" or "!=" => CompareOp.Neq,
            _ => throw new FormatException($"Unknown operator: {token}")
        };
    }

    #endregion
}
