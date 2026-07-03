namespace BethesdaMultitool.Core.Formats.Esm.Models;

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

    /// <summary>
    ///     True when every bound is zero — an authored-but-empty OBND some records carry (e.g. editor
    ///     markers, sound emitters). Consumers should treat it like a MISSING OBND: a zero-extent box
    ///     culls the mesh before it can decode and gives ray picking a zero-size target.
    /// </summary>
    public bool IsDegenerate => X1 == 0 && Y1 == 0 && Z1 == 0 && X2 == 0 && Y2 == 0 && Z2 == 0;

    public override string ToString()
    {
        return $"({X1}, {Y1}, {Z1}) to ({X2}, {Y2}, {Z2})";
    }
}
