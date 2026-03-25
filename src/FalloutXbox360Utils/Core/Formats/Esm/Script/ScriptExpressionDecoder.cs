namespace FalloutXbox360Utils.Core.Formats.Esm.Script;

/// <summary>
///     Decodes RPN (reverse Polish notation) expression bytecode into infix notation strings.
///     Used by <see cref="ScriptDecompiler" /> for If/ElseIf/While conditions and Set expressions.
/// </summary>
internal sealed class ScriptExpressionDecoder
{
    private readonly ScriptVariableReader _varReader;

    public ScriptExpressionDecoder(ScriptVariableReader varReader)
    {
        _varReader = varReader;
    }

    /// <summary>
    ///     Decode an RPN expression within a bounded region, converting to infix notation.
    /// </summary>
    internal string DecodeExpression(BytecodeReader reader, int exprEnd)
    {
        var stack = new Stack<ExprNode>();
        _varReader.ExprEnd = exprEnd;

        while (reader.Position < exprEnd && reader.HasData)
        {
            if (!TryDecodeExpressionToken(reader, stack, exprEnd))
            {
                break;
            }
        }

        if (stack.Count == 0)
        {
            return "<empty expression>";
        }

        return stack.Count == 1 ? stack.Pop().Text : string.Join(", ", stack.Reverse().Select(n => n.Text));
    }

    private bool TryDecodeExpressionToken(BytecodeReader reader, Stack<ExprNode> stack, int exprEnd)
    {
        if (!reader.HasData || reader.Position >= exprEnd)
        {
            return false;
        }

        var token = reader.PeekByte();

        // Every expression token is preceded by 0x20 as a universal prefix
        if (token == ScriptOpcodes.ExprPush && reader.CanRead(2))
        {
            reader.ReadByte(); // consume the 0x20 prefix
            var subToken = reader.PeekByte();

            // Check for two-byte operators after the 0x20 prefix
            if (reader.CanRead(2))
            {
                var second = reader.PeekByteAt(1);
                var twoByteOp = GetTwoByteOperator(subToken, second);
                if (twoByteOp != null)
                {
                    reader.Skip(2);
                    ApplyBinaryOperator(stack, twoByteOp);
                    return true;
                }
            }

            // Check for single-byte operators after the 0x20 prefix
            var singleOp = GetSingleByteOperator(subToken);
            if (singleOp != null)
            {
                reader.ReadByte();
                ApplyUnaryOrBinaryOperator(stack, singleOp);
                return true;
            }

            // Otherwise it's a push operand
            stack.Push(new ExprNode(DecodePushValue(reader), ExprNode.Atomic));
            return true;
        }

        // Tokens without the 0x20 prefix — shouldn't normally occur but handle gracefully
        var standalone = TryDecodeOperand(reader);
        if (standalone != null)
        {
            stack.Push(new ExprNode(standalone, ExprNode.Atomic));
            return true;
        }

        // Unknown token — dump remaining bytes
        var remaining = exprEnd - reader.Position;
        if (remaining > 0)
        {
            var hexDump = BitConverter.ToString(reader.ReadBytes(remaining)).Replace("-", " ");
            stack.Push(new ExprNode($"<unknown: {hexDump}>", ExprNode.Atomic));
        }

        return false;
    }

    private static void ApplyUnaryOrBinaryOperator(Stack<ExprNode> stack, string op)
    {
        // 0x7E '~' is always unary negation (distinct from binary minus 0x2D '-')
        if (op == "~")
        {
            var operand = stack.Count >= 1 ? stack.Pop().Text : "0";
            stack.Push(new ExprNode($"-{operand}", ExprNode.Atomic));
            return;
        }

        // Unary negation: '-' with fewer than 2 operands on stack
        if (op == "-" && stack.Count < 2)
        {
            var operand = stack.Count == 1 ? stack.Pop().Text : "0";
            stack.Push(new ExprNode($"-{operand}", ExprNode.Atomic));
            return;
        }

        ApplyBinaryOperator(stack, op);
    }

    private string DecodePushValue(BytecodeReader reader)
    {
        if (!reader.HasData)
        {
            return "<truncated push>";
        }

        var subToken = reader.PeekByte();

        switch (subToken)
        {
            case ScriptOpcodes.MarkerIntLocal:
            case ScriptOpcodes.MarkerFloatLocal:
                reader.ReadByte();
                return _varReader.ReadLocalVariable(reader);

            case ScriptOpcodes.MarkerReference:
                reader.ReadByte();
                return _varReader.ReadReferenceVariable(reader);

            case ScriptOpcodes.MarkerGlobal:
                reader.ReadByte();
                return _varReader.ReadGlobalVariable(reader);

            case ScriptOpcodes.ExprIntLiteral:
                reader.ReadByte();
                return ScriptVariableReader.ReadIntLiteral(reader);

            case ScriptOpcodes.ExprDoubleLiteral:
                reader.ReadByte();
                return _varReader.ReadDoubleLiteral(reader);

            case ScriptOpcodes.ExprFunctionCall:
                reader.ReadByte();
                return DecodeExpressionFunctionCall(reader);

            case 0x5A: // 'Z' — push SCRO reference as value
                reader.ReadByte();
                return reader.CanRead(2)
                    ? _varReader.ResolveScroReference(reader.ReadUInt16())
                    : "<truncated ref push>";

            default:
                // ASCII digit literals: 0x30='0' through 0x39='9'
                // Multi-digit numbers (e.g., 768 -> 0x37 0x36 0x38) and decimals (0.125)
                if (subToken is >= 0x30 and <= 0x39)
                {
                    return _varReader.ReadAsciiNumber(reader);
                }

                // Decimal-only literals: 0x2E='.' followed by digit (e.g., .5 -> 0x2E 0x35)
                if (subToken == 0x2E && reader.CanRead(2) && reader.PeekByteAt(1) is >= 0x30 and <= 0x39)
                {
                    return _varReader.ReadAsciiNumber(reader);
                }

                reader.ReadByte();
                return $"<push:0x{subToken:X2}>";
        }
    }

    /// <summary>
    ///     Try to decode a standalone operand (variable/literal without 0x20 prefix).
    /// </summary>
    private string? TryDecodeOperand(BytecodeReader reader)
    {
        if (!reader.HasData)
        {
            return null;
        }

        var token = reader.PeekByte();

        switch (token)
        {
            case ScriptOpcodes.MarkerIntLocal:
            case ScriptOpcodes.MarkerFloatLocal:
                reader.ReadByte();
                return _varReader.ReadLocalVariable(reader);

            case ScriptOpcodes.MarkerReference:
                reader.ReadByte();
                return _varReader.ReadReferenceVariable(reader);

            case ScriptOpcodes.MarkerGlobal:
                reader.ReadByte();
                return _varReader.ReadGlobalVariable(reader);

            case ScriptOpcodes.ExprIntLiteral:
                reader.ReadByte();
                return ScriptVariableReader.ReadIntLiteral(reader);

            case ScriptOpcodes.ExprDoubleLiteral:
                reader.ReadByte();
                return _varReader.ReadDoubleLiteral(reader);

            default:
                return null;
        }
    }

    internal string DecodeExpressionFunctionCall(BytecodeReader reader)
    {
        // Format: [opcode:2] [callParamLen:2] {[paramCount:2] [params...]} if callParamLen > 0
        if (!reader.CanRead(4))
        {
            return "<truncated function call>";
        }

        var opcode = reader.ReadUInt16();
        var callParamLen = reader.ReadUInt16();
        var callEnd = reader.Position + callParamLen;

        var funcDef = ScriptFunctionTable.Get(opcode);
        var funcName = ScriptDecompiler.GetFunctionDisplayName(funcDef, opcode);
        var prefix = _varReader.ConsumePendingRef();

        // If callParamLen is 0, there are no parameters (no paramCount field)
        if (callParamLen < 2 || !reader.CanRead(2))
        {
            reader.Position = callEnd;
            return $"{prefix}{funcName}";
        }

        var paramCount = reader.ReadUInt16();
        var stmtDecoder = _varReader.StmtDecoder!;
        var paramStrings = new List<string>();
        for (var i = 0; i < paramCount && reader.HasData; i++)
        {
            var expectedType = funcDef != null && i < funcDef.Params.Length
                ? funcDef.Params[i].Type
                : (ScriptParamType?)null;

            paramStrings.Add(stmtDecoder.DecodeFunctionParameter(reader, expectedType));
        }

        // Ensure we don't over-read past the call boundary
        reader.Position = callEnd;

        return paramStrings.Count > 0
            ? $"{prefix}{funcName} {string.Join(" ", paramStrings)}"
            : $"{prefix}{funcName}";
    }

    private static void ApplyBinaryOperator(Stack<ExprNode> stack, string op)
    {
        var prec = GetOperatorPrecedence(op);

        if (stack.Count < 2)
        {
            var partial = stack.Count == 1 ? $"({stack.Pop().Text} {op} ???)" : $"(??? {op} ???)";
            stack.Push(new ExprNode(partial, prec));
            return;
        }

        var right = stack.Pop();
        var left = stack.Pop();

        // Wrap left operand if its outermost operator has lower precedence
        var leftText = left.Precedence < prec ? $"({left.Text})" : left.Text;

        // Wrap right operand if lower precedence, or same precedence for
        // non-commutative operators to preserve left-to-right evaluation:
        // a - (b - c) != a - b - c
        var wrapRight = right.Precedence < prec ||
                        (right.Precedence == prec && op is "-" or "/" or "%");
        var rightText = wrapRight ? $"({right.Text})" : right.Text;

        stack.Push(new ExprNode($"{leftText} {op} {rightText}", prec));
    }

    private static string? GetTwoByteOperator(byte first, byte second)
    {
        return (first, second) switch
        {
            (0x26, 0x26) => "&&",
            (0x7C, 0x7C) => "||",
            (0x3D, 0x3D) => "==",
            (0x21, 0x3D) => "!=",
            (0x3E, 0x3D) => ">=",
            (0x3C, 0x3D) => "<=",
            _ => null
        };
    }

    private static string? GetSingleByteOperator(byte token)
    {
        return token switch
        {
            0x2B => "+",
            0x2D => "-",
            0x2A => "*",
            0x2F => "/",
            0x25 => "%",
            0x3E => ">",
            0x3C => "<",
            ScriptOpcodes.ExprUnaryNegate => "~", // always unary negation (distinct from binary minus)
            _ => null
        };
    }

    private static int GetOperatorPrecedence(string op)
    {
        return op switch
        {
            "||" => ExprNode.PrecOr,
            "&&" => ExprNode.PrecAnd,
            "==" or "!=" => ExprNode.PrecEquality,
            "<" or ">" or "<=" or ">=" => ExprNode.PrecRelational,
            "+" or "-" => ExprNode.PrecAdditive,
            "*" or "/" or "%" => ExprNode.PrecMultiplicative,
            _ => ExprNode.Atomic
        };
    }

    /// <summary>
    ///     Expression node on the RPN evaluation stack, carrying both the rendered text
    ///     and the precedence level of the outermost operator.
    ///     Higher precedence values bind more tightly; Atomic values are never wrapped.
    /// </summary>
    internal readonly record struct ExprNode(string Text, int Precedence)
    {
        public const int Atomic = int.MaxValue;
        public const int PrecOr = 1; // ||
        public const int PrecAnd = 2; // &&
        public const int PrecEquality = 3; // ==, !=
        public const int PrecRelational = 4; // <, >, <=, >=
        public const int PrecAdditive = 5; // +, -
        public const int PrecMultiplicative = 6; // *, /, %
    }
}
