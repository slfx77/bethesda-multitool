namespace BethesdaMultitool.Core.Formats.Esm.RecordModel.Decoding;

/// <summary>
///     One node in the ordered, labeled tree produced by <see cref="SchemaRecordDecoder" /> from a raw
///     record and its <see cref="Schema.RecordDef" />. The tree mirrors the schema's member order, so it
///     re-serializes faithfully and renders the same way the rich FNV typed presenters do — a label, a
///     display value, optional children (structs/arrays), and a FormID when the node is a reference.
/// </summary>
public sealed record DecodedNode
{
    /// <summary>Human-readable field label (the schema member's descriptive name, or its signature).</summary>
    public required string Label { get; init; }

    /// <summary>Display string for the value; null for container nodes (structs/arrays).</summary>
    public string? Value { get; init; }

    /// <summary>Boxed decoded value (int/float/string/uint), for typed consumers that walk the tree.</summary>
    public object? RawValue { get; init; }

    /// <summary>Set when this node is a FormID reference, so the GUI can render it as a clickable link.</summary>
    public uint? FormId { get; init; }

    /// <summary>The 4-char subrecord signature this node was decoded from, when it owns one.</summary>
    public string? Signature { get; init; }

    /// <summary>
    ///     True when the bytes were preserved verbatim rather than structurally decoded — an unmatched
    ///     subrecord, an opaque <see cref="Schema.RawMemberDef" />, or a tail the schema does not model.
    ///     Surfacing these keeps coverage gaps visible instead of silently dropped.
    /// </summary>
    public bool IsRaw { get; init; }

    /// <summary>Child nodes for struct and array members; empty for leaf scalars.</summary>
    public IReadOnlyList<DecodedNode> Children { get; init; } = [];
}
