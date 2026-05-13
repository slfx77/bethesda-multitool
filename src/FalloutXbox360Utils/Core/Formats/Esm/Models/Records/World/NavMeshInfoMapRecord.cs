namespace FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

/// <summary>
///     NavMesh Info Map (NAVI) record. Single per-ESM record holding the
///     cross-cell pathfinding metadata. PDB struct: NavMeshInfoMap (80 bytes,
///     FormType 0x38).
/// </summary>
public record NavMeshInfoMapRecord
{
    public uint FormId { get; init; }
    public string? EditorId { get; init; }

    /// <summary>bUpdateAll flag at +40.</summary>
    public bool UpdateAll { get; init; }

    /// <summary>bInit flag at +76 (runtime-mutated).</summary>
    public bool Initialized { get; init; }

    public long Offset { get; init; }
    public bool IsBigEndian { get; init; }
}
