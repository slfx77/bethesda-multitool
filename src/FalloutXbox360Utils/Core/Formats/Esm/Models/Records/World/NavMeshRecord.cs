namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

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

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}
