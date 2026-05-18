namespace FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

/// <summary>
///     Navigation Mesh (NAVM) record.
///     Defines AI pathfinding mesh for a cell — vertices, triangles, and door portals.
/// </summary>
public record NavMeshRecord
{
    /// <summary>FormID of the navigation mesh record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Parent cell FormID.</summary>
    public uint CellFormId { get; init; }

    /// <summary>Number of vertices in this navmesh.</summary>
    public uint VertexCount { get; init; }

    /// <summary>Number of triangles in this navmesh.</summary>
    public uint TriangleCount { get; init; }

    /// <summary>Number of door portal connections.</summary>
    public int DoorPortalCount { get; init; }

    /// <summary>
    ///     Subrecords captured verbatim during parsing (post-endian-conversion to PC LE).
    ///     Used by the cell pipeline to re-emit DMP-captured NAVMs for cells where master
    ///     has no NAVM (proto-only worldspaces, master-cell augmentation). Empty when
    ///     parsing failed or the record was a runtime-only stub.
    /// </summary>
    public List<NavMeshSubrecord> RawSubrecords { get; init; } = [];

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}

/// <summary>
///     One NAVM subrecord captured verbatim from parsing, in PC little-endian byte form.
///     The cell pipeline patches DATA (Cell field) and NVEX (Navmesh field) entries
///     before re-emission.
/// </summary>
public readonly record struct NavMeshSubrecord(string Signature, byte[] Bytes);
