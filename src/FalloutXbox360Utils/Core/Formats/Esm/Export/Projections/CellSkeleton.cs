namespace FalloutXbox360Utils.Core.Formats.Esm.Export.Projections;

/// <summary>
///     Minimal cell projection retained per source so cross-source builders
///     (<see cref="VirtualCellCanonicalizer" />, the placement index builders, key/container
///     locked-door indexes) can run after the heavy <c>CellRecord</c> is released. Drops
///     the heavyweight <c>Heightmap</c>, <c>RuntimeTerrainMesh</c>, <c>LandVisualData</c>,
///     <c>LightingData</c>, and per-cell flag arrays that the cross-dump comparison pipeline
///     never reads.
/// </summary>
internal sealed record CellSkeleton
{
    public required uint FormId { get; init; }
    public string? EditorId { get; init; }
    public string? FullName { get; init; }
    public uint? WorldspaceFormId { get; init; }
    public int? GridX { get; init; }
    public int? GridY { get; init; }

    /// <summary>Cell flags byte mirrored from <c>CellRecord.Flags</c> (bit 0 = interior, bit 1 = has-water).</summary>
    public byte Flags { get; init; }

    public bool IsInterior => (Flags & 0x01) != 0;
    public bool HasWater => (Flags & 0x02) != 0;

    public bool IsVirtual { get; init; }
    public bool IsPersistentCell { get; init; }
    public bool IsUnresolvedBucket { get; init; }
    public bool HasPersistentObjects { get; init; }

    /// <summary>Lightweight placement projections; the originating <c>CellRecord.PlacedObjects</c> can be dropped once these exist.</summary>
    public IReadOnlyList<PlacedObjectSkeleton> PlacedObjects { get; init; } = [];
}
