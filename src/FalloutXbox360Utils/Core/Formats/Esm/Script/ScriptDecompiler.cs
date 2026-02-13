using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm.Script;

/// <summary>
///     Decompiles Fallout: New Vegas compiled script bytecode (SCDA) back to GECK-compatible source text.
///     Handles both big-endian (Xbox 360) and little-endian (PC) bytecode.
/// </summary>
/// <remarks>
///     Create a new script decompiler.
/// </remarks>
/// <param name="variables">Script local variable definitions (from SLSD+SCVR).</param>
/// <param name="referencedObjects">SCRO FormID list (0-indexed in this list, 1-indexed in bytecode).</param>
/// <param name="resolveFormName">Callback to resolve FormID to editor ID string.</param>
/// <param name="isBigEndian">True for Xbox 360 bytecode, false for PC.</param>
/// <param name="scriptName">Optional script name for the ScriptName line.</param>
/// <param name="resolveExternalVariable">
///     Optional callback to resolve cross-script variable references.
///     Parameters: (FormID of referenced object, variable index) → variable name.
/// </param>
public sealed class ScriptDecompiler(
    List<ScriptVariableInfo> variables,
    List<uint> referencedObjects,
    Func<uint, string?> resolveFormName,
    bool isBigEndian = false,
    string? scriptName = null,
    Func<uint, ushort, string?>? resolveExternalVariable = null)
{
    private readonly bool _isBigEndian = isBigEndian;
    private readonly StringBuilder _output = new();
    private readonly List<uint> _referencedObjects = referencedObjects;
    private readonly Func<uint, ushort, string?>? _resolveExternalVariable = resolveExternalVariable;
    private readonly Func<uint, string?> _resolveFormName = resolveFormName;
    private readonly string? _scriptName = scriptName;
    private readonly List<ScriptVariableInfo> _variables = variables;
    private bool _hasPendingRef;
    private int _indentLevel;
    private ushort _pendingRefIndex;

    // State during decompilation
    private BytecodeReader _reader = null!;
    private int _exprEnd; // Current expression boundary (prevents over-reading ASCII numbers)

    /// <summary>
    ///     Expression node on the RPN evaluation stack, carrying both the rendered text
    ///     and the precedence level of the outermost operator.
    ///     Higher precedence values bind more tightly; Atomic values are never wrapped.
    /// </summary>
    private readonly record struct ExprNode(string Text, int Precedence)
    {
        public const int Atomic = int.MaxValue;
        public const int PrecOr = 1;             // ||
        public const int PrecAnd = 2;            // &&
        public const int PrecEquality = 3;       // ==, !=
        public const int PrecRelational = 4;     // <, >, <=, >=
        public const int PrecAdditive = 5;       // +, -
        public const int PrecMultiplicative = 6; // *, /, %
    }

    /// <summary>
    ///     Decompile compiled bytecode to GECK script source text.
    ///     Never throws — returns partial output with error comments on failure.
    /// </summary>
    public string Decompile(byte[] compiledData)
    {
        _output.Clear();
        _indentLevel = 0;
        _hasPendingRef = false;
        _reader = new BytecodeReader(compiledData, _isBigEndian);
        _exprEnd = compiledData.Length;

        try
        {
            DecompileTopLevel();
        }
        catch (Exception ex)
        {
            AppendLine($"; Decompilation error at offset 0x{_reader.Position:X}: {ex.Message}");
        }

        return _output.ToString().TrimEnd();
    }

    #region Top-Level Dispatcher

    private void DecompileTopLevel()
    {
        while (_reader.HasData && _reader.CanRead(4))
        {
            var opcodePos = _reader.Position;
            var opcode = _reader.ReadUInt16();
            var paramLen = _reader.ReadUInt16();

            // SetRef is special: the "paramLen" field IS the reference index (1-based SCRO),
            // and there are NO additional data bytes. The next opcode follows immediately.
            if (opcode == ScriptOpcodes.SetRef)
            {
                _pendingRefIndex = paramLen;
                _hasPendingRef = true;
                continue;
            }

            if (!_reader.CanRead(paramLen))
            {
                AppendLine(
                    $"; Truncated at offset 0x{opcodePos:X}: opcode 0x{opcode:X4} needs {paramLen} bytes, {_reader.Remaining} available");
                break;
            }

            var paramEnd = _reader.Position + paramLen;

            try
            {
                DispatchOpcode(opcode, paramLen, opcodePos);
            }
            catch (Exception ex)
            {
                AppendLine($"; Error decoding opcode 0x{opcode:X4} at offset 0x{opcodePos:X}: {ex.Message}");
            }

            // Ensure we're positioned at paramEnd regardless of how much was consumed
            _reader.Position = paramEnd;
        }
    }

    private void DispatchOpcode(ushort opcode, int paramLen, int opcodePos)
    {
        switch (opcode)
        {
            case ScriptOpcodes.ScriptName:
                AppendLine(!string.IsNullOrEmpty(_scriptName) ? $"ScriptName {_scriptName}" : "ScriptName");
                break;

            case ScriptOpcodes.Begin:
                HandleBegin(paramLen);
                break;

            case ScriptOpcodes.End:
                DecrementIndent();
                AppendLine("End");
                break;

            case ScriptOpcodes.Set:
                HandleSet(paramLen);
                break;

            case ScriptOpcodes.If:
                HandleConditionalBlock("If", paramLen);
                break;

            case ScriptOpcodes.ElseIf:
                DecrementIndent();
                HandleConditionalBlock("ElseIf", paramLen);
                break;

            case ScriptOpcodes.Else:
                DecrementIndent();
                AppendLine("Else");
                _indentLevel++;
                break;

            case ScriptOpcodes.EndIf:
                DecrementIndent();
                AppendLine("EndIf");
                break;

            case ScriptOpcodes.While:
                HandleConditionalBlock("While", paramLen);
                break;

            case ScriptOpcodes.EndWhile:
                DecrementIndent();
                AppendLine("EndWhile");
                break;

            case ScriptOpcodes.Return:
                AppendLine("Return");
                break;

            case ScriptOpcodes.VarShort:
            case ScriptOpcodes.VarLong:
            case ScriptOpcodes.VarFloat:
            case ScriptOpcodes.FlowRef:
                // Variable/reference declarations — skip (handled implicitly by variable definitions)
                break;

            case ScriptOpcodes.ScriptDone:
                // End of bytecode stream — caller will see Position >= Length
                _reader.Position = _reader.Length;
                break;

            default:
                if (opcode >= ScriptOpcodes.MinFunctionOpcode)
                {
                    HandleFunctionCall(opcode, paramLen);
                }
                else
                {
                    AppendLine($"; Unknown opcode 0x{opcode:X4} ({paramLen} bytes) at offset 0x{opcodePos:X}");
                }

                break;
        }
    }

    #endregion

    #region Flow Control Handlers

    private void DecrementIndent()
    {
        if (_indentLevel > 0)
        {
            _indentLevel--;
        }
    }

    private void HandleBegin(int paramLen)
    {
        if (paramLen < 2)
        {
            AppendLine("Begin");
            _indentLevel++;
            return;
        }

        var blockType = _reader.ReadUInt16();
        var blockName = GetBlockTypeName(blockType);

        // Begin data format: [blockType:2] [endOffset:4] [paramCount:2?] [event params...]
        // Skip the 4-byte end offset (jump target for matching End)
        if (paramLen >= 6)
        {
            _reader.ReadUInt32(); // endOffset — not needed for decompilation
        }

        // Remaining bytes after blockType(2) + endOffset(4) may contain event parameters
        // e.g., "Begin OnTriggerEnter player" has [paramCount:2] [ref_data...]
        var remainingAfterHeader = paramLen - 6;
        if (remainingAfterHeader >= 2)
        {
            var eventParamCount = _reader.ReadUInt16();
            if (eventParamCount > 0)
            {
                var eventParams = new List<string>();
                for (var i = 0; i < eventParamCount && _reader.HasData; i++)
                {
                    eventParams.Add(DecodeFunctionParameter(null));
                }

                AppendLine($"Begin {blockName} {string.Join(" ", eventParams)}");
                _indentLevel++;
                return;
            }
        }

        AppendLine($"Begin {blockName}");
        _indentLevel++;
    }

    private void HandleSet(int paramLen)
    {
        if (paramLen < 3)
        {
            AppendLine($"; Set with insufficient data ({paramLen} bytes)");
            return;
        }

        // Set format: [variable:varies] [exprLen:2] [expression...]
        var targetVar = ReadVariableReference();

        if (!_reader.CanRead(2))
        {
            AppendLine("; Set: truncated before expression length");
            return;
        }

        var exprLen = _reader.ReadUInt16();
        var exprEnd = _reader.Position + exprLen;
        var expr = DecodeExpression(exprEnd);

        AppendLine($"Set {targetVar} to {expr}");
    }

    /// <summary>
    ///     Handle If, ElseIf, and While — all share the same [jumpOffset:2][exprLen:2][expr] format.
    /// </summary>
    private void HandleConditionalBlock(string keyword, int paramLen)
    {
        if (paramLen < 4)
        {
            AppendLine($"; {keyword} with insufficient data ({paramLen} bytes)");
            return;
        }

        // [jumpOffset:2] [exprLen:2] [expression...]
        _reader.ReadUInt16(); // jumpOffset — skip (used by runtime to jump past block)
        var exprLen = _reader.ReadUInt16();
        var exprEnd = _reader.Position + exprLen;

        var expr = DecodeExpression(exprEnd);
        AppendLine($"{keyword} {expr}");
        _indentLevel++;
    }

    #endregion

    #region Function Call Handling

    private void HandleFunctionCall(ushort opcode, int paramLen)
    {
        var funcDef = ScriptFunctionTable.Get(opcode);
        var funcName = GetFunctionDisplayName(funcDef, opcode);

        var prefix = ConsumePendingRef();

        if (paramLen < 2)
        {
            AppendLine($"{prefix}{funcName}");
            return;
        }

        var paramCount = _reader.ReadUInt16();
        var paramStrings = DecodeParameterList(funcDef, paramCount);

        AppendLine(paramStrings.Count > 0
            ? $"{prefix}{funcName} {string.Join(" ", paramStrings)}"
            : $"{prefix}{funcName}");
    }

    private static string GetFunctionDisplayName(ScriptFunctionDef? funcDef, ushort opcode)
    {
        if (funcDef == null)
        {
            return ScriptFunctionTable.GetName(opcode);
        }

        // GECK scripts use both short and long names interchangeably.
        // Prefer short name when available as it's more common in hand-written scripts.
        return !string.IsNullOrEmpty(funcDef.ShortName) ? funcDef.ShortName : funcDef.Name;
    }

    private string ConsumePendingRef()
    {
        if (!_hasPendingRef)
        {
            return "";
        }

        _hasPendingRef = false;
        return ResolveScroReference(_pendingRefIndex) + ".";
    }

    private List<string> DecodeParameterList(ScriptFunctionDef? funcDef, int paramCount)
    {
        var paramStrings = new List<string>();
        for (var i = 0; i < paramCount && _reader.HasData; i++)
        {
            var expectedType = funcDef != null && i < funcDef.Params.Length
                ? funcDef.Params[i].Type
                : (ScriptParamType?)null;

            paramStrings.Add(DecodeFunctionParameter(expectedType));
        }

        return paramStrings;
    }

    #endregion

    #region Expression Decoder (RPN to Infix)

    /// <summary>
    ///     Decode an RPN expression within a bounded region, converting to infix notation.
    /// </summary>
    private string DecodeExpression(int exprEnd)
    {
        var stack = new Stack<ExprNode>();
        _exprEnd = exprEnd;

        while (_reader.Position < exprEnd && _reader.HasData)
        {
            if (!TryDecodeExpressionToken(stack, exprEnd))
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

    private bool TryDecodeExpressionToken(Stack<ExprNode> stack, int exprEnd)
    {
        if (!_reader.HasData || _reader.Position >= exprEnd)
        {
            return false;
        }

        var token = _reader.PeekByte();

        // Every expression token is preceded by 0x20 as a universal prefix
        if (token == ScriptOpcodes.ExprPush && _reader.CanRead(2))
        {
            _reader.ReadByte(); // consume the 0x20 prefix
            var subToken = _reader.PeekByte();

            // Check for two-byte operators after the 0x20 prefix
            if (_reader.CanRead(2))
            {
                var second = _reader.PeekByteAt(1);
                var twoByteOp = GetTwoByteOperator(subToken, second);
                if (twoByteOp != null)
                {
                    _reader.Skip(2);
                    ApplyBinaryOperator(stack, twoByteOp);
                    return true;
                }
            }

            // Check for single-byte operators after the 0x20 prefix
            var singleOp = GetSingleByteOperator(subToken);
            if (singleOp != null)
            {
                _reader.ReadByte();
                ApplyUnaryOrBinaryOperator(stack, singleOp);
                return true;
            }

            // Otherwise it's a push operand
            stack.Push(new ExprNode(DecodePushValue(), ExprNode.Atomic));
            return true;
        }

        // Tokens without the 0x20 prefix — shouldn't normally occur but handle gracefully
        var standalone = TryDecodeOperand();
        if (standalone != null)
        {
            stack.Push(new ExprNode(standalone, ExprNode.Atomic));
            return true;
        }

        // Unknown token — dump remaining bytes
        var remaining = exprEnd - _reader.Position;
        if (remaining > 0)
        {
            var hexDump = BitConverter.ToString(_reader.ReadBytes(remaining)).Replace("-", " ");
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

    private string DecodePushValue()
    {
        if (!_reader.HasData)
        {
            return "<truncated push>";
        }

        var subToken = _reader.PeekByte();

        switch (subToken)
        {
            case ScriptOpcodes.MarkerIntLocal:
            case ScriptOpcodes.MarkerFloatLocal:
                _reader.ReadByte();
                return ReadLocalVariable();

            case ScriptOpcodes.MarkerReference:
                _reader.ReadByte();
                return ReadReferenceVariable();

            case ScriptOpcodes.MarkerGlobal:
                _reader.ReadByte();
                return ReadGlobalVariable();

            case ScriptOpcodes.ExprIntLiteral:
                _reader.ReadByte();
                return ReadIntLiteral();

            case ScriptOpcodes.ExprDoubleLiteral:
                _reader.ReadByte();
                return ReadDoubleLiteral();

            case ScriptOpcodes.ExprFunctionCall:
                _reader.ReadByte();
                return DecodeExpressionFunctionCall();

            case 0x5A: // 'Z' — push SCRO reference as value
                _reader.ReadByte();
                return _reader.CanRead(2) ? ResolveScroReference(_reader.ReadUInt16()) : "<truncated ref push>";

            default:
                // ASCII digit literals: 0x30='0' through 0x39='9'
                // Multi-digit numbers (e.g., 768 → 0x37 0x36 0x38) and decimals (0.125)
                if (subToken is >= 0x30 and <= 0x39)
                {
                    return ReadAsciiNumber();
                }

                // Decimal-only literals: 0x2E='.' followed by digit (e.g., .5 → 0x2E 0x35)
                if (subToken == 0x2E && _reader.CanRead(2) && _reader.PeekByteAt(1) is >= 0x30 and <= 0x39)
                {
                    return ReadAsciiNumber();
                }

                _reader.ReadByte();
                return $"<push:0x{subToken:X2}>";
        }
    }

    /// <summary>
    ///     Try to decode a standalone operand (variable/literal without 0x20 prefix).
    /// </summary>
    private string? TryDecodeOperand()
    {
        if (!_reader.HasData)
        {
            return null;
        }

        var token = _reader.PeekByte();

        switch (token)
        {
            case ScriptOpcodes.MarkerIntLocal:
            case ScriptOpcodes.MarkerFloatLocal:
                _reader.ReadByte();
                return ReadLocalVariable();

            case ScriptOpcodes.MarkerReference:
                _reader.ReadByte();
                return ReadReferenceVariable();

            case ScriptOpcodes.MarkerGlobal:
                _reader.ReadByte();
                return ReadGlobalVariable();

            case ScriptOpcodes.ExprIntLiteral:
                _reader.ReadByte();
                return ReadIntLiteral();

            case ScriptOpcodes.ExprDoubleLiteral:
                _reader.ReadByte();
                return ReadDoubleLiteral();

            default:
                return null;
        }
    }

    private string DecodeExpressionFunctionCall()
    {
        // Format: [opcode:2] [callParamLen:2] {[paramCount:2] [params...]} if callParamLen > 0
        if (!_reader.CanRead(4))
        {
            return "<truncated function call>";
        }

        var opcode = _reader.ReadUInt16();
        var callParamLen = _reader.ReadUInt16();
        var callEnd = _reader.Position + callParamLen;

        var funcDef = ScriptFunctionTable.Get(opcode);
        var funcName = GetFunctionDisplayName(funcDef, opcode);
        var prefix = ConsumePendingRef();

        // If callParamLen is 0, there are no parameters (no paramCount field)
        if (callParamLen < 2 || !_reader.CanRead(2))
        {
            _reader.Position = callEnd;
            return $"{prefix}{funcName}";
        }

        var paramCount = _reader.ReadUInt16();
        var paramStrings = DecodeParameterList(funcDef, paramCount);

        // Ensure we don't over-read past the call boundary
        _reader.Position = callEnd;

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

    private static int GetOperatorPrecedence(string op) => op switch
    {
        "||" => ExprNode.PrecOr,
        "&&" => ExprNode.PrecAnd,
        "==" or "!=" => ExprNode.PrecEquality,
        "<" or ">" or "<=" or ">=" => ExprNode.PrecRelational,
        "+" or "-" => ExprNode.PrecAdditive,
        "*" or "/" or "%" => ExprNode.PrecMultiplicative,
        _ => ExprNode.Atomic
    };

    #endregion

    #region Function Parameter Decoding

    /// <summary>
    ///     Decode a single function parameter. All parameters are prefixed with a type marker byte
    ///     that indicates the encoding format.
    /// </summary>
    private string DecodeFunctionParameter(ScriptParamType? expectedType)
    {
        if (!_reader.HasData)
        {
            return "<truncated>";
        }

        var marker = _reader.PeekByte();

        // Check for marker bytes — all function parameters are marker-prefixed
        switch (marker)
        {
            case ScriptOpcodes.MarkerIntLocal: // 0x73 's' — int local variable
            case ScriptOpcodes.MarkerFloatLocal: // 0x66 'f' — float local variable
                _reader.ReadByte();
                return ReadLocalVariable();

            case ScriptOpcodes.MarkerReference: // 0x72 'r' — SCRO reference (2-byte index)
                _reader.ReadByte();
                return _reader.CanRead(2) ? ResolveScroReference(_reader.ReadUInt16()) : "<truncated ref>";

            case ScriptOpcodes.MarkerGlobal: // 0x47 'G' — global variable (2-byte SCRO index)
                _reader.ReadByte();
                return ReadGlobalVariable();

            case ScriptOpcodes.ExprIntLiteral: // 0x6E 'n' — integer literal (4 bytes)
                _reader.ReadByte();
                return ReadIntLiteral();

            case ScriptOpcodes.ExprDoubleLiteral: // 0x7A 'z' — double literal (8 bytes)
                _reader.ReadByte();
                return ReadDoubleLiteral(expectedType);
        }

        // No recognized marker — decode based on expected type (strings, etc.)
        if (expectedType == null)
        {
            return _reader.CanRead(2) ? ResolveScroReference(_reader.ReadUInt16()) : "<unknown param>";
        }

        return DecodeTypedParameter(expectedType.Value);
    }

    private string DecodeTypedParameter(ScriptParamType type)
    {
        // Fixed-size special types
        switch (type)
        {
            case ScriptParamType.Char:
                return DecodeStringParam();
            case ScriptParamType.Int:
                return _reader.CanRead(4) ? _reader.ReadInt32().ToString() : "<truncated int>";
            case ScriptParamType.Float:
                return DecodeFloatParam();
            case ScriptParamType.VatsValueData:
                return _reader.CanRead(4) ? _reader.ReadInt32().ToString() : "<truncated vatdata>";
            case ScriptParamType.ScriptVar:
                return DecodeScriptVarParam();
            case ScriptParamType.Axis:
                // Axis is a single raw byte: 'X'=0x58, 'Y'=0x59, 'Z'=0x5A (no marker prefix)
                return _reader.HasData ? DecodeAxis(_reader.ReadByte()) : "<truncated axis>";
        }

        // All remaining types are 2 bytes — read once, then interpret
        if (!_reader.CanRead(2))
        {
            return $"<truncated {type}>";
        }

        var val = _reader.ReadUInt16();

        // Check for labeled enum/code types (not SCRO references)
        var labeledResult = TryDecodeLabeledUInt16(type, val);
        if (labeledResult != null)
        {
            return labeledResult;
        }

        // Default: form reference via 2-byte SCRO index (1-based)
        return ResolveScroReference(val);
    }

    private static string? TryDecodeLabeledUInt16(ScriptParamType type, ushort val)
    {
        return type switch
        {
            ScriptParamType.ActorValue => GetActorValueName(val),
            ScriptParamType.Axis => DecodeAxis(val),
            ScriptParamType.Sex => val == 0 ? "Male" : "Female",
            ScriptParamType.AnimGroup => GetAnimGroupName(val),
            ScriptParamType.CrimeType => DecodeCrimeType(val),
            ScriptParamType.FormType => $"FormType:{val}",
            ScriptParamType.MiscStat => GetMiscStatName(val),
            ScriptParamType.Alignment => $"Alignment:{val}",
            ScriptParamType.EquipType => $"EquipType:{val}",
            ScriptParamType.CritStage => DecodeCritStage(val),
            ScriptParamType.VatsValue => $"VATSValue:{val}",
            ScriptParamType.Stage => val.ToString(),
            _ => null
        };
    }

    private string DecodeStringParam()
    {
        if (!_reader.CanRead(2))
        {
            return "<truncated string>";
        }

        var strLen = _reader.ReadUInt16();
        if (!_reader.CanRead(strLen))
        {
            return "<truncated string>";
        }

        var strBytes = _reader.ReadBytes(strLen);
        var str = Encoding.ASCII.GetString(strBytes).TrimEnd('\0');
        return $"\"{str}\"";
    }

    private string DecodeFloatParam()
    {
        if (!_reader.CanRead(8))
        {
            return "<truncated float>";
        }

        var dval = _reader.ReadDouble();
        return FormatDouble(dval);
    }

    private string DecodeScriptVarParam()
    {
        if (!_reader.HasData)
        {
            return "<truncated scriptvar>";
        }

        // ScriptVar params use the same marker-based encoding as other params
        var marker = _reader.PeekByte();
        switch (marker)
        {
            case ScriptOpcodes.MarkerIntLocal:
            case ScriptOpcodes.MarkerFloatLocal:
                _reader.ReadByte();
                return ReadLocalVariable();
            case ScriptOpcodes.MarkerReference:
                _reader.ReadByte();
                return _reader.CanRead(2) ? ResolveScroReference(_reader.ReadUInt16()) : "<truncated ref>";
            case ScriptOpcodes.MarkerGlobal:
                _reader.ReadByte();
                return ReadGlobalVariable();
        }

        // Fallback: read as uint16 index
        return _reader.CanRead(2) ? GetVariableName(_reader.ReadUInt16()) : "<truncated scriptvar>";
    }

    private static string GetAnimGroupName(ushort val)
    {
        // TESAnimGroup enum values — from PDB symbols (ANIM_GROUP_* enum)
        return val switch
        {
            0 => "Idle",
            1 => "DynamicIdle",
            2 => "SpecialIdle",
            3 => "Forward",
            4 => "Backward",
            5 => "Left",
            6 => "Right",
            7 => "FastForward",
            8 => "FastBackward",
            9 => "FastLeft",
            10 => "FastRight",
            11 => "DodgeForward",
            12 => "DodgeBack",
            13 => "DodgeLeft",
            14 => "DodgeRight",
            15 => "TurnLeft",
            16 => "TurnRight",
            17 => "Aim",
            18 => "AimUp",
            19 => "AimDown",
            20 => "AimIS",
            21 => "AimISUp",
            22 => "AimISDown",
            23 => "Holster",
            24 => "Equip",
            25 => "Unequip",
            92 => "AttackPower",
            93 => "AttackForwardPower",
            94 => "AttackBackPower",
            95 => "AttackLeftPower",
            96 => "AttackRightPower",
            170 => "BlockIdle",
            171 => "BlockHit",
            172 => "Recoil",
            _ => $"AnimGroup:{val}"
        };
    }

    private static string DecodeAxis(ushort val)
    {
        return val switch
        {
            0x58 => "X",
            0x59 => "Y",
            0x5A => "Z",
            _ => $"Axis:{val}"
        };
    }

    private static string DecodeCritStage(ushort val)
    {
        // Values from GECK wiki — confirmed against SCTX source text
        return val switch
        {
            0 => "None",
            1 => "GooStart",
            2 => "GooEnd",
            3 => "DisintegrateStart",
            4 => "DisintegrateEnd",
            _ => $"CritStage:{val}"
        };
    }

    private static string DecodeCrimeType(ushort val)
    {
        return val switch
        {
            0 => "Steal",
            1 => "Pickpocket",
            2 => "Trespass",
            3 => "Attack",
            4 => "Murder",
            _ => $"CrimeType:{val}"
        };
    }

    // Names from PDB enum MiscStatManager::MiscStatID, mapped to GECK display strings
    private static string GetMiscStatName(ushort index)
    {
        return index switch
        {
            0 => "\"Quests Completed\"",
            1 => "\"Locations Discovered\"",
            2 => "\"People Killed\"",
            3 => "\"Creatures Killed\"",
            4 => "\"Locks Picked\"",
            5 => "\"Computers Hacked\"",
            6 => "\"Stimpaks Taken\"",
            7 => "\"Rad-X Taken\"",
            8 => "\"RadAway Taken\"",
            9 => "\"Chems Taken\"",
            10 => "\"Times Addicted\"",
            11 => "\"Mines Disarmed\"",
            12 => "\"Speech Successes\"",
            13 => "\"Pockets Picked\"",
            14 => "\"Pants Exploded\"",
            15 => "\"Books Read\"",
            16 => "\"Health From Stimpaks\"",
            17 => "\"Weapons Created\"",
            18 => "\"Health From Food\"",
            19 => "\"Water Consumed\"",
            20 => "\"Sandman Kills\"",
            21 => "\"Paralyzing Punches\"",
            22 => "\"Robots Disabled\"",
            23 => "\"Times Slept\"",
            24 => "\"Corpses Eaten\"",
            25 => "\"Mysterious Stranger Visits\"",
            26 => "\"Doctor Bags Used\"",
            27 => "\"Challenges Completed\"",
            28 => "\"Miss Fortunate Occurrences\"",
            29 => "\"Disintegrations\"",
            30 => "\"Have Limbs Crippled\"",
            31 => "\"Speech Failures\"",
            32 => "\"Items Crafted\"",
            33 => "\"Weapon Modifications\"",
            34 => "\"Items Repaired\"",
            35 => "\"Total Things Killed\"",
            36 => "\"Dismembered Limbs\"",
            37 => "\"Caravan Games Won\"",
            38 => "\"Caravan Games Lost\"",
            39 => "\"Barter Amount Traded\"",
            40 => "\"Roulette Games Played\"",
            41 => "\"Blackjack Games Played\"",
            42 => "\"Slot Games Played\"",
            _ => $"MiscStat:{index}"
        };
    }

    #endregion

    #region Variable Reading

    private string ReadLocalVariable()
    {
        return _reader.CanRead(2) ? GetVariableName(_reader.ReadUInt16()) : "<truncated var>";
    }

    private string ReadReferenceVariable()
    {
        // Format: [refIdx:2] [innerByte:1] [...]
        // Inner byte determines what follows:
        //   0x73/0x66 = variable access: [varIdx:2]  → ref.varName
        //   0x58      = function call:   [opcode:2][callParamLen:2][paramCount:2][params...]
        if (!_reader.CanRead(3))
        {
            return _reader.CanRead(2) ? ResolveScroReference(_reader.ReadUInt16()) : "<truncated ref.var>";
        }

        var refIndex = _reader.ReadUInt16();
        var refName = ResolveScroReference(refIndex);
        var innerByte = _reader.PeekByte();

        if (innerByte is ScriptOpcodes.MarkerIntLocal or ScriptOpcodes.MarkerFloatLocal)
        {
            // ref.variable: [varMarker:1] [varIdx:2]
            _reader.ReadByte();
            if (!_reader.CanRead(2))
            {
                return $"{refName}.var?";
            }

            var varIndex = _reader.ReadUInt16();

            // Try to resolve from the referenced object's script variable list
            if (_resolveExternalVariable != null)
            {
                var refFormId = GetScroFormId(refIndex);
                if (refFormId.HasValue)
                {
                    var resolvedName = _resolveExternalVariable(refFormId.Value, varIndex);
                    if (!string.IsNullOrEmpty(resolvedName))
                    {
                        return $"{refName}.{resolvedName}";
                    }
                }
            }

            return $"{refName}.var{varIndex}";
        }

        if (innerByte == ScriptOpcodes.ExprFunctionCall)
        {
            // ref.FunctionCall: [0x58] [opcode:2] [callParamLen:2] [paramCount:2] [params...]
            _reader.ReadByte();
            _pendingRefIndex = refIndex;
            _hasPendingRef = true;
            return DecodeExpressionFunctionCall();
        }

        // Unknown inner format — just return the reference
        return refName;
    }

    private string ReadGlobalVariable()
    {
        if (!_reader.CanRead(2))
        {
            return "<truncated global>";
        }

        // Global variables are SCRO references (1-based)
        return ResolveScroReference(_reader.ReadUInt16());
    }

    private string ReadIntLiteral()
    {
        return _reader.CanRead(4) ? _reader.ReadInt32().ToString() : "<truncated int>";
    }

    /// <summary>
    ///     Read a multi-digit ASCII-encoded number (e.g., 768 → bytes 0x37 0x36 0x38).
    ///     Supports decimal points (e.g., 0.125 → 0x30 0x2E 0x31 0x32 0x35) and
    ///     negative sign prefix (0x2D).
    /// </summary>
    private string ReadAsciiNumber()
    {
        var chars = new List<char>();

        // Read consecutive digit bytes and optional decimal point.
        // Respect expression boundary to avoid reading into the next instruction.
        while (_reader.HasData && _reader.Position < _exprEnd)
        {
            var b = _reader.PeekByte();
            if (b is >= 0x30 and <= 0x39)
            {
                chars.Add((char)_reader.ReadByte());
            }
            else if (b == 0x2E && !chars.Contains('.'))
            {
                // Decimal point — only include if followed by a digit
                if (_reader.CanRead(2) && _reader.PeekByteAt(1) is >= 0x30 and <= 0x39)
                {
                    chars.Add((char)_reader.ReadByte());
                }
                else
                {
                    break;
                }
            }
            else
            {
                break;
            }
        }

        return new string(chars.ToArray());
    }

    private string ReadDoubleLiteral(ScriptParamType? expectedType = null)
    {
        if (!_reader.CanRead(8))
        {
            return "<truncated double>";
        }

        var value = _reader.ReadDouble();

        // Only try FormID resolution when the parameter is NOT a known numeric type.
        // Float/Int params are plain numbers; reference types may encode FormIDs in doubles
        // via Script::PutNumericIDInDouble.
        if (expectedType is ScriptParamType.Float or ScriptParamType.Int or ScriptParamType.VatsValueData)
        {
            return FormatDouble(value);
        }

        return TryResolveDoubleAsFormId(value) ?? FormatDouble(value);
    }

    #endregion

    #region Variable/Reference Resolution

    /// <summary>
    ///     Read a variable reference for the Set target (marker byte + index).
    /// </summary>
    private string ReadVariableReference()
    {
        if (!_reader.HasData)
        {
            return "<truncated var ref>";
        }

        var marker = _reader.ReadByte();

        switch (marker)
        {
            case ScriptOpcodes.MarkerIntLocal:
            case ScriptOpcodes.MarkerFloatLocal:
                return ReadLocalVariable();

            case ScriptOpcodes.MarkerReference:
                return ReadReferenceVariable();

            case ScriptOpcodes.MarkerGlobal:
                return ReadGlobalVariable();

            default:
                // Unknown marker — might be a direct index
                if (_reader.CanRead(1))
                {
                    var idx = (ushort)((marker << 8) | _reader.ReadByte());
                    if (!_isBigEndian)
                    {
                        idx = (ushort)((idx >> 8) | ((idx & 0xFF) << 8));
                    }

                    return GetVariableName(idx);
                }

                return $"<unknown var marker 0x{marker:X2}>";
        }
    }

    private string GetVariableName(uint index)
    {
        var variable = _variables.FirstOrDefault(v => v.Index == index);
        return variable?.Name ?? $"var{index}";
    }

    /// <summary>
    ///     Resolve a 1-based SCRO index to a FormID name or hex string.
    /// </summary>
    private string ResolveScroReference(ushort index)
    {
        if (index == 0)
        {
            return "0"; // Null/player reference
        }

        // SCRO is 1-based in bytecode
        var listIndex = index - 1;
        if (listIndex < _referencedObjects.Count)
        {
            var value = _referencedObjects[listIndex];

            // SCRV entries (local variable references) are flagged with high bit.
            // The variable index is in the lower 31 bits.
            if ((value & 0x80000000) != 0)
            {
                var varIndex = value & 0x7FFFFFFF;
                return GetVariableName(varIndex);
            }

            // Well-known FormID: player reference
            if (value == 0x00000014)
            {
                return "player";
            }

            var name = _resolveFormName(value);
            return !string.IsNullOrEmpty(name) ? name : $"0x{value:X8}";
        }

        return $"SCRO[{index}]";
    }

    /// <summary>
    ///     Get the FormID for a 1-based SCRO index without resolving to a name.
    /// </summary>
    private uint? GetScroFormId(ushort index)
    {
        if (index == 0)
        {
            return null;
        }

        var listIndex = index - 1;
        if (listIndex >= _referencedObjects.Count)
        {
            return null;
        }

        var value = _referencedObjects[listIndex];
        // Skip SCRV entries (flagged with high bit) — they're local variables, not form references
        return (value & 0x80000000) != 0 ? null : value;
    }

    /// <summary>
    ///     Check if a double value might be a packed FormID (PutNumericIDInDouble).
    /// </summary>
    private string? TryResolveDoubleAsFormId(double value)
    {
        // Use range check instead of exact equality for floor comparison
        if (value <= 0 || value > uint.MaxValue)
        {
            return null;
        }

        var floor = Math.Floor(value);
        if (Math.Abs(value - floor) > 0.001)
        {
            return null;
        }

        var possibleFormId = (uint)floor;
        return _resolveFormName(possibleFormId);
    }

    #endregion

    #region Formatting Helpers

    private void AppendLine(string line)
    {
        if (_indentLevel > 0)
        {
            _output.Append(new string(' ', _indentLevel * 2));
        }

        _output.AppendLine(line);
    }

    private static string FormatDouble(double value)
    {
        if (Math.Abs(value) < double.Epsilon)
        {
            return "0";
        }

        var floor = Math.Floor(value);
        if (Math.Abs(value - floor) < 0.0001 && Math.Abs(value) < 1e15)
        {
            // GECK writes integer-valued numbers without decimal point (e.g., "100" not "100.0")
            return ((long)value).ToString();
        }

        return value.ToString("G");
    }

    private static string GetBlockTypeName(ushort blockType)
    {
        // Block type IDs from GECK wiki: https://geckwiki.com/index.php/Begin
        return blockType switch
        {
            0 => "GameMode",
            1 => "MenuMode",
            2 => "OnActivate",
            3 => "OnAdd",
            4 => "OnEquip",
            5 => "OnUnequip",
            6 => "OnDrop",
            7 => "SayToDone",
            8 => "OnHit",
            9 => "OnHitWith",
            10 => "OnDeath",
            11 => "OnMurder",
            12 => "OnCombatEnd",
            13 => "Function",
            15 => "OnPackageStart",
            16 => "OnPackageDone",
            17 => "ScriptEffectStart",
            18 => "ScriptEffectFinish",
            19 => "ScriptEffectUpdate",
            20 => "OnPackageChange",
            21 => "OnLoad",
            22 => "OnMagicEffectHit",
            23 => "OnSell",
            24 => "OnTrigger",
            25 => "OnStartCombat",
            26 => "OnTriggerEnter",
            27 => "OnTriggerLeave",
            28 => "OnActorEquip",
            29 => "OnActorUnequip",
            30 => "OnReset",
            31 => "OnOpen",
            32 => "OnClose",
            33 => "OnGrab",
            34 => "OnRelease",
            35 => "OnDestructionStageChange",
            36 => "OnFire",
            37 => "OnNPCActivate",
            _ => $"BlockType:{blockType:X4}"
        };
    }

    private static string GetActorValueName(ushort index)
    {
        return index switch
        {
            0 => "Aggression",
            1 => "Confidence",
            2 => "Energy",
            3 => "Responsibility",
            4 => "Mood",
            5 => "Strength",
            6 => "Perception",
            7 => "Endurance",
            8 => "Charisma",
            9 => "Intelligence",
            10 => "Agility",
            11 => "Luck",
            12 => "ActionPoints",
            13 => "CarryWeight",
            14 => "CritChance",
            15 => "HealRate",
            16 => "Health",
            17 => "MeleeDamage",
            18 => "DamageResistance",
            19 => "PoisonResistance",
            20 => "RadResistance",
            21 => "SpeedMult",
            22 => "Fatigue",
            23 => "Karma",
            24 => "XP",
            25 => "PerceptionCondition",
            26 => "EnduranceCondition",
            27 => "LeftAttackCondition",
            28 => "RightAttackCondition",
            29 => "LeftMobilityCondition",
            30 => "RightMobilityCondition",
            31 => "BrainCondition",
            32 => "Barter",
            33 => "BigGuns",
            34 => "EnergyWeapons",
            35 => "Explosives",
            36 => "Lockpick",
            37 => "Medicine",
            38 => "MeleeWeapons",
            39 => "Repair",
            40 => "Science",
            41 => "Guns",
            42 => "Sneak",
            43 => "Speech",
            44 => "Survival",
            45 => "Unarmed",
            // Actor values 46+ from GECK wiki Actor_Value_Codes
            46 => "InventoryWeight",
            47 => "Paralysis",
            48 => "Invisibility",
            49 => "Chameleon",
            50 => "NightEye",
            51 => "Turbo",
            52 => "FireResist",
            53 => "WaterBreathing",
            54 => "RadiationRads",
            55 => "BloodyMess",
            56 => "UnarmedDamage",
            57 => "Assistance",
            58 => "ElectricResist",
            59 => "FrostResist",
            60 => "EnergyResist",
            61 => "EmpResist",
            62 => "Variable01",
            63 => "Variable02",
            64 => "Variable03",
            65 => "Variable04",
            66 => "Variable05",
            67 => "Variable06",
            68 => "Variable07",
            69 => "Variable08",
            70 => "Variable09",
            71 => "Variable10",
            72 => "IgnoreCrippledLimbs",
            73 => "Dehydration",
            74 => "Hunger",
            75 => "SleepDeprevation",
            76 => "DamageThreshold",
            _ => $"ActorValue:{index}"
        };
    }

    #endregion
}
