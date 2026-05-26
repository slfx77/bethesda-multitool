namespace FalloutXbox360Utils.Core.Formats.Esm.Export.Projections;

/// <summary>
///     Minimal script projection consumed by <c>BuildNpcScriptReferenceIndex</c>. Holds
///     just the identity + referenced-object FormIDs the index walks.
/// </summary>
internal sealed record ScriptSkeleton
{
    public required uint FormId { get; init; }
    public string? EditorId { get; init; }

    /// <summary>Mirror of <c>ScriptRecord.ScriptType</c> — the computed type label (non-null on the source).</summary>
    public required string ScriptType { get; init; }
    public uint? OwnerQuestFormId { get; init; }

    /// <summary>Distinct list of FormIDs referenced by this script (already deduped at projection time).</summary>
    public IReadOnlyList<uint> ReferencedObjects { get; init; } = [];
}
