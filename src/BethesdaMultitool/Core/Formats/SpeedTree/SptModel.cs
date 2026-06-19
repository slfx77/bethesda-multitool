using System.Numerics;

namespace BethesdaMultitool.Core.Formats.SpeedTree;

/// <summary>
///     Parsed contents of a SpeedTree <c>.spt</c> ("__IdvSpt_02_") tree definition. Holds the
///     procedural parameters, branch splines, and explicit leaf cards — NOT final geometry. Branch
///     tubes / fronds / leaf cards are generated from this model (see the SpeedTree geometry builder).
/// </summary>
public sealed record SptModel
{
    public required SptGeneralParams General { get; init; }

    public IReadOnlyList<SptBranch> Branches { get; init; } = [];

    public IReadOnlyList<SptLeaf> Leaves { get; init; } = [];

    public IReadOnlyList<SptLeafTextureCoords> LeafTextureCoords { get; init; } = [];

    /// <summary>Leaf-table float (token 3000, struct offset +0x2C) — typically a global leaf size.</summary>
    public float LeafSize { get; init; }

    public SptLeafTable? LeafTable { get; init; }

    public SptWind? Wind { get; init; }
}

/// <summary>Section 1002 (general) parameters.</summary>
public sealed record SptGeneralParams
{
    /// <summary>Bark texture path (token 2000) — a dev-machine absolute <c>.tga</c> path in shipped files.</summary>
    public string? BarkTexturePath { get; init; }

    public float Float2001 { get; init; } // +0x40
    public byte Byte2002 { get; init; }
    public float Float2003 { get; init; } // +0x44
    public uint Token2005 { get; init; }  // LOD/config
    public float Float2006 { get; init; } // +0x4C
    public float Float2007 { get; init; } // +0x50
}

/// <summary>
///     One branch record (tokens 0x3F8..0x3F9). Carries nine BezierSplines plus scalar parameters.
///     Spline slots 0..7 = tokens 6000..6007; slot 8 = token 6017.
/// </summary>
public sealed record SptBranch
{
    /// <summary>Nine spline slots; any may be null if the stored string was not a valid BezierSpline.</summary>
    public IReadOnlyList<SptBezierSpline?> Splines { get; init; } = new SptBezierSpline?[9];

    public uint UInt6008 { get; init; }
    public uint UInt6009 { get; init; }
    public float Float6010 { get; init; }
    public float Float6011 { get; init; }
    public float Float6012 { get; init; }
    public float Float6013 { get; init; }
    public float Float6014 { get; init; }
    public bool Bool6015 { get; init; }
    public bool Bool6016 { get; init; }
}

/// <summary>One leaf card (tokens 0x3EF..0x3F0 inside the 0x3F1 collection).</summary>
public sealed record SptLeaf
{
    public byte Type { get; init; }          // 4000
    public Vector3 Position { get; init; }   // 4001
    public float Size { get; init; }         // 4002
    public string? Material { get; init; }   // 4003 (dev-machine .tga path in shipped files)
    public Vector3 Corner0 { get; init; }    // 4004
    public Vector3 Corner1 { get; init; }    // 4005
    public Vector3 Corner2 { get; init; }    // 4006
    public float Float4007 { get; init; }    // 4007
}

/// <summary>
///     One 8-float leaf UV block from token 10000/10002. The runtime passes this exact order to
///     <c>CLeafGeometry::SetTextureCoords</c>: four consecutive (u,v) pairs for the leaf card vertices.
/// </summary>
public readonly record struct SptLeafTextureCoords(Vector2 Corner0, Vector2 Corner1, Vector2 Corner2, Vector2 Corner3)
{
    public static SptLeafTextureCoords FullAtlas { get; } =
        new(new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f), new Vector2(0f, 0f));

    public Vector2 this[int index] => index switch
    {
        0 => Corner0,
        1 => Corner1,
        2 => Corner2,
        3 => Corner3,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };
}

/// <summary>General leaf-table parameters (tokens 3001..3010). Captured for completeness.</summary>
public sealed record SptLeafTable
{
    public uint UInt3001 { get; init; }  // +0x34
    public float Float3002 { get; init; } // +0x30
    public byte Byte3003 { get; init; }
    public float Float3004 { get; init; }
    public float Float3005 { get; init; }
    public byte Byte3006 { get; init; }
    public float Float3007 { get; init; } // SIdvLeafInfo+0x20
    public uint UInt3008 { get; init; }   // +0x0C
    public byte Byte3009 { get; init; }   // +0
    public float Float3010 { get; init; } // +4
}

/// <summary>Section 1011 (wind) parameters.</summary>
public sealed record SptWind
{
    public Vector3 Vec5000 { get; init; }
    public Vector3 Vec5001 { get; init; }
    public Vector3 Vec5002 { get; init; }
    public Vector3 Vec5003 { get; init; }
    public Vector3 Vec5004 { get; init; }
    public float Float5005 { get; init; }
    public byte Byte5006 { get; init; }
}
