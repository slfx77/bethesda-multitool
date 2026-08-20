using System.Collections.Immutable;

namespace BethesdaMultitool.Core.Formats.Papyrus;

/// <summary>The byte order selected by the PEX magic number.</summary>
public enum PexEndianness
{
    Big,
    Little
}

/// <summary>Game identifiers stored in the PEX header.</summary>
public enum PexGameId : ushort
{
    Skyrim = 1,
    Fallout4 = 2,
    Fallout76 = 3
}

/// <summary>Immutable representation of a Skyrim, Fallout 4, or Fallout 76 PEX file.</summary>
public sealed record PexFile(
    PexHeader Header,
    ImmutableArray<string> StringTable,
    PexDebugInfo? DebugInfo,
    ImmutableArray<PexUserFlag> UserFlags,
    ImmutableArray<PexObject> Objects,
    int BytesConsumed);

public sealed record PexHeader(
    PexEndianness Endianness,
    byte MajorVersion,
    byte MinorVersion,
    PexGameId GameId,
    long CompilationTime,
    string SourceFileName,
    string UserName,
    string ComputerName);

/// <summary>A checked index into <see cref="PexFile.StringTable" /> and its resolved value.</summary>
public readonly record struct PexStringReference(ushort Index, string Value)
{
    public override string ToString()
    {
        return Value;
    }
}

public sealed record PexDebugInfo(
    long ModificationTime,
    ImmutableArray<PexDebugFunction> Functions,
    ImmutableArray<PexDebugPropertyGroup> PropertyGroups,
    ImmutableArray<PexDebugStructOrder> StructOrders);

public enum PexDebugFunctionType : byte
{
    Normal = 0,
    Getter = 1,
    Setter = 2
}

public sealed record PexDebugFunction(
    PexStringReference ObjectName,
    PexStringReference StateName,
    PexStringReference FunctionName,
    PexDebugFunctionType Type,
    ImmutableArray<ushort> InstructionLineMap);

public sealed record PexDebugPropertyGroup(
    PexStringReference ObjectName,
    PexStringReference GroupName,
    PexStringReference DocumentationString,
    uint UserFlags,
    ImmutableArray<PexStringReference> Properties);

public sealed record PexDebugStructOrder(
    PexStringReference ObjectName,
    PexStringReference StructName,
    ImmutableArray<PexStringReference> Members);

public sealed record PexUserFlag(PexStringReference Name, byte BitIndex);

public sealed record PexObject(
    PexStringReference Name,
    uint DeclaredSize,
    PexStringReference ParentClassName,
    PexStringReference DocumentationString,
    bool IsConst,
    uint UserFlags,
    PexStringReference AutoStateName,
    ImmutableArray<PexStruct> Structs,
    ImmutableArray<PexVariable> Variables,
    ImmutableArray<PexProperty> Properties,
    ImmutableArray<PexState> States)
{
    /// <summary>
    ///     Ordered references stored after the state table by Fallout 76 v3.15. Retail targets
    ///     resolve to state names in the same object; the table's runtime purpose and ordering
    ///     semantics remain unknown.
    /// </summary>
    public ImmutableArray<PexStringReference> Fallout76TrailingStateReferences { get; init; } = [];

    /// <summary>
    ///     Whether the Fallout 76 trailing state-reference count field was physically present.
    ///     This distinguishes an explicit zero-count retail table from a writer that omits it.
    /// </summary>
    public bool HasFallout76TrailingStateReferenceTable { get; init; }
}

public sealed record PexStruct(
    PexStringReference Name,
    ImmutableArray<PexStructMember> Members);

public sealed record PexStructMember(
    PexStringReference Name,
    PexStringReference TypeName,
    uint UserFlags,
    PexValue DefaultValue,
    bool IsConst,
    PexStringReference DocumentationString);

public sealed record PexVariable(
    PexStringReference Name,
    PexStringReference TypeName,
    uint UserFlags,
    PexValue DefaultValue,
    bool IsConst);

[Flags]
public enum PexPropertyFlags : byte
{
    None = 0,
    Readable = 1 << 0,
    Writable = 1 << 1,
    Auto = 1 << 2
}

public sealed record PexProperty(
    PexStringReference Name,
    PexStringReference TypeName,
    PexStringReference DocumentationString,
    uint UserFlags,
    PexPropertyFlags Flags,
    PexStringReference? AutoVariableName,
    PexFunction? ReadFunction,
    PexFunction? WriteFunction);

public sealed record PexState(
    PexStringReference Name,
    ImmutableArray<PexFunction> Functions);

[Flags]
public enum PexFunctionFlags : byte
{
    None = 0,
    Global = 1 << 0,
    Native = 1 << 1
}

public sealed record PexFunction(
    PexStringReference? Name,
    PexStringReference ReturnTypeName,
    PexStringReference DocumentationString,
    uint UserFlags,
    PexFunctionFlags Flags,
    ImmutableArray<PexTypedName> Parameters,
    ImmutableArray<PexTypedName> LocalVariables,
    ImmutableArray<PexInstruction> Instructions)
{
    /// <summary>The complete function-flag byte, including bits whose meaning is not mapped.</summary>
    public byte RawFlags => (byte)Flags;

    /// <summary>Raw flag bits other than the public Global and Native bits.</summary>
    public byte UnmappedFlags => (byte)(RawFlags &
                                        ~(byte)(PexFunctionFlags.Global | PexFunctionFlags.Native));
}

public readonly record struct PexTypedName(
    PexStringReference Name,
    PexStringReference TypeName);

/// <summary>
///     On-disk opcodes. Skyrim ends at <see cref="ArrayRFindElement" />, Fallout 4 at
///     <see cref="ArrayClear" />, and Fallout 76 at <see cref="ArrayGetAllMatchingStructs" />.
/// </summary>
public enum PexOpCode : byte
{
    Nop = 0,
    IAdd = 1,
    FAdd = 2,
    ISubtract = 3,
    FSubtract = 4,
    IMultiply = 5,
    FMultiply = 6,
    IDivide = 7,
    FDivide = 8,
    IMod = 9,
    Not = 10,
    INegate = 11,
    FNegate = 12,
    Assign = 13,
    Cast = 14,
    CompareEqual = 15,
    CompareLessThan = 16,
    CompareLessThanOrEqual = 17,
    CompareGreaterThan = 18,
    CompareGreaterThanOrEqual = 19,
    Jump = 20,
    JumpTrue = 21,
    JumpFalse = 22,
    CallMethod = 23,
    CallParent = 24,
    CallStatic = 25,
    Return = 26,
    StringConcat = 27,
    PropertyGet = 28,
    PropertySet = 29,
    ArrayCreate = 30,
    ArrayLength = 31,
    ArrayGetElement = 32,
    ArraySetElement = 33,
    ArrayFindElement = 34,
    ArrayRFindElement = 35,
    Is = 36,
    StructCreate = 37,
    StructGet = 38,
    StructSet = 39,
    ArrayFindStruct = 40,
    ArrayRFindStruct = 41,
    ArrayAdd = 42,
    ArrayInsert = 43,
    ArrayRemoveLast = 44,
    ArrayRemove = 45,
    ArrayClear = 46,
    ArrayGetAllMatchingStructs = 47
}

public sealed record PexInstruction(
    PexOpCode OpCode,
    ImmutableArray<PexValue> Arguments,
    ImmutableArray<PexValue> VariadicArguments);

public enum PexValueType : byte
{
    None = 0,
    Identifier = 1,
    String = 2,
    Integer = 3,
    Float = 4,
    Boolean = 5
}

public abstract record PexValue(PexValueType Type);

public sealed record PexNoneValue() : PexValue(PexValueType.None);

public sealed record PexIdentifierValue(PexStringReference Identifier)
    : PexValue(PexValueType.Identifier);

public sealed record PexStringValue(PexStringReference String)
    : PexValue(PexValueType.String);

public sealed record PexIntegerValue(int Value) : PexValue(PexValueType.Integer);

public sealed record PexFloatValue(float Value) : PexValue(PexValueType.Float);

public sealed record PexBooleanValue(bool Value) : PexValue(PexValueType.Boolean);
