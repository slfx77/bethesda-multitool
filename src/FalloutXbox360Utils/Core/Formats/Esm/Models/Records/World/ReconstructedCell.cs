namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Fully reconstructed Cell with placed objects.
///     Aggregates data from CELL main record header and associated REFR/ACHR/ACRE records.
/// </summary>
public record ReconstructedCell
{
    /// <summary>FormID of the CELL record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name.</summary>
    public string? FullName { get; init; }

    /// <summary>Cell X coordinate in the grid (from XCLC, null for interior cells).</summary>
    public int? GridX { get; init; }

    /// <summary>Cell Y coordinate in the grid (from XCLC, null for interior cells).</summary>
    public int? GridY { get; init; }

    /// <summary>Parent worldspace FormID (null for interior cells).</summary>
    public uint? WorldspaceFormId { get; init; }

    /// <summary>Cell flags from DATA subrecord.</summary>
    public byte Flags { get; init; }

    /// <summary>Whether this is an interior cell.</summary>
    public bool IsInterior => (Flags & 0x01) != 0;

    /// <summary>Whether this cell has water.</summary>
    public bool HasWater => (Flags & 0x02) != 0;

    /// <summary>Placed objects in this cell (REFR, ACHR, ACRE records).</summary>
    public List<PlacedReference> PlacedObjects { get; init; } = [];

    /// <summary>Associated LAND record heightmap (if found).</summary>
    public LandHeightmap? Heightmap { get; init; }

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}
