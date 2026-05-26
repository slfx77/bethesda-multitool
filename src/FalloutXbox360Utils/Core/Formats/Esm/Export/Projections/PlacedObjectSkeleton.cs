using FalloutXbox360Utils.Core.Formats.Esm.Models.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export.Projections;

/// <summary>
///     Minimal placement projection used by the streaming cross-dump pipeline.
///     Captures the fields that <see cref="CrossDumpPlacementIndexBuilder" />,
///     <see cref="NpcPlacementInfo" />, <see cref="KeyLockedDoorInfo" />, and
///     <see cref="ContainerPlacementInfo" /> read so the heavier <see cref="PlacedReference" />
///     can be released once the projection exists.
/// </summary>
/// <remarks>
///     Held by reference inside <see cref="CellSkeleton.PlacedObjects" />. Phase 1 keeps the
///     full <see cref="PlacedReference" /> reachable so downstream Info types (which currently
///     hold a <see cref="PlacedReference" />) keep working unchanged; Phase 2 will swap the
///     Info types over to read from this skeleton and drop the field reference.
/// </remarks>
internal sealed record PlacedObjectSkeleton
{
    /// <summary>Mirror of <see cref="PlacedReference.FormId" />.</summary>
    public required uint FormId { get; init; }

    /// <summary>Mirror of <see cref="PlacedReference.BaseFormId" />.</summary>
    public required uint BaseFormId { get; init; }

    /// <summary>Mirror of <see cref="PlacedReference.RecordType" /> ("REFR", "ACHR", "ACRE").</summary>
    public required string RecordType { get; init; }

    /// <summary>Mirror of <see cref="PlacedReference.LockKeyFormId" /> — drives key→door reverse lookup.</summary>
    public uint? LockKeyFormId { get; init; }

    /// <summary>
    ///     Back-reference to the originating <see cref="PlacedReference" />. Phase 1 keeps it so existing
    ///     <see cref="NpcPlacementInfo.Ref" /> / <see cref="KeyLockedDoorInfo.Ref" /> / <see cref="ContainerPlacementInfo.Ref" />
    ///     keep their current shape. Phase 2 widens this skeleton with the remaining fields actually consumed
    ///     by downstream report builders and drops this reference.
    /// </summary>
    public required PlacedReference Ref { get; init; }

    public static PlacedObjectSkeleton From(PlacedReference reference)
    {
        return new PlacedObjectSkeleton
        {
            FormId = reference.FormId,
            BaseFormId = reference.BaseFormId,
            RecordType = reference.RecordType,
            LockKeyFormId = reference.LockKeyFormId,
            Ref = reference
        };
    }
}
