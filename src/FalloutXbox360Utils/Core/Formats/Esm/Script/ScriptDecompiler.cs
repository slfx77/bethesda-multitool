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
    private readonly string? _scriptName = scriptName;
    private ScriptExpressionDecoder? _exprDecoder;
    private int _indentLevel;

    // State during decompilation
    private BytecodeReader _reader = null!;
    private ScriptStatementDecoder? _stmtDecoder;

    // Sub-components (initialized on first Decompile call)
    private ScriptVariableReader? _varReader;

    /// <summary>
    ///     Decompile compiled bytecode to GECK script source text.
    ///     Never throws — returns partial output with error comments on failure.
    /// </summary>
    public string Decompile(byte[] compiledData)
    {
        _output.Clear();
        _indentLevel = 0;
        _reader = new BytecodeReader(compiledData, _isBigEndian);

        if (_varReader == null)
        {
            _varReader = new ScriptVariableReader(
                variables, referencedObjects, resolveFormName, _isBigEndian, resolveExternalVariable);
            _exprDecoder = new ScriptExpressionDecoder(_varReader);
            _stmtDecoder = new ScriptStatementDecoder(_varReader);
            _varReader.ExprDecoder = _exprDecoder;
            _varReader.StmtDecoder = _stmtDecoder;
        }

        _varReader.HasPendingRef = false;
        _varReader.ExprEnd = compiledData.Length;

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

    #region Formatting Helpers

    private void AppendLine(string line)
    {
        if (_indentLevel > 0)
        {
            _output.Append(new string(' ', _indentLevel * 2));
        }

        _output.AppendLine(line);
    }

    #endregion

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
                _varReader!.PendingRefIndex = paramLen;
                _varReader.HasPendingRef = true;
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
        var blockName = ScriptStatementDecoder.GetBlockTypeName(blockType);

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
                    eventParams.Add(_stmtDecoder!.DecodeFunctionParameter(_reader, null));
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
        var targetVar = _varReader!.ReadVariableReference(_reader);

        if (!_reader.CanRead(2))
        {
            AppendLine("; Set: truncated before expression length");
            return;
        }

        var exprLen = _reader.ReadUInt16();
        var exprEnd = _reader.Position + exprLen;
        var expr = _exprDecoder!.DecodeExpression(_reader, exprEnd);

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

        var expr = _exprDecoder!.DecodeExpression(_reader, exprEnd);
        AppendLine($"{keyword} {expr}");
        _indentLevel++;
    }

    #endregion

    #region Function Call Handling

    private void HandleFunctionCall(ushort opcode, int paramLen)
    {
        var funcDef = ScriptFunctionTable.Get(opcode);
        var funcName = GetFunctionDisplayName(funcDef, opcode);

        var prefix = _varReader!.ConsumePendingRef();

        if (paramLen < 2)
        {
            AppendLine($"{prefix}{funcName}");
            return;
        }

        var paramCount = _reader.ReadUInt16();
        var paramStrings = DecodeParameterList(_reader, funcDef, paramCount);

        AppendLine(paramStrings.Count > 0
            ? $"{prefix}{funcName} {string.Join(" ", paramStrings)}"
            : $"{prefix}{funcName}");
    }

    internal static string GetFunctionDisplayName(ScriptFunctionDef? funcDef, ushort opcode)
    {
        if (funcDef == null)
        {
            return ScriptFunctionTable.GetName(opcode);
        }

        // GECK scripts use both short and long names interchangeably.
        // Prefer short name when available as it's more common in hand-written scripts.
        return !string.IsNullOrEmpty(funcDef.ShortName) ? funcDef.ShortName : funcDef.Name;
    }

    private List<string> DecodeParameterList(BytecodeReader reader, ScriptFunctionDef? funcDef, int paramCount)
    {
        var paramStrings = new List<string>();
        for (var i = 0; i < paramCount && reader.HasData; i++)
        {
            var expectedType = funcDef != null && i < funcDef.Params.Length
                ? funcDef.Params[i].Type
                : (ScriptParamType?)null;

            paramStrings.Add(_stmtDecoder!.DecodeFunctionParameter(reader, expectedType));
        }

        return paramStrings;
    }

    #endregion
}
