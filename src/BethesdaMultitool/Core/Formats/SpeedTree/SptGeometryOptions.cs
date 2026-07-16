using System.Globalization;
using BethesdaMultitool.Core;

namespace BethesdaMultitool.Core.Formats.SpeedTree;

/// <summary>
///     Parameters for <see cref="SptGeometryBuilder" />, which reimplements the SpeedTree RT SDK's
///     <c>CIdvBranch::Compute</c> loft (recovered from the Xbox MemDebug decompile — see the
///     <c>speedtree-compute-algorithm</c> memory). Branch shape, lengths, radii, angles, child/leaf
///     counts, ring counts, the per-ring gravity bend, and leaf card size are all DATA-DRIVEN from the
///     <c>.spt</c> splines/scalars and the decompiled formulas — there are deliberately NO geometry
///     tuning knobs here. What remains is only what the SDK math genuinely leaves to the host: the
///     recursion cap, the leaf atlas override (TREE.ICON), and how a static (non-camera) mesh stands in
///     for the engine's per-frame leaf billboards. The loft output is already world scale — the engine
///     renders it 1:1 with no OBND/billboard rescale (verified against 15 runtime mesh dumps).
/// </summary>
public sealed record SptGeometryOptions
{
    /// <summary>Recursion-depth cap (the <c>.spt</c> typically has ≤4 branch-record templates anyway).</summary>
    public int MaxLevels { get; init; } = 8;

    /// <summary>
    ///     Fallback master scale (SpeedTree "tree size", pre-×10 units) used only when the <c>.spt</c>
    ///     General section carries no Float2006 size — it then IS the tree's world size the way Float2006
    ///     would be.
    /// </summary>
    public float TrunkHeight { get; init; } = 100f;

    /// <summary>
    ///     IGNORED — kept only so in-flight callers still compile. The engine renders the loft at its
    ///     natural world scale with NO rescale to TREE OBND/BNAM (15 runtime SptMeshDumper oracles;
    ///     the old rescale oversized shrubs up to 2.7×). Delete once
    ///     <c>ReferenceMeshDecoder12</c> (shared-dirty with the NIF-animation session) drops its assignment.
    /// </summary>
    public float? TargetHeight { get; init; }

    /// <summary>Final-height tuning multiplier (env <c>FALLOUT_VIEWER_SPT_HEIGHT_SCALE</c>) — a host-side
    /// nudge for the viewer, not a shape parameter.</summary>
    public float HeightScale { get; init; } = 1.0f;

    /// <summary>
    ///     Compatibility-only legacy option. Child spawn counts are binary-derived and are no longer capped
    ///     here; mesh-buffer capacity remains the only vertex-budget guard.
    /// </summary>
    public int MaxChildrenPerBranch { get; init; } = 64;

    /// <summary>Emit each leaf as a crossed pair of perpendicular cards (volume from any angle) when not
    /// camera-facing.</summary>
    public bool CrossedLeafCards { get; init; } = true;

    /// <summary>
    ///     Emit leaves as GPU billboard cards: one quad per leaf with the card CENTER in the tangent slot
    ///     and the signed 2D corner offset in the bitangent slot, so the D3D12 leaf-billboard vertex
    ///     shader re-faces each card to the camera per frame (the efficient equivalent of SpeedTree's
    ///     CPU-side per-frame leaf billboard). Set by the live viewer; the still/GLB paths leave it false
    ///     and use <see cref="LeafFaceDirection" /> / crossed cards.
    /// </summary>
    public bool LeafBillboard { get; init; }

    /// <summary>
    ///     When set, every leaf card is oriented to FACE this world direction (a single quad whose normal
    ///     points along it) instead of the bud-oriented crossed pair — i.e. a static stand-in for the
    ///     engine's per-card camera-facing leaf billboards. The render harness passes the camera direction
    ///     so a still shows the billboarded look; the live viewer needs a per-card leaf-billboard shader to
    ///     reproduce it at every angle (per-submesh <c>IsBillboard</c> would rotate the whole leaf cloud).
    /// </summary>
    public System.Numerics.Vector3? LeafFaceDirection { get; init; }

    /// <summary>Alpha-test threshold (0-255) for leaf cards.</summary>
    public byte LeafAlphaThreshold { get; init; } = 84;

    /// <summary>
    ///     Game-relative leaf-atlas path that OVERRIDES the <c>.spt</c>'s dev-era leaf material for every
    ///     leaf card. The engine sources the leaf texture from the <c>TREE</c> record's <c>ICON</c> field
    ///     (e.g. WhiteOak's ICON = <c>WhiteOakLeaves01.dds</c> → <c>textures\trees\leaves\whiteoakleaves01.dds</c>),
    ///     NOT the `.spt`'s baked path (`treewoakleaves01b`, a dev leftover that never shipped) — confirmed
    ///     by dumping the TREE record + decompiling the engine. Null → fall back to the `.spt` material.
    /// </summary>
    public string? LeafTextureOverride { get; init; }

    /// <summary>
    ///     Leaf canopy-depth dimming scalar OVERRIDING the <c>.spt</c>'s token 3010 — the engine sources it
    ///     from the <c>TREE</c> record's CNAM (LeafDimmingValue) via
    ///     <c>TESObjectTREE::GetLeafDimming → CSpeedTreeRT::SetLeafDimmingScalar</c>. 0 = no dimming,
    ///     1 = interior leaves darken fully with canopy depth. Null → the .spt token (default 1.0).
    /// </summary>
    public float? LeafDimming { get; init; }

    /// <summary>
    ///     Branch/bark dimming scalar from the <c>TREE</c> record's CNAM (BranchDimmingValue) via
    ///     <c>CSpeedTreeRT::SetBranchDimmingScalar</c>. The <c>.spt</c> has NO token for it
    ///     (<c>SIdvLeafInfo::Parse</c> never writes +8), so null → 0 (neutral bark).
    /// </summary>
    public float? BranchDimming { get; init; }

    /// <summary>TREE CNAM Rock Speed, multiplied into the shared rock timer per tree type.</summary>
    public float RockSpeed { get; init; } = 1f;

    /// <summary>TREE CNAM Rustle Speed, multiplied into the shared rustle timer per tree type.</summary>
    public float RustleSpeed { get; init; } = 1f;

    /// <summary>
    ///     Temporary opt-in architectural path that emits the authored branch/leaf LOD sequence plus a
    ///     far billboard. False preserves the established single-LOD geometry byte-for-byte.
    /// </summary>
    public bool RuntimeLod { get; init; }

    /// <summary>
    ///     Game-relative far-billboard atlas. The live decoder supplies the conventional
    ///     <c>textures\trees\billboards\{model-stem}.dds</c> path; explicit callers may override it.
    /// </summary>
    public string? BillboardTexturePath { get; init; }

    public static SptGeometryOptions Default { get; } = new();

    /// <summary>
    ///     Build options from the defaults, applying the <c>FALLOUT_VIEWER_SPT_HEIGHT*</c> env-var
    ///     overrides. Only a host-side height nudge is exposed — branch/leaf geometry AND world scale are
    ///     fully derived from the <c>.spt</c> data and the decompiled formulas, so there is nothing else
    ///     to tune.
    /// </summary>
    public static SptGeometryOptions FromEnvironment()
    {
        return Default with
        {
            TrunkHeight = ReadFloat(EnvironmentVariables.Viewer.SpeedTreeHeight, Default.TrunkHeight, 1f, 100000f),
            HeightScale = ReadFloat(EnvironmentVariables.Viewer.SpeedTreeHeightScale, Default.HeightScale, 0.05f, 20f),
            RuntimeLod = SpeedTreeRuntimeLod.Enabled,
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
