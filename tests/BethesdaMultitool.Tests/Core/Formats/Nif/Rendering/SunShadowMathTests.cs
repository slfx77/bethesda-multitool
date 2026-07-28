using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Atmosphere;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     Locks the sun-shadow-map math contract (<see cref="SunShadowMath" />) the D3D12 shadow pass
///     and the shaders' ShadowFactor rely on: the light frustum maps the coverage center to the
///     middle of the map with REVERSED depth (nearer the light ⇒ larger z), texel snapping keeps
///     the mapping bit-stable under sub-texel camera drift, the origin fold makes a cached map
///     origin-agnostic, and the cache key only changes when an actual input changes.
///     Transform convention: System.Numerics row-vector (<c>Vector4.Transform(v, M)</c>), the same
///     product HLSL's <c>mul(M, v)</c> computes against the raw-uploaded row-major matrix.
/// </summary>
public sealed class SunShadowMathTests
{
    private static readonly Vector3 NoonishSun = Vector3.Normalize(new Vector3(0.3f, 0.2f, 0.9f));

    private static Vector4 Project(Matrix4x4 viewProj, Vector3 originRelativePos)
    {
        return Vector4.Transform(new Vector4(originRelativePos, 1f), viewProj);
    }

    [Fact]
    public void BuildLightFrustum_CenterMapsToMiddleOfMap_WithReversedDepth()
    {
        var center = new Vector3(41000f, -12500f, 900f);
        var frustum = SunShadowMath.BuildLightFrustum(
            NoonishSun, center, Vector3.Zero, 8192f, 4096);

        var clip = Project(frustum.ViewProj, center);
        // Ortho: w stays 1, xy near the map middle (within the texel-snap slack of the center).
        Assert.Equal(1f, clip.W, 3);
        Assert.True(MathF.Abs(clip.X) < 0.01f && MathF.Abs(clip.Y) < 0.01f,
            $"center should project near the map middle, got ({clip.X}, {clip.Y})");
        Assert.InRange(clip.Z, 0.01f, 0.99f);

        // Reversed-Z: a point closer to the light stores a LARGER depth than one farther from it.
        var nearer = Project(frustum.ViewProj, center + NoonishSun * 1000f).Z;
        var farther = Project(frustum.ViewProj, center - NoonishSun * 1000f).Z;
        Assert.True(nearer > clip.Z && clip.Z > farther,
            $"depth must grow toward the light (reversed-Z): {nearer} > {clip.Z} > {farther}");
    }

    [Fact]
    public void BuildLightFrustum_CoversTheRadius_AndClipsBeyondIt()
    {
        var center = new Vector3(1000f, 2000f, 0f);
        const float radius = 4096f;
        var frustum = SunShadowMath.BuildLightFrustum(NoonishSun, center, Vector3.Zero, radius, 4096);

        // A ground point half the radius out is inside the footprint; 3x the radius out is not.
        var inside = Project(frustum.ViewProj, center + new Vector3(radius * 0.5f, 0f, 0f));
        Assert.True(MathF.Abs(inside.X) <= 1f && MathF.Abs(inside.Y) <= 1f,
            $"point at radius/2 must be inside the ortho footprint, got ({inside.X}, {inside.Y})");
        var outside = Project(frustum.ViewProj, center + new Vector3(radius * 3f, 0f, 0f));
        Assert.True(MathF.Abs(outside.X) > 1f || MathF.Abs(outside.Y) > 1f,
            "a point 3 radii out must fall outside the footprint (shader treats it as lit)");
    }

    [Fact]
    public void BuildLightFrustum_TexelSnap_KeepsFixedWorldPointsStable_UnderSubTexelDrift()
    {
        var center = new Vector3(20480f, 20480f, 500f);
        const float radius = 8192f;
        const int resolution = 4096;
        var texel = 2f * radius / resolution; // 4 world units

        var a = SunShadowMath.BuildLightFrustum(NoonishSun, center, Vector3.Zero, radius, resolution);
        var b = SunShadowMath.BuildLightFrustum(
            NoonishSun, center + new Vector3(texel * 0.2f, texel * 0.2f, 0f), Vector3.Zero, radius, resolution);

        Assert.Equal(texel, a.TexelWorldSize, 3);
        // The snap's contract: a fixed world point may shift by a WHOLE number of texels between
        // the two frustums (the rounding boundary), but never by a fraction — sub-texel drift is
        // what makes rasterized shadow edges swim. Assert the fractional texel position of a probe
        // point is preserved on both axes.
        var probe = center + new Vector3(1234f, -567f, 89f);
        var pa = Project(a.ViewProj, probe);
        var pb = Project(b.ViewProj, probe);
        AssertWholeTexelShift(pa.X, pb.X, resolution);
        AssertWholeTexelShift(pa.Y, pb.Y, resolution);
    }

    private static void AssertWholeTexelShift(float clipA, float clipB, int resolution)
    {
        var texelsApart = (clipA - clipB) * 0.5f * resolution;
        var fractional = MathF.Abs(texelsApart - MathF.Round(texelsApart));
        Assert.True(fractional < 0.01f,
            $"probe moved {texelsApart} texels between snapped frustums — must be a whole number");
    }

    [Fact]
    public void BuildLightFrustum_NearZenithSun_StaysFinite()
    {
        // FO4's noon apex is ~86-90° elevation — the LookAt up-vector must not degenerate there.
        var frustum = SunShadowMath.BuildLightFrustum(
            new Vector3(0f, 0f, 1f), new Vector3(100f, 200f, 300f), Vector3.Zero, 4096f, 2048);
        var clip = Project(frustum.ViewProj, new Vector3(100f, 200f, 300f));
        Assert.True(float.IsFinite(clip.X) && float.IsFinite(clip.Y) && float.IsFinite(clip.Z),
            "zenith sun must still produce a finite projection");
    }

    [Fact]
    public void FoldSampleMatrix_MakesACachedMapOriginAgnostic()
    {
        var renderOrigin = new Vector3(8192f, 4096f, 0f);
        var currentOrigin = new Vector3(12288f, 4096f, 0f); // camera crossed a snap cell
        var center = new Vector3(10000f, 5000f, 700f);
        var frustum = SunShadowMath.BuildLightFrustum(NoonishSun, center, renderOrigin, 8192f, 4096);

        var absolute = new Vector3(9000f, 4500f, 650f);
        var atRender = Project(frustum.ViewProj, absolute - renderOrigin);
        var folded = SunShadowMath.FoldSampleMatrix(frustum.ViewProj, renderOrigin, currentOrigin);
        var atCurrent = Project(folded, absolute - currentOrigin);

        Assert.Equal(atRender.X, atCurrent.X, 3);
        Assert.Equal(atRender.Y, atCurrent.Y, 3);
        Assert.Equal(atRender.Z, atCurrent.Z, 3);
    }

    [Fact]
    public void KeyStability_DoesNotImplyFrustumStability_RefreshesMustReplayThePublishedFrustum()
    {
        // The pose key snaps the anchor by CenterSnap, so the raw camera anchor drifts up to
        // ~512 units between key changes. A frustum REBUILT from the drifted anchor is a
        // materially different world→clip mapping than the published one — content rendered with
        // it strobes against the stale published sampling matrices (the whole-cell camera-motion
        // flicker). Any refresh that doesn't re-publish (the wind-animated near-cascade path)
        // must therefore replay the PUBLISHED frustum, origin-folded — never rebuild.
        var published = new Vector3(10_000f, 20_000f, 1_000f);
        var drifted = published + new Vector3(200f, -180f, 0f); // well inside one snap cell
        Assert.Equal(
            SunShadowMath.BuildKey(NoonishSun, published, 2048f, 0),
            SunShadowMath.BuildKey(NoonishSun, drifted, 2048f, 0));

        const int resolution = 4096;
        var atPublished = SunShadowMath.BuildLightFrustum(
            NoonishSun, published, Vector3.Zero, 2048f, resolution);
        var rebuilt = SunShadowMath.BuildLightFrustum(
            NoonishSun, drifted, Vector3.Zero, 2048f, resolution);

        var probe = Project(atPublished.ViewProj, published);
        var probeRebuilt = Project(rebuilt.ViewProj, published);
        var texelsApartX = MathF.Abs(probe.X - probeRebuilt.X) * 0.5f * resolution;
        var texelsApartY = MathF.Abs(probe.Y - probeRebuilt.Y) * 0.5f * resolution;
        Assert.True(texelsApartX > 4f || texelsApartY > 4f,
            $"a same-key rebuilt frustum shifted a fixed world point by ({texelsApartX}, {texelsApartY}) " +
            "texels — if this ever becomes sub-texel the replay-published rule could be revisited");
    }

    [Fact]
    public void BuildKey_IsStableUnderSubSnapDrift_AndChangesWithRealInputs()
    {
        var center = new Vector3(10000f, 20000f, 500f);
        var key = SunShadowMath.BuildKey(NoonishSun, center, 8192f, 7);

        // Sub-snap camera drift and denormal sun noise re-produce the SAME key (no cache thrash)…
        Assert.Equal(key, SunShadowMath.BuildKey(
            NoonishSun * 1.0000001f, center + new Vector3(50f, -50f, 10f), 8192f, 7));
        // …while a real sun move, a coarse camera move, or new streamed content re-render the map.
        Assert.NotEqual(key, SunShadowMath.BuildKey(
            Vector3.Normalize(new Vector3(0.5f, 0.2f, 0.7f)), center, 8192f, 7));
        Assert.NotEqual(key, SunShadowMath.BuildKey(
            NoonishSun, center + new Vector3(SunShadowMath.CenterSnap * 2f, 0f, 0f), 8192f, 7));
        Assert.NotEqual(key, SunShadowMath.BuildKey(NoonishSun, center, 8192f, 8));
    }

    [Fact]
    public void FrameContract_ProtectsLateShadowAllocations_AndDisablesEmptyCascades()
    {
        var frame = SourceContract.ReadAppSource("WorldView3DControl.Frame.cs");
        var shadowMap = ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Camera",
            "D3D12", "ShadowMapRenderer12.cs");

        var reserve = frame.IndexOf(
            "TryReserveTail(ShadowPassRingReservationBytes)", StringComparison.Ordinal);
        var sceneAllocationsStart = frame.IndexOf("BindAtmosphereConstants(", reserve, StringComparison.Ordinal);
        var release = frame.IndexOf("ReleaseTailReservation();", sceneAllocationsStart, StringComparison.Ordinal);
        var shadowPass = frame.IndexOf(
            "RecordSunShadowPass(cmd, referenceRenderOrigin", release, StringComparison.Ordinal);
        Assert.True(reserve >= 0 && reserve < sceneAllocationsStart,
            "the shadow tail must be protected before the first scene constant allocation");
        Assert.True(sceneAllocationsStart < release && release < shadowPass,
            "the protected tail must remain unavailable until immediately before shadow replay");

        Assert.Contains("Span<bool> cascadeHasDraws", frame, StringComparison.Ordinal);
        Assert.Contains("_shadowMap.SetCascadeAvailability(i, drewCascade);", frame, StringComparison.Ordinal);
        Assert.Contains(
            "_shadowMap.Publish(frustums, renderOrigin, cascadeHasDraws);",
            frame,
            StringComparison.Ordinal);
        Assert.Contains("cascadeHasDraws[i] ? 1f : 0f", shadowMap, StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] relativePath)
    {
        return File.ReadAllText(Path.Combine(FindRepoRoot(), Path.Combine(relativePath)));
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}