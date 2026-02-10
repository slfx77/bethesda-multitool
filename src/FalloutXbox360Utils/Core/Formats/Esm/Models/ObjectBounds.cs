namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Object bounds from the OBND subrecord (12 bytes).
///     Defines the 3D bounding box for a placeable object.
/// </summary>
public record ObjectBounds
{
    /// <summary>Minimum X bound.</summary>
    public short X1 { get; init; }

    /// <summary>Minimum Y bound.</summary>
    public short Y1 { get; init; }

    /// <summary>Minimum Z bound.</summary>
    public short Z1 { get; init; }

    /// <summary>Maximum X bound.</summary>
    public short X2 { get; init; }

    /// <summary>Maximum Y bound.</summary>
    public short Y2 { get; init; }

    /// <summary>Maximum Z bound.</summary>
    public short Z2 { get; init; }

    public override string ToString() => $"({X1}, {Y1}, {Z1}) to ({X2}, {Y2}, {Z2})";
}
