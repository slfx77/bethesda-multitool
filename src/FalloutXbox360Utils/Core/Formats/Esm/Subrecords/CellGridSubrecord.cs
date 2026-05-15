namespace FalloutXbox360Utils.Core.Formats.Esm.Subrecords;

/// <summary>
///     XCLC cell grid subrecord - contains cell X/Y coordinates.
///     Critical for positioning heightmaps in a worldspace.
/// </summary>
public record CellGridSubrecord
{
    /// <summary>Cell X coordinate in the grid.</summary>
    public int GridX { get; init; }

    /// <summary>Cell Y coordinate in the grid.</summary>
    public int GridY { get; init; }

    /// <summary>Land flags byte.</summary>
    public byte LandFlags { get; init; }

    /// <summary>Parent CELL FormID when this XCLC was read from a full CELL record.</summary>
    public uint? CellFormId { get; init; }

    /// <summary>Offset in the dump where XCLC was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether detected as big-endian.</summary>
    public bool IsBigEndian { get; init; }
}
