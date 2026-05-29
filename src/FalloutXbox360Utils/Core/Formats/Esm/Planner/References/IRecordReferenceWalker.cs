using System.Collections.Immutable;

namespace FalloutXbox360Utils.Core.Formats.Esm.Planner.References;

/// <summary>
///     Per-record-type walker that enumerates every outgoing FormID reference on a record.
///     One implementation per ported record type (SCPT, PACK, INFO, REFR, NPC_, …).
///     The <see cref="ReferenceResolver" /> dispatches to these by record type and applies
///     <see cref="DegradationPolicy" /> to each yielded reference to produce a final
///     <see cref="ResolvedRef" />.
/// </summary>
public interface IRecordReferenceWalker
{
    /// <summary>The 4-character record signature this walker handles.</summary>
    string RecordType { get; }

    /// <summary>The CLR type of the record model this walker accepts.</summary>
    Type ModelType { get; }

    /// <summary>
    ///     Enumerate raw outgoing references — the resolver applies the degradation policy
    ///     and constructs <see cref="ResolvedRef" /> instances after the fact, so walkers
    ///     stay small and focused on the per-type field schema.
    /// </summary>
    IEnumerable<RawReference> Walk(object model);
}

/// <summary>
///     One outgoing FormID reference as seen by an <see cref="IRecordReferenceWalker" />.
///     The walker reports what the record says; the resolver decides what to do about it.
/// </summary>
public sealed record RawReference
{
    public required string FieldPath { get; init; }
    public required uint? FormId { get; init; }

    /// <summary>
    ///     Optional containment signature for downgrade purposes (e.g. <c>"PLDT"</c>). When
    ///     non-null, a dangling reference in this field triggers a container reshape rather
    ///     than a subrecord drop. Walkers carry the signal up so the resolver can apply
    ///     <see cref="ResolvedRefAction.DowngradeContainer" /> per <see cref="DegradationPolicy" />.
    /// </summary>
    public string? ContainerSignature { get; init; }
}

/// <summary>Aggregate result of one record's reference walk — exposed for testing.</summary>
public sealed record ReferenceWalkResult
{
    public required ImmutableArray<RawReference> References { get; init; }
}
