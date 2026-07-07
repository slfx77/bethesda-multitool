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

    /// <summary>Token 3000, <c>SIdvLeafInfo+0x24</c>: blossom branch-param threshold (default 0.75).</summary>
    public float LeafSize { get; init; }

    public SptLeafTable? LeafTable { get; init; }

    public SptWind? Wind { get; init; }

    /// <summary>Post-tree branch-LOD parameters (token 9001 section). Null when the <c>.spt</c> omits the
    /// section, in which case the engine's constructor defaults apply (no LOD0 decimation).</summary>
    public SptLodInfo? Lod { get; init; }
}

/// <summary>
///     Branch-LOD parameters recovered from the post-tree LOD section (<c>CTreeEngine::ParseLodInfo</c>,
///     360 MemDebug 0x8298A5B0). The engine NEVER renders the raw <c>CIdvBranch::Compute</c> skeleton — it
///     decimates it in <c>CTreeEngine::BuildBranchLods</c>: each branch gets a "volume" weight
///     (<c>ComputeVolume</c> = Σ segLen·(rᵢ+rᵢ₊₁)), and LOD level <c>d</c> keeps the heaviest branches until
///     their cumulative weight reaches <c>fraction·total</c>, where LOD0's fraction is <see cref="BranchNearFraction" />
///     (lerping toward <see cref="BranchFarFraction" /> at the last level). Defaults are the
///     <c>CTreeEngine</c> ctor values (near 1.0 = keep all, so a missing section means no decimation).
/// </summary>
public sealed record SptLodInfo
{
    /// <summary>Token 9007 → <c>CTreeEngine+0x70</c>. Ctor default 6. When &lt; 2, LOD0 keeps everything.</summary>
    public int NumBranchLods { get; init; } = 6;

    /// <summary>Token 9012 → <c>+0xe0</c>: LOD0 (near) keep fraction of total branch volume. Ctor default 1.0.</summary>
    public float BranchNearFraction { get; init; } = 1f;

    /// <summary>Token 9008 → <c>+0xdc</c>: far-LOD keep fraction. Ctor default 0.5.</summary>
    public float BranchFarFraction { get; init; } = 0.5f;
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

    // Scalar tokens land at SIdvBranchInfo+0x00..+0x1D in Parse order (decompiled 360
    // SIdvBranchInfo::Parse; the +0x20/+0x24 scalars — including CIdvBranch::Compute's
    // ring-spacing pow() exponent — have NO tokens and keep their ctor defaults).
    public uint UInt6008 { get; init; }   // +0x00 verts per ring − 1
    public uint UInt6009 { get; init; }   // +0x04 ring count − 1
    public float Float6010 { get; init; } // +0x08 child/leaf spawn range start
    public float Float6011 { get; init; } // +0x0C child/leaf spawn range end
    public float Float6012 { get; init; } // +0x10 child frequency scale
    public float Float6013 { get; init; } // +0x14 bark texture U tiling (wraps around the ring)
    public float Float6014 { get; init; } // +0x18 bark texture V tiling (repeats along the branch)
    public bool Bool6015 { get; init; }   // +0x1C absolute-U-tiling flag
    public bool Bool6016 { get; init; }   // +0x1D absolute-V-tiling flag
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

/// <summary>General leaf-table parameters (tokens 3001..3010), mapped to <c>SIdvLeafInfo</c>.</summary>
public sealed record SptLeafTable
{
    public uint UInt3001 { get; init; }   // +0x2c: blossom branch-depth/ancestor mode
    public float Float3002 { get; init; } // +0x28: blossom probability
    public byte Byte3003 { get; init; }
    public float Float3004 { get; init; }
    public float Float3005 { get; init; }
    public byte Byte3006 { get; init; }
    public float Float3007 { get; init; } // +0x20: RoomForLeaf spacing factor
    public uint UInt3008 { get; init; }   // +0x0c: RoomForLeaf placement mode
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
