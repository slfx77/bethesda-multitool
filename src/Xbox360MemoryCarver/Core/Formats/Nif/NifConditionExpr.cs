// Runtime condition expression parser and evaluator for nif.xml
// Handles expressions like: "Has Vertices", "Num Vertices > 0", "((Data Flags #BITAND# 63) != 0)"
// These conditions depend on field values read at runtime, not just version info

// S3218: Method shadowing is intentional in this expression tree visitor pattern

#pragma warning disable S3218

using System.Globalization;

namespace Xbox360MemoryCarver.Core.Formats.Nif;

/// <summary>
///     Parses and evaluates nif.xml runtime condition expressions.
///     These conditions depend on field values read during parsing.
///     Grammar:
///     expr     -> or_expr
///     or_expr  -> and_expr (('#OR#' | '||') and_expr)*
///     and_expr -> compare (('#AND#' | '&amp;&amp;') compare)*
///     compare  -> '(' expr ')' | '!' compare | field_expr
///     field_expr -> value ((op value) | empty)
///     value    -> '(' bitop_expr ')' | field_name | number
///     bitop_expr -> value ('#BITAND#' | '#BITOR#') value
///     op       -> '#GT#' | '#GTE#' | '#LT#' | '#LTE#' | '#EQ#' | '#NEQ#' | '!=' | '==' | etc.
/// </summary>
public sealed class NifConditionExpr
{
    private readonly string _expression;
    private int _pos;

    private NifConditionExpr(string expression)
    {
        _expression = expression.Trim();
        _pos = 0;
    }

    /// <summary>
    ///     Evaluates a condition expression against field values.
    /// </summary>
    public static bool Evaluate(string? expression, IReadOnlyDictionary<string, object> fieldValues)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return true; // No condition = always include

        try
        {
            var parser = new NifConditionExpr(expression);
            var ast = parser.ParseExpr();
            return ast.Evaluate(fieldValues);
        }
        catch (Exception ex)
        {
            // DEBUG: Print the parse error to console
            Console.WriteLine($"    [DEBUG] Condition eval error for '{expression}': {ex.Message}");
            // On parse error, default to including the field (conservative)
            return true;
        }
    }

    /// <summary>
    ///     Evaluates a value expression and returns a numeric result.
    ///     Used for evaluating arg expressions like "1" or "#ARG#".
    /// </summary>
    public static long EvaluateValue(string? expression, IReadOnlyDictionary<string, object> fieldValues)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return 0;

        try
        {
            var parser = new NifConditionExpr(expression);
            var valueAst = parser.ParseValueExpr();
            return valueAst.Evaluate(fieldValues);
        }
        catch
        {
            // On parse error, return 0
            return 0;
        }
    }

    /// <summary>
    ///     Pre-parses an expression into an evaluatable form for repeated use.
    /// </summary>
    public static Func<IReadOnlyDictionary<string, object>, bool> Compile(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return _ => true;

        try
        {
            var parser = new NifConditionExpr(expression);
            var ast = parser.ParseExpr();
            return ctx => ast.Evaluate(ctx);
        }
        catch
        {
            return _ => true;
        }
    }

    /// <summary>
    ///     Gets all field names referenced by this condition expression.
    /// </summary>
    public static HashSet<string> GetReferencedFields(string? expression)
    {
        var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(expression))
            return fields;

        try
        {
            var parser = new NifConditionExpr(expression);
            var ast = parser.ParseExpr();
            ast.CollectFields(fields);
        }
        catch
        {
            // Ignore parse errors
        }

        return fields;
    }

    #region Lexer

    private void SkipWhitespace()
    {
        while (_pos < _expression.Length && char.IsWhiteSpace(_expression[_pos]))
            _pos++;
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

    private bool PeekOperator()
    {
        SkipWhitespace();
        if (_pos >= _expression.Length) return false;

        // Check for multi-char operators
        var remaining = _expression.AsSpan(_pos);
        return remaining.StartsWith("#GT#", StringComparison.OrdinalIgnoreCase) ||
               remaining.StartsWith("#GTE#", StringComparison.OrdinalIgnoreCase) ||
               remaining.StartsWith("#LT#", StringComparison.OrdinalIgnoreCase) ||
               remaining.StartsWith("#LTE#", StringComparison.OrdinalIgnoreCase) ||
               remaining.StartsWith("#EQ#", StringComparison.OrdinalIgnoreCase) ||
               remaining.StartsWith("#NEQ#", StringComparison.OrdinalIgnoreCase) ||
               remaining.StartsWith("!=", StringComparison.Ordinal) ||
               remaining.StartsWith("==", StringComparison.Ordinal) ||
               remaining.StartsWith(">=", StringComparison.Ordinal) ||
               remaining.StartsWith("<=", StringComparison.Ordinal) ||
               _expression[_pos] == '>' ||
               _expression[_pos] == '<';
    }

    private void Expect(char c)
    {
        SkipWhitespace();
        if (_pos >= _expression.Length || _expression[_pos] != c)
            throw new FormatException($"Expected '{c}' at position {_pos}");
        _pos++;
    }

    private string ReadIdentifier()
    {
        SkipWhitespace();
        var start = _pos;

        // Field names can contain spaces and special characters
        // Read until we hit an operator, parenthesis, or end
        while (_pos < _expression.Length)
        {
            var c = _expression[_pos];

            // Stop at operators and special chars
            if (c == '(' || c == ')' || c == '!' || c == '#' ||
                c == '>' || c == '<' || c == '=' ||
                c == '&' || c == '|')
                break;

            _pos++;
        }

        return _expression[start.._pos].Trim();
    }

    private bool TryReadNumber(out long value)
    {
        SkipWhitespace();
        var start = _pos;
        value = 0;

        if (_pos >= _expression.Length)
            return false;

        // Check for hex prefix
        if (_pos + 2 < _expression.Length &&
            _expression[_pos] == '0' &&
            (_expression[_pos + 1] == 'x' || _expression[_pos + 1] == 'X'))
        {
            _pos += 2;
            var hexStart = _pos;
            while (_pos < _expression.Length && IsHexDigit(_expression[_pos]))
                _pos++;

            if (_pos > hexStart)
            {
                value = long.Parse(_expression[hexStart.._pos], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                return true;
            }

            _pos = start;
            return false;
        }

        // Check for negative numbers
        var negative = false;
        if (_expression[_pos] == '-')
        {
            negative = true;
            _pos++;
        }

        // Decimal number
        var numStart = _pos;
        while (_pos < _expression.Length && char.IsDigit(_expression[_pos]))
            _pos++;

        if (_pos > numStart)
        {
            value = long.Parse(_expression[numStart.._pos], CultureInfo.InvariantCulture);
            if (negative) value = -value;
            return true;
        }

        _pos = start;
        return false;
    }

    private static bool IsHexDigit(char c)
    {
        return char.IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
    }

    #endregion

    #region Parser

    private ICondNode ParseExpr()
    {
        return ParseOrExpr();
    }

    private ICondNode ParseOrExpr()
    {
        var left = ParseAndExpr();

        while (Match("#OR#") || Match("||"))
        {
            var right = ParseAndExpr();
            left = new OrCondNode(left, right);
        }

        return left;
    }

    private ICondNode ParseAndExpr()
    {
        var left = ParseUnary();

        while (Match("#AND#") || Match("&&"))
        {
            var right = ParseUnary();
            left = new AndCondNode(left, right);
        }

        return left;
    }

    private ICondNode ParseUnary()
    {
        if (Match("!") || Match("#NOT#"))
            return new NotCondNode(ParseUnary());

        return ParsePrimary();
    }

    private ICondNode ParsePrimary()
    {
        SkipWhitespace();

        // Parenthesized expression - could be condition or value
        if (Peek('('))
        {
            // Save position to backtrack if needed
            var savedPos = _pos;

            // Try parsing as a value expression first (handles bitwise ops)
            try
            {
                var valueExpr = ParseValueExpr();

                // If followed by comparison operator, it's a value comparison
                if (PeekOperator())
                {
                    var op = ReadOperator();
                    var right = ParseValueExpr();
                    return new CompareCondNode(valueExpr, op, right);
                }

                // Not followed by operator - could be boolean field in parens like "(Has Normals)"
                // or a parenthesized condition expression
                // If it's just a field, treat as boolean
                if (valueExpr is FieldNode) return new BoolCondNode(valueExpr);

                // If it's a bitwise expression result, treat as boolean (non-zero = true)
                return new BoolCondNode(valueExpr);
            }
            catch
            {
                // Value parsing failed, try as condition expression
                _pos = savedPos;
                Expect('(');
                var expr = ParseExpr();
                Expect(')');

                // Check if this is followed by an operator (bit comparison result)
                if (PeekOperator())
                {
                    var op = ReadOperator();
                    var right = ParseValueExpr();
                    return new CompareCondNode(new WrappedNode(expr), op, right);
                }

                return expr;
            }
        }

        // Simple field reference or comparison
        var left = ParseValueExpr();

        // Check for comparison operator
        if (PeekOperator())
        {
            var op = ReadOperator();
            var right = ParseValueExpr();
            return new CompareCondNode(left, op, right);
        }

        // Boolean field: "Has Vertices" means fieldValue != 0
        return new BoolCondNode(left);
    }

    /// <summary>
    ///     Parse a value expression: handles bitwise OR (lowest precedence in values)
    /// </summary>
    private IValueNode ParseValueExpr()
    {
        var left = ParseBitAndExpr();

        while (Match("#BITOR#") || Match("|"))
        {
            var right = ParseBitAndExpr();
            left = new BitOrNode(left, right);
        }

        return left;
    }

    /// <summary>
    ///     Parse bitwise AND expression (higher precedence than OR)
    /// </summary>
    private IValueNode ParseBitAndExpr()
    {
        var left = ParseValueAtom();

        while (Match("#BITAND#") || Match("&"))
        {
            var right = ParseValueAtom();
            left = new BitAndNode(left, right);
        }

        return left;
    }

    /// <summary>
    ///     Parse atomic value: number, field name, or parenthesized value expression
    /// </summary>
    private IValueNode ParseValueAtom()
    {
        SkipWhitespace();

        // Parenthesized sub-expression
        if (Peek('('))
        {
            Expect('(');
            var expr = ParseValueExpr();
            Expect(')');
            return expr;
        }

        // Try to read a number first
        if (TryReadNumber(out var num))
            return new LiteralNode(num);

        // Handle special tokens that start with # (like #ARG#)
        if (Peek('#'))
        {
            var specialToken = ReadSpecialToken();
            if (!string.IsNullOrEmpty(specialToken))
                return new FieldNode(specialToken);
        }

        // Otherwise it's a field name
        var fieldName = ReadIdentifier();
        if (string.IsNullOrEmpty(fieldName))
            throw new FormatException($"Expected field name or number at position {_pos}");

        return new FieldNode(fieldName);
    }

    /// <summary>
    ///     Read a special token like #ARG# that starts and ends with #
    /// </summary>
    private string ReadSpecialToken()
    {
        SkipWhitespace();
        if (_pos >= _expression.Length || _expression[_pos] != '#')
            return string.Empty;

        var start = _pos;
        _pos++; // Skip opening #

        // Read until we find the closing #
        while (_pos < _expression.Length && _expression[_pos] != '#') _pos++;

        if (_pos < _expression.Length && _expression[_pos] == '#')
        {
            _pos++; // Skip closing #
            return _expression[start.._pos]; // Return full token including both #
        }

        // No closing # found, reset position
        _pos = start;
        return string.Empty;
    }

    private CompareOp ReadOperator()
    {
        if (Match("#GT#") || Match(">")) return CompareOp.Gt;
        if (Match("#GTE#") || Match(">=")) return CompareOp.Gte;
        if (Match("#LT#") || Match("<")) return CompareOp.Lt;
        if (Match("#LTE#") || Match("<=")) return CompareOp.Lte;
        if (Match("#EQ#") || Match("==")) return CompareOp.Eq;
        if (Match("#NEQ#") || Match("!=")) return CompareOp.Neq;
        throw new FormatException($"Expected operator at position {_pos}");
    }

    #endregion

    #region AST Nodes

    private interface ICondNode
    {
        bool Evaluate(IReadOnlyDictionary<string, object> fields);
        void CollectFields(HashSet<string> fields);
    }

    private interface IValueNode
    {
        long Evaluate(IReadOnlyDictionary<string, object> fields);
        void CollectFields(HashSet<string> fields);
    }

    private enum CompareOp
    {
        Gt,
        Gte,
        Lt,
        Lte,
        Eq,
        Neq
    }

    private sealed class LiteralNode(long value) : IValueNode
    {
        public long Evaluate(IReadOnlyDictionary<string, object> fields)
        {
            return value;
        }

        public void CollectFields(HashSet<string> fields)
        {
        }
    }

    private sealed class FieldNode(string fieldName) : IValueNode
    {
        public long Evaluate(IReadOnlyDictionary<string, object> fields)
        {
            if (fields.TryGetValue(fieldName, out var val))
                return val switch
                {
                    bool b => b ? 1 : 0,
                    byte b => b,
                    sbyte sb => sb,
                    short s => s,
                    ushort us => us,
                    int i => i,
                    uint ui => ui,
                    long l => l,
                    ulong ul => (long)ul,
                    _ => 0
                };
            // Field not found - default to 0 (conservative for "Has X" conditions)
            return 0;
        }

        public void CollectFields(HashSet<string> fields)
        {
            fields.Add(fieldName);
        }
    }

    private sealed class BitAndNode(IValueNode left, IValueNode right) : IValueNode
    {
        public long Evaluate(IReadOnlyDictionary<string, object> fields)
        {
            return left.Evaluate(fields) & right.Evaluate(fields);
        }

        public void CollectFields(HashSet<string> fields)
        {
            left.CollectFields(fields);
            right.CollectFields(fields);
        }
    }

    private sealed class BitOrNode(IValueNode left, IValueNode right) : IValueNode
    {
        public long Evaluate(IReadOnlyDictionary<string, object> fields)
        {
            return left.Evaluate(fields) | right.Evaluate(fields);
        }

        public void CollectFields(HashSet<string> fields)
        {
            left.CollectFields(fields);
            right.CollectFields(fields);
        }
    }

    /// <summary>
    ///     Wraps a condition node to use as a value (for parenthesized expressions that return bool).
    /// </summary>
    private sealed class WrappedNode(ICondNode inner) : IValueNode
    {
        public long Evaluate(IReadOnlyDictionary<string, object> fields)
        {
            return inner.Evaluate(fields) ? 1 : 0;
        }

        public void CollectFields(HashSet<string> fields)
        {
            inner.CollectFields(fields);
        }
    }

    private sealed class CompareCondNode(IValueNode left, CompareOp op, IValueNode right) : ICondNode
    {
        public bool Evaluate(IReadOnlyDictionary<string, object> fields)
        {
            var l = left.Evaluate(fields);
            var r = right.Evaluate(fields);

            return op switch
            {
                CompareOp.Gt => l > r,
                CompareOp.Gte => l >= r,
                CompareOp.Lt => l < r,
                CompareOp.Lte => l <= r,
                CompareOp.Eq => l == r,
                CompareOp.Neq => l != r,
                _ => false
            };
        }

        public void CollectFields(HashSet<string> fields)
        {
            left.CollectFields(fields);
            right.CollectFields(fields);
        }
    }

    private sealed class BoolCondNode(IValueNode value) : ICondNode
    {
        public bool Evaluate(IReadOnlyDictionary<string, object> fields)
        {
            return value.Evaluate(fields) != 0;
        }

        public void CollectFields(HashSet<string> fields)
        {
            value.CollectFields(fields);
        }
    }

    private sealed class AndCondNode(ICondNode left, ICondNode right) : ICondNode
    {
        public bool Evaluate(IReadOnlyDictionary<string, object> fields)
        {
            return left.Evaluate(fields) && right.Evaluate(fields);
        }

        public void CollectFields(HashSet<string> fields)
        {
            left.CollectFields(fields);
            right.CollectFields(fields);
        }
    }

    private sealed class OrCondNode(ICondNode left, ICondNode right) : ICondNode
    {
        public bool Evaluate(IReadOnlyDictionary<string, object> fields)
        {
            return left.Evaluate(fields) || right.Evaluate(fields);
        }

        public void CollectFields(HashSet<string> fields)
        {
            left.CollectFields(fields);
            right.CollectFields(fields);
        }
    }

    private sealed class NotCondNode(ICondNode inner) : ICondNode
    {
        public bool Evaluate(IReadOnlyDictionary<string, object> fields)
        {
            return !inner.Evaluate(fields);
        }

        public void CollectFields(HashSet<string> fields)
        {
            inner.CollectFields(fields);
        }
    }

    #endregion
}
