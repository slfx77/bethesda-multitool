using System.Globalization;
using FalloutXbox360Utils.Core;

namespace FalloutXbox360Utils.Core.Formats.SpeedTree;

/// <summary>
///     Tunable parameters for <see cref="SptGeometryBuilder" />, which reimplements the SpeedTree RT
///     SDK's <c>CIdvBranch::Compute</c> loft (recovered from the Xbox MemDebug decompile — see the
///     <c>speedtree-compute-algorithm</c> memory). The branch shape, lengths, radii, angles, child
///     counts and ring counts are DATA-DRIVEN from the <c>.spt</c> splines/scalars; these knobs only
///     cover the bits the decompile left as inference (curl strength, child density, leaf sizing/spacing,
///     bud angle) plus final rescale to the TREE OBND height. Tune against the in-game look via the
///     <c>EsmAnalyzer spt render</c> harness.
/// </summary>
public sealed record SptGeometryOptions
{
    /// <summary>Recursion-depth cap (the <c>.spt</c> typically has ≤4 branch-record templates anyway).</summary>
    public int MaxLevels { get; init; } = 8;

    /// <summary>
    ///     Fallback master scale (SpeedTree "tree size") used only when the <c>.spt</c> General section
    ///     carries no Float2006 size. The SDK works in arbitrary internal units; the tree is rescaled to
    ///     <see cref="TargetHeight" /> afterwards, so this mainly sets internal proportions.
    /// </summary>
    public float TrunkHeight { get; init; } = 100f;

    /// <summary>Authoritative final tree height (model units) from the TREE OBND Z-extent. Null → keep
    /// the generated height.</summary>
    public float? TargetHeight { get; init; }

    /// <summary>Final-height tuning multiplier (env <c>FALLOUT_VIEWER_SPT_HEIGHT_SCALE</c>).</summary>
    public float HeightScale { get; init; } = 1.0f;

    /// <summary>A child branch's length is clamped to this fraction of its parent's length.</summary>
    public float ChildLengthClamp { get; init; } = 0.85f;

    /// <summary>Small random lean applied to the trunk (degrees) for natural variation.</summary>
    public float TrunkLeanDeg { get; init; } = 4f;

    /// <summary>Scales the per-ring curvature bend (radians) driven by spline slot 1.</summary>
    public float CurlStrengthRad { get; init; } = 0.13f;

    /// <summary>Scales the per-ring angular noise (spline slot 0, in degrees).</summary>
    public float NoiseScale { get; init; } = 0.4f;

    /// <summary>Per-segment downward (−Z) pull that arches branches into a mound (SDK declination-weight
    /// gravity). 0 = straight branches; higher = lower, wider, droopier crown.</summary>
    public float GravityStrength { get; init; } = 1.2f;

    /// <summary>Minimum ring radius as a fraction of the branch's base radius (keeps tips from collapsing).</summary>
    public float MinRingRadiusFraction { get; init; } = 0.06f;

    /// <summary>Child-branch count = round(Float6012 · this · <see cref="ChildDensity" />).</summary>
    public float ChildFreqScale { get; init; } = 0.015f;

    /// <summary>User multiplier on the child-branch count.</summary>
    public float ChildDensity { get; init; } = 1.0f;

    /// <summary>Minimum children a non-terminal branch spawns (guarantees the tree reaches the leaf level).</summary>
    public int MinChildrenPerBranch { get; init; } = 2;

    /// <summary>Hard cap on children spawned per branch.</summary>
    public int MaxChildrenPerBranch { get; init; } = 10;

    /// <summary>Leaf cards emitted per ring on terminal (leaf-bearing) branches.</summary>
    public int LeavesPerRing { get; init; } = 4;

    /// <summary>Leaves only start past this fraction along a terminal branch (foliage toward the tips).</summary>
    public float LeafStartFraction { get; init; }

    /// <summary>Leaf card half-size as a fraction of the tree's master scale (treeSize); per-template c2
    /// aspect kept. Master-scale-relative so short shrubs get big foliage and tall pines get small clusters.</summary>
    public float LeafSizeFraction { get; init; } = 0.05f;

    /// <summary>Extra leaf-size multiplier (env <c>FALLOUT_VIEWER_SPT_LEAF_SCALE</c>).</summary>
    public float LeafSizeScale { get; init; } = 1.0f;

    /// <summary>Bud declination off the branch direction (degrees) before a leaf card is placed.</summary>
    public float BudAngleDeg { get; init; } = 35f;

    /// <summary>How far the bud steps out from the branch, in leaf-half-size units.</summary>
    public float BudReach { get; init; } = 0.5f;

    /// <summary>Emit each leaf as a crossed pair of perpendicular cards (volume from any angle).</summary>
    public bool CrossedLeafCards { get; init; } = true;

    /// <summary>
    ///     When set, every leaf card is oriented to FACE this world direction (a single quad whose normal
    ///     points along it) instead of the bud-oriented crossed pair — i.e. a static stand-in for the
    ///     engine's per-card camera-facing leaf billboards. The render harness passes the camera direction
    ///     so a still shows the billboarded look; the live viewer needs a per-card leaf-billboard shader to
    ///     reproduce it at every angle (per-submesh <c>IsBillboard</c> would rotate the whole leaf cloud).
    /// </summary>
    public System.Numerics.Vector3? LeafFaceDirection { get; init; }

    /// <summary>Alpha-test threshold (0-255) for leaf cards.</summary>
    public byte LeafAlphaThreshold { get; init; } = 96;

    public static SptGeometryOptions Default { get; } = new();

    /// <summary>
    ///     Build options from the defaults, applying any <c>FALLOUT_VIEWER_SPT_*</c> env-var overrides so
    ///     tree height / leaf size / leaf density / child density / curl can be tuned live without a
    ///     rebuild (<c>.spt</c> geometry is intentionally not disk-cached).
    /// </summary>
    public static SptGeometryOptions FromEnvironment()
    {
        return Default with
        {
            TrunkHeight = ReadFloat(EnvironmentVariables.Viewer.SpeedTreeHeight, Default.TrunkHeight, 1f, 100000f),
            HeightScale = ReadFloat(EnvironmentVariables.Viewer.SpeedTreeHeightScale, Default.HeightScale, 0.05f, 20f),
            LeafSizeScale = ReadFloat(EnvironmentVariables.Viewer.SpeedTreeLeafScale, Default.LeafSizeScale, 0.05f, 20f),
            LeavesPerRing =
                (int)ReadFloat(EnvironmentVariables.Viewer.SpeedTreeLeafCount, Default.LeavesPerRing, 0f, 32f),
            ChildDensity = ReadFloat(EnvironmentVariables.Viewer.SpeedTreeChildDensity, Default.ChildDensity, 0f, 10f),
            CurlStrengthRad =
                ReadFloat(EnvironmentVariables.Viewer.SpeedTreeCurl, Default.CurlStrengthRad, 0f, 2f),
            GravityStrength =
                ReadFloat(EnvironmentVariables.Viewer.SpeedTreeGravity, Default.GravityStrength, 0f, 5f),
        };
    }

    private static float ReadFloat(string name, float fallback, float min, float max)
    {
        var raw = EnvironmentVariables.Get(name);
        if (!string.IsNullOrWhiteSpace(raw) &&
            float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return Math.Clamp(value, min, max);
        }

        return fallback;
    }
}
