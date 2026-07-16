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

    /// <summary>Post-tree frond parameters (token 13000 section → <c>CFrondEngine::Parse</c>). Null when
    /// the <c>.spt</c> omits the section — the <c>CFrondEngine</c> ctor default is DISABLED, so branch
    /// levels are only frond-gated when a section explicitly enables fronds.</summary>
    public SptFrond? Frond { get; init; }
}

/// <summary>
///     Frond parameters from the post-tree 13000 section (<c>CFrondEngine::Parse</c> — 360 0x82978CA8 ==
///     Oblivion FUN_0079f1e0; both binaries agree). When <see cref="Enabled" />, every branch at
///     <c>level &gt;= <see cref="Level" /></c> is frond-gated in <c>CIdvBranch::Compute</c>: the branch (and
///     its whole subtree) still GENERATES — identical RNG draws, and its placed leaves persist in the
///     global pools — but the object is destroyed instead of being linked into the parent's child vector
///     (360 CIdvBranch::Compute L2447-2462), so it lofts no bark tube and never enters
///     <c>BuildBranchLods</c>' volume ranking. Its rings feed CFrondEngine guides instead, and Bethesda's
///     <c>BSTreeModel::CreateGeometry</c> never consumes frond geometry — gated levels are invisible in-game.
/// </summary>
public sealed record SptFrond
{
    /// <summary>Token 13007 → <c>CFrondEngine+0x3C</c> (byte). Ctor default false.</summary>
    public bool Enabled { get; init; }

    /// <summary>Token 13002 → <c>CFrondEngine+0x38</c>: first frond-gated branch level. Ctor default 1.</summary>
    public int Level { get; init; } = 1;
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

    /// <summary>
    ///     Token 9011 → <c>CTreeEngine+0xc0</c>: number of leaf LOD lists built by
    ///     <c>CTreeEngine::BuildLeafLods</c>. Constructor default 1 (the unmerged LOD0 list only).
    /// </summary>
    public int NumLeafLods { get; init; } = 1;

    /// <summary>
    ///     Token 9010 → <c>CTreeEngine+0xe4</c>: per-level leaf-card size increase. The parser normalizes
    ///     authored zero to the SDK's 0.1 fallback, exactly as <c>ParseLodInfo</c> does.
    /// </summary>
    public float LeafLodSizeIncrease { get; init; } = 0.1f;

    /// <summary>Token 9013 → <c>+0xe8</c>: upper bound of the per-branch demotion draw in
    /// <c>BuildBranchLods</c> (<c>u = GetUniform(0, this)</c>; sort key zeroed when
    /// <c>(1−u)·v + u·max &lt; 0</c>). Ctor default 0 = no demotion (draws still happen, from a private
    /// <c>Reseed(-1)</c> RNG — never the tree stream).</summary>
    public float BranchDemotionRandomness { get; init; }

    /// <summary>Token 9014 → <c>+0xec</c>: guarantee fraction — branches with volume &gt;
    /// <c>max·(1−this)</c> skip the demotion draw and are front-inserted ahead of the sorted rest.
    /// Ctor default 0.05.</summary>
    public float BranchGuaranteeFraction { get; init; } = 0.05f;
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
    // Bark texture tilings + their "absolute" flags (SIdvBranchInfo ctor defaults 1.0 / 1.0 / true / false):
    // flag set → the tiling is consumed raw (repeats per revolution / per unit path); clear → the engine
    // scales it by radius·2π (U) or branch length/tree size (V) at CIdvBranch::Compute L2051-2062.
    public float Float6013 { get; init; } = 1f; // +0x14 bark U tiling
    public float Float6014 { get; init; } = 1f; // +0x18 bark V tiling
    public bool Bool6015 { get; init; } = true; // +0x1C absolute-U flag
    public bool Bool6016 { get; init; }         // +0x1D absolute-V flag
}

/// <summary>One leaf card (tokens 0x3EF..0x3F0 inside the 0x3F1 collection).</summary>
public sealed record SptLeaf
{
    public byte Type { get; init; }          // 4000
    // 4001 = the per-leaf SIZE BASE vector (MakeLeaf adds the ±4002 jitter to it and CLeafGeometry
    // scales the card extents by the result). Every shipped .spt authors (1,1,1); default to that so
    // synthetic models without the token keep unit-scaled cards.
    public Vector3 Position { get; init; } = Vector3.One;
    public float Size { get; init; }         // 4002 (size jitter bound; also the lighting-normal lerp t)
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
    // +0x2c: blossom gate anchor mode (SIdvLeafInfo ctor default 1; 0 = gate on the leaf-bearing
    // BRANCH's spawn fraction, non-zero = gate on the bud's own percent).
    public uint UInt3001 { get; init; } = 1;
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
