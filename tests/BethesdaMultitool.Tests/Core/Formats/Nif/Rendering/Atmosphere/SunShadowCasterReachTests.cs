using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Atmosphere;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Atmosphere;

/// <summary>
///     Locks the CASTER side of the sun-shadow cascade contract.
///     <para>
///         The defect these pin (user 2026-08-11, "shadows close to the camera disappear", and the
///         paired yaw report) was an asymmetry nobody had written down. The pixel shader
///         (<c>shadow_sampling.hlsli</c> <c>ShadowFactor</c>) selects the smallest cascade whose
///         footprint contains the RECEIVING PIXEL and returns immediately — there is no fallback to a
///         coarser cascade when the finer one holds no occluder. Meanwhile the CPU decided which
///         cascade a CASTER was drawn into by asking whether the caster's own sphere sat INSIDE the
///         cascade box, and the box was symmetric at +/-1.25*radius. Light only travels one way, so a
///         caster is always UP-SUN of the surface it darkens: cascade 0's box reached just 2560 units
///         up-sun, and every caster past that was both withheld from the submission list and clipped
///         by the projection, while the pixels its shadow should have darkened still selected
///         cascade 0 and found an empty map. Because the anchor is texel-snapped, a metre of camera
///         movement flipped casters across that edge and the shadow blinked.
///     </para>
///     <para>
///         <see cref="AcceptedCaster_AlwaysRasterizesIntoTheSameCascade" /> is the one that matters:
///         it is the cross-check between the two halves, which is exactly what was missing.
///     </para>
/// </summary>
public sealed class SunShadowCasterReachTests
{
    // Cascade 0 of the shipped ladder {2048, 8192, 32768, 131072}.
    private const float Cascade0Radius = 2048f;
    private const int Resolution = 2048;

    // Hour 8.8 at WastelandNV is a moderate morning sun; the reported repro poses are at that hour.
    private static readonly Vector3 MorningSun = Vector3.Normalize(new Vector3(0.62f, 0.34f, 0.71f));

    private static float ClipDepth(Matrix4x4 viewProj, Vector3 worldPos)
    {
        return Vector4.Transform(new Vector4(worldPos, 1f), viewProj).Z;
    }

    /// <summary>
    ///     A depth the rasterizer keeps: outside (0,1) the primitive is clipped away, and
    ///     TryCascadeShadow rejects the same range on the receiving side.
    /// </summary>
    private static bool WithinDepthRange(float clipZ)
    {
        return clipZ > 0f && clipZ < 1f;
    }

    [Fact]
    public void CasterReach_ExceedsTheBoxDepth_WhenTheSceneIsTallerThanTheCascade()
    {
        // A WastelandNV-scale vertical span. The old symmetric box gave cascade 0 only
        // 1.25 * 2048 = 2560 units up-sun, which a 9,000-unit-tall scene overruns immediately.
        var reach = SunShadowMath.CascadeCasterReach(MorningSun, Cascade0Radius, 9000f);

        Assert.True(reach > Cascade0Radius * SunShadowMath.CascadeDepthExtentFactor,
            $"a scene taller than the cascade must widen the up-sun reach; got {reach}");
    }

    [Fact]
    public void CasterReach_NeverShrinksBelowTheBoxDepth()
    {
        // Unknown extent (0) and a flat scene must both collapse to the box's own depth, so the
        // default path stays exactly what it was.
        Assert.Equal(
            Cascade0Radius * SunShadowMath.CascadeDepthExtentFactor,
            SunShadowMath.CascadeCasterReach(MorningSun, Cascade0Radius, 0f),
            3);
    }

    [Fact]
    public void CasterReach_DegeneratesToTheSceneHeightAtZenith_AndTheLateralLimitAtTheHorizon()
    {
        // Zenith: the up-sun axis IS world up, so the reach is the scene's height.
        var zenith = SunShadowMath.CascadeCasterReach(
            new Vector3(0f, 0f, 1f), Cascade0Radius, 9000f);
        Assert.Equal(9000f, zenith, 1);

        // Horizon: height contributes nothing along the light axis; the bound is the lateral limit,
        // which is the farthest a caster can be and still land its shadow in the footprint.
        var horizon = SunShadowMath.CascadeCasterReach(
            new Vector3(1f, 0f, 0f), Cascade0Radius, 9000f);
        Assert.Equal(Cascade0Radius * SunShadowMath.CascadeLateralReachFactor, horizon, 1);
    }

    [Fact]
    public void CasterReach_IsCeilinged_SoAPollutedSceneZSpanCannotWreckDepthPrecision()
    {
        // MEASURED, not hypothetical: at WastelandNV the rendered Z span came back ~173,000 (union of
        // decoded MESH bounds over every cull survivor in a 65k-unit cylinder), which without a
        // ceiling gave cascade 0 a ~104,000-unit depth range — every real caster squashed into the
        // bottom 5% of a reversed-Z float buffer, where precision is worst, plus a long tail of
        // casters that can never reach the near field.
        var polluted = SunShadowMath.CascadeCasterReach(MorningSun, Cascade0Radius, 173_000f);

        Assert.Equal(Cascade0Radius * SunShadowMath.CascadeCasterReachRadiiCeiling, polluted, 1);
        // Still far wider than the box that caused the defect — the ceiling must not undo the fix.
        Assert.True(polluted > Cascade0Radius * SunShadowMath.CascadeDepthExtentFactor * 5f,
            $"the ceiling must stay well clear of the ±1.25-radius box; got {polluted}");
    }

    [Fact]
    public void BuildLightFrustum_WithoutACasterReach_IsBitIdenticalToTheSymmetricBox()
    {
        // The default argument must not perturb any existing caller (terrain, tests, export paths).
        var center = new Vector3(91009.9f, 21443f, 7608.6f);
        var a = SunShadowMath.BuildLightFrustum(
            MorningSun, center, Vector3.Zero, Cascade0Radius, Resolution);
        var b = SunShadowMath.BuildLightFrustum(
            MorningSun, center, Vector3.Zero, Cascade0Radius, Resolution, 0f);

        Assert.Equal(a.ViewProj, b.ViewProj);
        Assert.Equal(a.TexelWorldSize, b.TexelWorldSize);
        Assert.Equal(a.NormalizedDepthBias, b.NormalizedDepthBias);
    }

    [Fact]
    public void SymmetricBox_ClipsAnUpSunCaster_ThatTheExtendedBoxKeeps()
    {
        // The failure, reproduced as arithmetic: a caster 4,000 units up-sun of the anchor — a
        // ridge or a tall building above a camera looking down at terrain — whose shadow lands
        // inside cascade 0's footprint.
        var anchor = new Vector3(91009.9f, 21443f, 7608.6f);
        var caster = anchor + MorningSun * 4000f;

        var symmetric = SunShadowMath.BuildLightFrustum(
            MorningSun, anchor, Vector3.Zero, Cascade0Radius, Resolution);
        Assert.False(WithinDepthRange(ClipDepth(symmetric.ViewProj, caster)),
            "the symmetric box is expected to clip this caster — that IS the bug being fixed");

        var reach = SunShadowMath.CascadeCasterReach(MorningSun, Cascade0Radius, 9000f);
        var extended = SunShadowMath.BuildLightFrustum(
            MorningSun, anchor, Vector3.Zero, Cascade0Radius, Resolution, reach);
        Assert.True(WithinDepthRange(ClipDepth(extended.ViewProj, caster)),
            "the extended box must rasterize an up-sun caster whose shadow lands in the footprint");
    }

    [Fact]
    public void ExtendedBox_StillClipsBehindTheReceivers()
    {
        // Extending the near plane must NOT extend the far plane: the down-sun side only has to hold
        // receivers, and spending depth range there would cost precision for nothing.
        var anchor = new Vector3(91009.9f, 21443f, 7608.6f);
        var behind = anchor - MorningSun * 4000f;
        var reach = SunShadowMath.CascadeCasterReach(MorningSun, Cascade0Radius, 9000f);
        var extended = SunShadowMath.BuildLightFrustum(
            MorningSun, anchor, Vector3.Zero, Cascade0Radius, Resolution, reach);

        Assert.False(WithinDepthRange(ClipDepth(extended.ViewProj, behind)),
            "the down-sun far plane must stay at the cascade's own depth");
    }

    [Fact]
    public void CascadeContains_IsAsymmetric_AcceptingUpSunAndRejectingDownSun()
    {
        var reach = SunShadowMath.CascadeCasterReach(MorningSun, Cascade0Radius, 9000f);
        var upSun = MorningSun * 4000f;
        var downSun = -MorningSun * 4000f;

        Assert.True(
            SunShadowMath.CascadeContains(upSun, MorningSun, Cascade0Radius, 0f, 0f, reach),
            "an up-sun caster inside the extended box must be submitted to this cascade");
        Assert.False(
            SunShadowMath.CascadeContains(downSun, MorningSun, Cascade0Radius, 0f, 0f, reach),
            "a down-sun position is past the far plane and cannot cast into this cascade");
    }

    [Fact]
    public void CascadeContains_StillRejectsLaterally_RegardlessOfReach()
    {
        // The lateral test is where "can this reach the footprint" is actually decided, and the
        // up-sun extension must not weaken it — otherwise every fix is paid for in draw calls.
        var reach = SunShadowMath.CascadeCasterReach(MorningSun, Cascade0Radius, 9000f);
        var lateralAxis = Vector3.Normalize(Vector3.Cross(MorningSun, Vector3.UnitZ));
        var farLaterally = lateralAxis * (Cascade0Radius * 8f);

        Assert.False(
            SunShadowMath.CascadeContains(farLaterally, MorningSun, Cascade0Radius, 0f, 0f, reach),
            "a caster far off the light axis lands its shadow outside the footprint");
    }

    /// <summary>
    ///     THE invariant. Whatever <see cref="SunShadowMath.CascadeContains" /> admits, the frustum
    ///     built with the same reach must actually rasterize — anything else is a caster the CPU
    ///     submitted and the GPU threw away, i.e. a hole in a cascade the pixel shader still selects.
    ///     Swept over the full sphere of caster directions so it cannot pass by luck.
    /// </summary>
    [Fact]
    public void AcceptedCaster_AlwaysRasterizesIntoTheSameCascade()
    {
        var anchor = new Vector3(91009.9f, 21443f, 7608.6f);
        const float sceneZSpan = 9000f;
        var reach = SunShadowMath.CascadeCasterReach(MorningSun, Cascade0Radius, sceneZSpan);
        var frustum = SunShadowMath.BuildLightFrustum(
            MorningSun, anchor, Vector3.Zero, Cascade0Radius, Resolution, reach);

        var admitted = 0;
        for (var yaw = 0; yaw < 360; yaw += 15)
        {
            for (var pitch = -80; pitch <= 80; pitch += 10)
            {
                var y = yaw * MathF.PI / 180f;
                var p = pitch * MathF.PI / 180f;
                var direction = new Vector3(
                    MathF.Cos(p) * MathF.Cos(y), MathF.Cos(p) * MathF.Sin(y), MathF.Sin(p));

                for (var distance = 250f; distance <= 12000f; distance += 250f)
                {
                    var delta = direction * distance;
                    if (!SunShadowMath.CascadeContains(
                            delta, MorningSun, Cascade0Radius, 0f, 0f, reach))
                    {
                        continue;
                    }

                    admitted++;
                    var clipZ = ClipDepth(frustum.ViewProj, anchor + delta);
                    Assert.True(WithinDepthRange(clipZ),
                        $"classifier admitted a caster at {delta} that the frustum clips " +
                        $"(clip.z = {clipZ}); the submission and rasterization bounds have diverged");
                }
            }
        }

        Assert.True(admitted > 0, "the sweep must actually admit casters or it proves nothing");
    }

    [Theory]
    [InlineData(false, 100f, 200f, false)]
    [InlineData(true, 100f, 100f, false)]
    [InlineData(true, 100f, 200f, true)]
    [InlineData(true, 0f, 9000f, true)]
    public void ReferenceExtentChange_DefersOnlyAnIdentityChangingMismatchedCapture(
        bool referenceIdentityChanged,
        float armedSpan,
        float postCullSpan,
        bool expected)
    {
        Assert.Equal(
            expected,
            SunShadowMath.ShouldDeferForReferenceExtentChange(
                referenceIdentityChanged, armedSpan, postCullSpan));
    }
}