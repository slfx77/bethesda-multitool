using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;

namespace FalloutXbox360Utils.Core.Formats.Esm.Script;

/// <summary>
///     Reads variable references, literals, and SCRO references from script bytecode.
///     Shared by <see cref="ScriptDecompiler" />, <see cref="ScriptExpressionDecoder" />,
///     and <see cref="ScriptStatementDecoder" />.
/// </summary>
internal sealed class ScriptVariableReader
{
    private readonly bool _isBigEndian;
    private readonly List<uint> _referencedObjects;
    private readonly Func<uint, ushort, string?>? _resolveExternalVariable;
    private readonly Func<uint, string?> _resolveFormName;
    private readonly List<ScriptVariableInfo> _variables;

    public ScriptVariableReader(
        List<ScriptVariableInfo> variables,
        List<uint> referencedObjects,
        Func<uint, string?> resolveFormName,
        bool isBigEndian,
        Func<uint, ushort, string?>? resolveExternalVariable)
    {
        _variables = variables;
        _referencedObjects = referencedObjects;
        _resolveFormName = resolveFormName;
        _isBigEndian = isBigEndian;
        _resolveExternalVariable = resolveExternalVariable;
    }

    /// <summary>Current expression boundary (prevents over-reading ASCII numbers).</summary>
    internal int ExprEnd { get; set; }

    /// <summary>Pending SetRef state: whether a SetRef opcode was just consumed.</summary>
    internal bool HasPendingRef { get; set; }

    /// <summary>Pending SetRef state: the 1-based SCRO index from the last SetRef opcode.</summary>
    internal ushort PendingRefIndex { get; set; }

    /// <summary>
    ///     Back-reference to the expression decoder, needed by
    ///     <see cref="ReadReferenceVariable" /> when a ref.FunctionCall is encountered.
    /// </summary>
    internal ScriptExpressionDecoder? ExprDecoder { get; set; }

    /// <summary>
    ///     Back-reference to the statement decoder, needed by
    ///     <see cref="ScriptExpressionDecoder.DecodeExpressionFunctionCall" /> for parameter list decoding.
    /// </summary>
    internal ScriptStatementDecoder? StmtDecoder { get; set; }

    #region Formatting

    internal static string FormatDouble(double value)
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

    #endregion

    #region Variable Reading

    internal string ReadLocalVariable(BytecodeReader reader)
    {
        return reader.CanRead(2) ? GetVariableName(reader.ReadUInt16()) : "<truncated var>";
    }

    internal string ReadReferenceVariable(BytecodeReader reader)
    {
        // Format: [refIdx:2] [innerByte:1] [...]
        // Inner byte determines what follows:
        //   0x73/0x66 = variable access: [varIdx:2]  -> ref.varName
        //   0x58      = function call:   [opcode:2][callParamLen:2][paramCount:2][params...]
        if (!reader.CanRead(3))
        {
            return reader.CanRead(2) ? ResolveScroReference(reader.ReadUInt16()) : "<truncated ref.var>";
        }

        var refIndex = reader.ReadUInt16();
        var refName = ResolveScroReference(refIndex);
        var innerByte = reader.PeekByte();

        if (innerByte is ScriptOpcodes.MarkerIntLocal or ScriptOpcodes.MarkerFloatLocal)
        {
            // ref.variable: [varMarker:1] [varIdx:2]
            reader.ReadByte();
            if (!reader.CanRead(2))
            {
                return $"{refName}.var?";
            }

            var varIndex = reader.ReadUInt16();

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
            reader.ReadByte();
            PendingRefIndex = refIndex;
            HasPendingRef = true;
            return ExprDecoder!.DecodeExpressionFunctionCall(reader);
        }

        // Unknown inner format -- just return the reference
        return refName;
    }

    internal string ReadGlobalVariable(BytecodeReader reader)
    {
        if (!reader.CanRead(2))
        {
            return "<truncated global>";
        }

        // Global variables are SCRO references (1-based)
        return ResolveScroReference(reader.ReadUInt16());
    }

    internal static string ReadIntLiteral(BytecodeReader reader)
    {
        return reader.CanRead(4) ? reader.ReadInt32().ToString() : "<truncated int>";
    }

    /// <summary>
    ///     Read a multi-digit ASCII-encoded number (e.g., 768 -> bytes 0x37 0x36 0x38).
    ///     Supports decimal points (e.g., 0.125 -> 0x30 0x2E 0x31 0x32 0x35) and
    ///     negative sign prefix (0x2D).
    /// </summary>
    internal string ReadAsciiNumber(BytecodeReader reader)
    {
        var chars = new List<char>();

        // Read consecutive digit bytes and optional decimal point.
        // Respect expression boundary to avoid reading into the next instruction.
        while (reader.HasData && reader.Position < ExprEnd)
        {
            var b = reader.PeekByte();
            if (b is >= 0x30 and <= 0x39)
            {
                chars.Add((char)reader.ReadByte());
            }
            else if (b == 0x2E && !chars.Contains('.'))
            {
                // Decimal point -- only include if followed by a digit
                if (reader.CanRead(2) && reader.PeekByteAt(1) is >= 0x30 and <= 0x39)
                {
                    chars.Add((char)reader.ReadByte());
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

    internal string ReadDoubleLiteral(BytecodeReader reader, ScriptParamType? expectedType = null)
    {
        if (!reader.CanRead(8))
        {
            return "<truncated double>";
        }

        var value = reader.ReadDouble();

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
    internal string ReadVariableReference(BytecodeReader reader)
    {
        if (!reader.HasData)
        {
            return "<truncated var ref>";
        }

        var marker = reader.ReadByte();

        switch (marker)
        {
            case ScriptOpcodes.MarkerIntLocal:
            case ScriptOpcodes.MarkerFloatLocal:
                return ReadLocalVariable(reader);

            case ScriptOpcodes.MarkerReference:
                return ReadReferenceVariable(reader);

            case ScriptOpcodes.MarkerGlobal:
                return ReadGlobalVariable(reader);

            default:
                // Unknown marker -- might be a direct index
                if (reader.CanRead(1))
                {
                    var idx = (ushort)((marker << 8) | reader.ReadByte());
                    if (!_isBigEndian)
                    {
                        idx = (ushort)((idx >> 8) | ((idx & 0xFF) << 8));
                    }

                    return GetVariableName(idx);
                }

                return $"<unknown var marker 0x{marker:X2}>";
        }
    }

    internal string GetVariableName(uint index)
    {
        var variable = _variables.FirstOrDefault(v => v.Index == index);
        return variable?.Name ?? $"var{index}";
    }

    /// <summary>
    ///     Resolve a 1-based SCRO index to a FormID name or hex string.
    /// </summary>
    internal string ResolveScroReference(ushort index)
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
    ///     Consume a pending SetRef and return the "RefName." prefix, or empty string.
    /// </summary>
    internal string ConsumePendingRef()
    {
        if (!HasPendingRef)
        {
            return "";
        }

        HasPendingRef = false;
        return ResolveScroReference(PendingRefIndex) + ".";
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
        // Skip SCRV entries (flagged with high bit) -- they're local variables, not form references
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
}
