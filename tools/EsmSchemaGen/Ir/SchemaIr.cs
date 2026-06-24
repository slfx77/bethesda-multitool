namespace EsmSchemaGen.Ir;

/// <summary>
///     Normalized intermediate representation of an xEdit record definition. The Pascal DSL parser
///     (<see cref="Pascal.WbExprParser" />) produces a generic call AST, the <see cref="IrBuilder" />
///     lowers it into this IR, and the emitter (later stage) writes it as C# schema-data tables that a
///     single generic reader/writer interprets at runtime. Records are immutable.
/// </summary>
public abstract record SchemaNode
{
    /// <summary>Human-readable label from the DSL (the descriptive name argument).</summary>
    public string? Name { get; init; }

    /// <summary>Set when the member carried <c>.SetRequired</c> / <c>aRequired</c>.</summary>
    public bool Required { get; init; }

    /// <summary>Native default from <c>.SetDefaultNativeValue(n)</c>, if any.</summary>
    public long? DefaultValue { get; init; }
}

/// <summary>A top-level record: a 4-char signature plus its ordered members.</summary>
public sealed record RecordDef(string Signature, IReadOnlyList<MemberDef> Members) : SchemaNode;

/// <summary>
///     A record member. <see cref="Signature" /> is the 4-char subrecord signature when this member
///     owns its own framed subrecord (e.g. a <c>wbStruct(DATA, …)</c>); it is null for an inline field
///     inside a struct (the DSL uses the same builders for both).
/// </summary>
public abstract record MemberDef : SchemaNode
{
    public string? Signature { get; init; }
}

/// <summary>A scalar value field (integer, float, string, byte array).</summary>
public sealed record FieldDef(PrimType Type) : MemberDef
{
    /// <summary>Fixed byte width for strings/byte-arrays where the DSL pins one; null = variable/implied.</summary>
    public int? FixedSize { get; init; }

    /// <summary>Inline anonymous enum from <c>wbEnum([...])</c> on this integer field.</summary>
    public EnumDef? InlineEnum { get; init; }

    /// <summary>Inline anonymous flags from <c>wbFlags([...])</c> on this integer field.</summary>
    public FlagsDef? InlineFlags { get; init; }

    /// <summary>Name of a referenced enum/flags symbol (resolved against the game's enum table later).</summary>
    public string? EnumRefName { get; init; }
}

/// <summary>A 4-byte FormID, optionally type-checked against a set of allowed target signatures.</summary>
public sealed record FormIdDef : MemberDef
{
    public IReadOnlyList<string> Targets { get; init; } = [];
}

/// <summary>A struct: an ordered sequence of members. Has its own signature when rooted on a subrecord.</summary>
public sealed record StructDef(IReadOnlyList<MemberDef> Members) : MemberDef;

/// <summary>An array of a single element type.</summary>
public sealed record ArrayDef(MemberDef Element) : MemberDef
{
    /// <summary>&gt;0 = fixed count; -1 = length-prefixed; 0 = greedy (repeat until the stream/signature changes).</summary>
    public int Count { get; init; }

    /// <summary>Name of a sibling field that supplies the count, when the count is dynamic.</summary>
    public string? CountRef { get; init; }
}

/// <summary>A tagged union: the active variant is chosen at runtime by a named decider function.</summary>
public sealed record UnionDef(string DeciderName, IReadOnlyList<MemberDef> Variants) : MemberDef;

/// <summary>Padding/unused bytes (<c>wbUnused(N)</c>): consumed on read, preserved verbatim, no value.</summary>
public sealed record UnusedDef(int Size) : MemberDef;

/// <summary>A zero-length marker subrecord (<c>wbEmpty</c>).</summary>
public sealed record EmptyDef : MemberDef;

/// <summary>
///     A DSL construct the IR builder does not model yet (an unmapped <c>wb*</c> call or symbol). Kept
///     so parsing never throws mid-record; surfaced in the coverage report so the gaps are visible.
/// </summary>
public sealed record UnknownMemberDef(string CallName) : MemberDef;

/// <summary>A named or anonymous enumeration: integer value -> label.</summary>
public sealed record EnumDef(string? Name, IReadOnlyList<EnumMember> Members);

public sealed record EnumMember(long Value, string Label);

/// <summary>A named or anonymous bit-flag set: bit index -> label.</summary>
public sealed record FlagsDef(string? Name, IReadOnlyList<FlagMember> Bits);

public sealed record FlagMember(int Bit, string Label);

/// <summary>Primitive value kinds, mapped from the DSL <c>itXX</c> / <c>wbFloat</c> / <c>wbString</c> etc.</summary>
public enum PrimType
{
    U8,
    S8,
    U16,
    S16,
    U24,
    U32,
    S32,
    U64,
    S64,
    Float,
    Double,
    Half,
    FormId,
    ZString,
    LString,
    StringKC,
    ByteArray
}
