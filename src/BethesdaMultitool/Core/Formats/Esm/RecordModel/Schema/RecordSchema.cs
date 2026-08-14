namespace BethesdaMultitool.Core.Formats.Esm.RecordModel.Schema;

/// <summary>
///     Runtime representation of a record-layout schema, generated from the xEdit (TES5Edit) record
///     definitions by the <c>tools/EsmSchemaGen</c> code generator. The generated per-game
///     <c>*Schema.g.cs</c> files instantiate these types; the schema-driven reader interprets them to
///     decode records. A future generic writer could share the model, but none exists today. These are
///     pure, immutable data — no behavior — so the same types serve every game. The generator carries
///     an internal mirror of these shapes in <c>EsmSchemaGen.Ir</c>;
///     explicitly regenerated output compiles against this runtime model, exposing shape drift during
///     the reviewed regeneration/build rather than through an automatic clean-CI oracle check.
/// </summary>
public abstract record SchemaNode
{
    /// <summary>Human-readable label from the definition (the descriptive name argument).</summary>
    public string? Name { get; init; }

    /// <summary>Set when the member was marked required in the definition.</summary>
    public bool Required { get; init; }

    /// <summary>Native default value, when the definition declared one.</summary>
    public long? DefaultValue { get; init; }
}

/// <summary>A top-level record: a 4-char signature plus its ordered members.</summary>
public sealed record RecordDef(string Signature, IReadOnlyList<MemberDef> Members) : SchemaNode;

/// <summary>
///     A record member. <see cref="Signature" /> is the 4-char subrecord signature when this member owns
///     its own framed subrecord; null for an inline field inside a struct.
/// </summary>
public abstract record MemberDef : SchemaNode
{
    public string? Signature { get; init; }

    /// <summary>
    ///     Minimum record-header form version required for this member, from <c>wbFromVersion</c> or
    ///     the upper arm of a literal two-way <c>wbFormVersionDecider</c>. Null means no lower gate.
    /// </summary>
    public ushort? MinFormVersion { get; init; }

    /// <summary>
    ///     Exclusive record-header form-version ceiling for this member, from <c>wbBelowVersion</c> or
    ///     the lower arm of a literal two-way <c>wbFormVersionDecider</c>. Null means no upper gate.
    /// </summary>
    public ushort? MaxFormVersionExclusive { get; init; }
}

/// <summary>A scalar value field (integer, float, string, byte array).</summary>
public sealed record FieldDef(PrimType Type) : MemberDef
{
    /// <summary>Fixed byte width where the definition pins one (e.g. a fixed-length string); null = variable.</summary>
    public int? FixedSize { get; init; }

    /// <summary>Inline anonymous enum on this integer field.</summary>
    public EnumDef? InlineEnum { get; init; }

    /// <summary>Inline anonymous flags on this integer field.</summary>
    public FlagsDef? InlineFlags { get; init; }

    /// <summary>Name of a referenced enum/flags symbol, resolved against the game's enum/flags tables.</summary>
    public string? EnumRefName { get; init; }
}

/// <summary>A 4-byte FormID, optionally type-checked against a set of allowed target signatures.</summary>
public sealed record FormIdDef : MemberDef
{
    public IReadOnlyList<string> Targets { get; init; } = [];
}

/// <summary>A struct: an ordered sequence of members; rooted on a subrecord when it has a signature.</summary>
public sealed record StructDef(IReadOnlyList<MemberDef> Members) : MemberDef;

/// <summary>An array of a single element type.</summary>
public sealed record ArrayDef(MemberDef Element) : MemberDef
{
    /// <summary>&gt;0 = fixed count; -1 = length-prefixed; 0 = greedy (repeat until the stream/signature changes).</summary>
    public int Count { get; init; }

    /// <summary>Name of a sibling field supplying the count, when the count is dynamic.</summary>
    public string? CountRef { get; init; }
}

/// <summary>A tagged union whose active variant is chosen at runtime by a named decider.</summary>
public sealed record UnionDef(string DeciderName, IReadOnlyList<MemberDef> Variants) : MemberDef;

/// <summary>Padding/unused bytes: consumed on read without producing an individually retained value.</summary>
public sealed record UnusedDef(int Size) : MemberDef;

/// <summary>A zero-length marker subrecord.</summary>
public sealed record EmptyDef : MemberDef;

/// <summary>
///     A member the generator could not model yet (an unmapped builder/helper). Within an inline struct,
///     the reader exposes the remaining undecodable tail as one raw node and stops; it does not capture a
///     bounded member value or provide a generic round-trip writer.
/// </summary>
public sealed record RawMemberDef(string Builder) : MemberDef;

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
