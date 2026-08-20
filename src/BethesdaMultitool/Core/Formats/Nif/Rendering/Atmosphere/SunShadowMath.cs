using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Atmosphere;

/// <summary>
///     Pure math for the viewer's directional (sun/moon) shadow map: fits an orthographic
///     light frustum around the visible scene, snaps it to shadow-map texels so a static sun
///     doesn't shimmer while the camera drifts, and quantizes the inputs into a cache key so
///     the map is only re-rendered when something it depends on actually changed.
///     <para>
///         Kept free of any D3D dependency (unlike <c>ShadowMapRenderer12</c>, which consumes
///         it) so the CLI test TFM can exercise the matrix/key math directly.
///     </para>
///     <para>
///         Conventions match the scene renderers: System.Numerics row-vector matrices uploaded
///         raw to HLSL column-major cbuffers (shaders transform with <c>mul(M, v)</c>), and
///         REVERSED-Z depth (near→1, far→0, GreaterEqual test) like every scene PSO — the
///         ortho swaps its near/far arguments to get that mapping.
///     </para>
/// </summary>
internal static class SunShadowMath
{
    /// <summary>
    ///     Snap step for the coverage center inside the key. The frustum carries
    ///     <c>CenterSnap</c> of extra radius so geometry stays covered while the camera walks
    ///     between key re-snaps.
    /// </summary>
    public const float CenterSnap = 512f;

    /// <summary>
    ///     DOWN-sun half-extent along the light direction, as a multiple of the cascade radius —
    ///     the far plane <see cref="BuildLightFrustum" /> gives its ortho box. The UP-sun side is
    ///     <see cref="CascadeCasterReach" />, which is larger: that side has to hold casters, not
    ///     just receivers.
    /// </summary>
    public const float CascadeDepthExtentFactor = 1.25f;

    /// <summary>
    ///     Lateral reach of a cascade as a multiple of its radius. The ortho box is a SQUARE of
    ///     half-width <c>radius</c> whose lateral axes come from <see cref="Matrix4x4.CreateLookAt" />'s
    ///     internal basis, so a test built on any other perpendicular basis would disagree with it near
    ///     the corners. Using the CIRCUMSCRIBED circle (radius*sqrt(2)) sidesteps that entirely: it is
    ///     rotation-invariant about the light axis and strictly contains the square, so classification
    ///     can never exclude something the box would have covered. It over-includes by ~57% of area,
    ///     which is a trivial price for not depending on a basis convention.
    /// </summary>
    public const float CascadeLateralReachFactor = 1.41422f;

    /// <summary>
    ///     Ceiling on <see cref="CascadeCasterReach" />, as a multiple of the cascade radius.
    ///     <para>
    ///         The reach formula is exact, but its scene-Z-span input is far larger than the geometry
    ///         that can actually shadow the near field. MEASURED at WastelandNV 2026-08-13: the span
    ///         came back ~173,000 world units (back-solved from the logged depth bias), which gave
    ///         cascade 0 a ~104,000-unit depth range. Every real caster then landed in the bottom 5%
    ///         of the stored depth — precisely where a reversed-Z float buffer has its WORST
    ///         precision — and the box admitted a long tail of casters that can never matter.
    ///         (What stretches that span is NOT established: it is the union of DECODED MESH bounds
    ///         over every cull survivor in a 65k-unit cylinder, not OBND, so do not blame the
    ///         displaced-OBND records for it without measuring.)
    ///     </para>
    ///     <para>
    ///         Verified on the same day that clamping 101,418 -> 16,384 at cascade 0 left occupancy
    ///         BIT-IDENTICAL at both repro poses (14.216% / 5.403%) while lifting the used depth
    ///         range from [0, 0.049] to [0, 0.341]: it costs no caster and buys ~7x the precision.
    ///     </para>
    ///     <para>
    ///         Eight radii is chosen against the geometry, not by feel: at cascade 0 it reaches 16,384
    ///         units up-sun, which at the repro's 33° sun elevation clears a caster ~9,000 units above
    ///         the anchor — an order of magnitude more than any structure that can shadow the near
    ///         field, and still 6.4x the ±1.25-radius box that caused the defect. A caster beyond it is
    ///         either not real geometry or is better served by a coarser cascade.
    ///     </para>
    /// </summary>
    public const float CascadeCasterReachRadiiCeiling = 8f;

    /// <summary>
    ///     Builds the light view-projection for a directional light over the visible scene.
    ///     <paramref name="sceneCenter" /> / <paramref name="renderOrigin" /> are ABSOLUTE world
    ///     coordinates; the returned matrix consumes positions RELATIVE to
    ///     <paramref name="renderOrigin" /> — the same space the scene VS hands the rasterizer
    ///     (instance matrices are CPU-folded to that origin), so the shadow pass can replay the
    ///     scene's own instance buffers unchanged.
    /// </summary>
    /// <param name="sunDirection">Normalized world direction TOWARD the light (scene → sun).</param>
    /// <param name="sceneCenter">Absolute world position the map is centered on (the camera).</param>
    /// <param name="renderOrigin">The scene's camera-relative render origin (0 in absolute mode).</param>
    /// <param name="radius">
    ///     Half-extent of the ortho footprint (world units) — covers the
    ///     visibility square around the center.
    /// </param>
    /// <param name="resolution">Shadow map dimension in texels (square).</param>
    /// <param name="casterReach">
    ///     How far UP-SUN of <paramref name="sceneCenter" /> the ortho box must reach to hold every
    ///     caster whose shadow can land in the footprint — the classic CSM extended near plane, and
    ///     the value <see cref="CascadeCasterReach" /> computes. Values below the box's own
    ///     <see cref="CascadeDepthExtentFactor" /> depth are ignored, so the default of 0 reproduces
    ///     the symmetric box exactly.
    /// </param>
    public static LightFrustum BuildLightFrustum(
        Vector3 sunDirection, Vector3 sceneCenter, Vector3 renderOrigin, float radius, int resolution,
        float casterReach = 0f)
    {
        var dir = Vector3.Normalize(sunDirection);
        var center = sceneCenter - renderOrigin;

        // Up axis for the light view: world Z (Bethesda up) unless the sun is near the zenith
        // (FO4's noon apex is ~86-90°, where a Z up vector degenerates in LookAt).
        var up = MathF.Abs(dir.Z) < 0.9f ? new Vector3(0f, 0f, 1f) : new Vector3(0f, 1f, 0f);

        // Depth extents. DOWN-sun (the far plane) only has to hold RECEIVERS, so the cascade's own
        // 1.25*radius is right. UP-sun (the near plane) has to hold CASTERS, and a caster is always
        // up-sun of the surface it darkens — capping that side at 1.25*radius silently clips every
        // caster further toward the light than 2560 units in cascade 0, whose shadow then vanishes
        // from a footprint the pixel shader still selects (user 2026-08-11, "shadows close to the
        // camera disappear"). See CascadeCasterReach.
        var backReach = radius * CascadeDepthExtentFactor;
        var lightReach = MathF.Max(casterReach, backReach);

        // Eye far enough back along the light that every caster sits in front of it (near > 0).
        // The ortho projection itself is invariant to this distance — clip.z works out to
        // (backReach + axial) / (lightReach + backReach) regardless — but the texel snap below is
        // phase-locked to it, so it stays at 2*radius whenever the reach fits.
        var eyeDistance = MathF.Max(radius * 2f, lightReach + radius * 0.75f);
        var eye = center + dir * eyeDistance;
        var view = Matrix4x4.CreateLookAt(eye, center, up);

        // Texel snap: quantize the light-space translation to whole texels so the fitted frustum
        // (and with it every rasterized depth sample) is bit-stable while the world-space center
        // drifts by less than a texel — the classic anti-shimmer for a moving camera / static sun.
        var texelWorldSize = 2f * radius / resolution;
        view.M41 = MathF.Round(view.M41 / texelWorldSize) * texelWorldSize;
        view.M42 = MathF.Round(view.M42 / texelWorldSize) * texelWorldSize;

        // Reversed-Z ortho: swapping the near/far arguments of CreateOrthographic maps
        // view depth near→1, far→0 — matching the scene's GreaterEqual/clear-0 convention.
        var near = eyeDistance - lightReach; // up-sun: must hold every CASTER
        var far = eyeDistance + backReach; // down-sun: only has to hold RECEIVERS
        var proj = Matrix4x4.CreateOrthographic(2f * radius, 2f * radius, far, near);

        // Constant compare bias ≈ two texels of worst-case slope, expressed in the normalized
        // depth the map stores (depth range = far - near world units).
        var normalizedBias = 2f * texelWorldSize / MathF.Max(far - near, 1f);

        // Light-perpendicular billboard basis for the shadow pass's leaf cards (same up-selection
        // as the view above, so the basis is always well-defined).
        var cardRight = Vector3.Normalize(Vector3.Cross(up, dir));
        var cardUp = Vector3.Normalize(Vector3.Cross(dir, cardRight));

        return new LightFrustum(view * proj, texelWorldSize, normalizedBias, cardRight, cardUp);
    }

    /// <summary>
    ///     How far UP-SUN of the cascade anchor the shadow box must reach so that every caster whose
    ///     shadow can land inside the footprint is actually rasterized into the map.
    ///     <para>
    ///         This exists because the two sides of the shadow pipeline ask DIFFERENT questions and
    ///         must not answer them with different numbers. The pixel shader
    ///         (<c>shadow_sampling.hlsli</c>'s <c>ShadowFactor</c>) picks the smallest cascade whose
    ///         footprint contains the RECEIVING PIXEL and returns immediately — there is no fallback
    ///         to a coarser cascade when the finer one holds no occluder. The CPU
    ///         (<see cref="CascadeContains" />) decides which cascades a CASTER is drawn into. So any
    ///         caster the CPU withholds from cascade 0, while a receiving pixel still selects
    ///         cascade 0, produces a hole that reads as "the shadow is missing" — and because the
    ///         anchor is texel-snapped, a metre of camera movement flips casters across the boundary
    ///         and the shadow blinks. A symmetric +/-1.25*radius box makes that inevitable: light
    ///         travels one way, so the up-sun side needs strictly more room than the down-sun side.
    ///     </para>
    ///     <para>
    ///         The bound is exact rather than a fudge factor. A caster's shadow lands at the caster's
    ///         own LATERAL coordinate (that coordinate is invariant along the light axis), so the
    ///         lateral test in <see cref="CascadeContains" /> already decides "can this reach the
    ///         footprint". All this has to add is the axial room such a caster can occupy: the worst
    ///         case is a caster at the top of the scene's Z span sitting at the lateral limit, whose
    ///         axial offset is <c>zSpan*|dir.Z| + lateralLimit*sqrt(1 - dir.Z^2)</c>. It degenerates
    ///         correctly at both ends — the scene's height at a zenith sun, the lateral limit at a
    ///         horizon sun — and is never smaller than the box's own depth.
    ///     </para>
    /// </summary>
    /// <param name="sunDirection">Unit direction TOWARD the light.</param>
    /// <param name="radius">Cascade radius.</param>
    /// <param name="sceneZSpan">World Z extent of the rendered scene (max - min), 0 when unknown.</param>
    public static float CascadeCasterReach(Vector3 sunDirection, float radius, float sceneZSpan)
    {
        var dir = Vector3.Normalize(sunDirection);
        var vertical = MathF.Abs(dir.Z);
        var horizontal = MathF.Sqrt(MathF.Max(1f - vertical * vertical, 0f));
        var lateralLimit = radius * CascadeLateralReachFactor;
        var reach = MathF.Max(sceneZSpan, 0f) * vertical + lateralLimit * horizontal;
        // Clamped ABOVE by the radii ceiling (untrustworthy Z span — see the constant) and BELOW by the
        // box's own depth, so the default/unknown case is bit-identical to the pre-fix symmetric box.
        reach = MathF.Min(reach, radius * CascadeCasterReachRadiiCeiling);
        return MathF.Max(reach, radius * CascadeDepthExtentFactor);
    }

    /// <summary>
    ///     Whether a world-space sphere can CAST INTO the cascade of the given radius centred on the
    ///     shadow anchor. Conservative: a true answer may include spheres the ortho box would clip, but
    ///     a false answer guarantees the sphere cannot cast into that cascade — provided
    ///     <paramref name="casterReach" /> matches the box the frustum was actually built with.
    ///     (Before 2026-08-13 this tested "does the sphere sit INSIDE the box", which is a different
    ///     and strictly narrower question, and it dropped up-sun casters whose shadows landed
    ///     squarely in the footprint.)
    ///     <para>
    ///         Because every cascade shares an anchor and a light direction and differs only in radius,
    ///         the volumes are strictly NESTED — which is what lets a caster be classified once into
    ///         its smallest containing cascade and the replay then draw a contiguous PREFIX per cascade.
    ///     </para>
    /// </summary>
    /// <param name="delta">World-space offset from the shadow anchor to the sphere centre.</param>
    /// <param name="sunDirection">Unit light direction (the same one the cascades were fitted to).</param>
    /// <param name="radius">Cascade radius.</param>
    /// <param name="sphereRadius">Radius of the caster's bounding sphere.</param>
    /// <param name="slack">Extra reach for camera/anchor drift between rebuild and use.</param>
    /// <param name="casterReach">
    ///     Up-sun extent of the box, from <see cref="CascadeCasterReach" />. MUST be the same value
    ///     passed to <see cref="BuildLightFrustum" /> for this cascade: this test decides what is
    ///     SUBMITTED and that one decides what is RASTERIZED, so a disagreement is invisible on the
    ///     CPU and shows up only as missing shadows. Defaulting to 0 collapses to the symmetric box.
    /// </param>
    public static bool CascadeContains(
        Vector3 delta, Vector3 sunDirection, float radius, float sphereRadius, float slack,
        float casterReach = 0f)
    {
        var reach = sphereRadius + slack;
        var axial = Vector3.Dot(delta, sunDirection);
        var backReach = radius * CascadeDepthExtentFactor;
        // ASYMMETRIC, and deliberately so — see CascadeCasterReach. Positive axial is UP-SUN, where
        // casters live; negative is down-sun, where only receivers do.
        if (axial > MathF.Max(casterReach, backReach) + reach || axial < -backReach - reach)
        {
            return false;
        }

        // Perpendicular distance from the light axis, via Pythagoras on the projection.
        var lateralSquared = MathF.Max(delta.LengthSquared() - axial * axial, 0f);
        var lateralLimit = radius * CascadeLateralReachFactor + reach;
        return lateralSquared <= lateralLimit * lateralLimit;
    }

    /// <summary>
    ///     A reference visibility/population change can invalidate the survivor-derived scene Z span
    ///     during the very render whose shadow capture was armed from the previous cull. That frame's
    ///     per-cascade instance prefixes and light frustums must not be published with different spans;
    ///     defer once and let the next frame arm from the post-cull value.
    /// </summary>
    public static bool ShouldDeferForReferenceExtentChange(
        bool referenceIdentityChanged,
        float armedSceneZSpan,
        float postCullSceneZSpan)
    {
        return referenceIdentityChanged && !armedSceneZSpan.Equals(postCullSceneZSpan);
    }

    /// <summary>
    ///     Whether a just-rendered cascade may COMMIT its cached state (pose key, content key, content
    ///     throttle, published anchor). True only when every sub-pass feeding that cascade reached an
    ///     AUTHORITATIVE result — which includes the valid EMPTY one, where the fitted box genuinely
    ///     contains no caster. A pass that could not run (ring exhaustion) leaves the old keys in place
    ///     so the next frame retries instead of caching a degenerate render.
    ///     <para>
    ///         Committing the authoritative EMPTY case is the point. Withholding the keys there left
    ///         the cascade permanently pose-pending, so it re-cleared its target and re-gathered
    ///         terrain EVERY frame forever with nothing to show for it. It still heals through the
    ///         ordinary keys: a streamed-in caster bumps a content version, and camera or sun motion
    ///         changes the pose key.
    ///     </para>
    ///     <para>
    ///         Note that "drew something" is deliberately NOT an input. Whether the box happened to
    ///         contain a caster says nothing about whether the answer is trustworthy, and conflating
    ///         the two is what made an empty-but-correct cascade indistinguishable from a failed one.
    ///     </para>
    /// </summary>
    /// <param name="referenceReplayCompleted">
    ///     <c>ReferenceRenderer12.LastShadowReplayCompleted</c>.
    /// </param>
    /// <param name="terrainCasts">Whether terrain was asked to contribute to this cascade at all.</param>
    /// <param name="terrainReplayCompleted">
    ///     <c>TerrainRenderer12.LastShadowReplayCompleted</c>. Meaningful only when
    ///     <paramref name="terrainCasts" /> — terrain that was never asked to run cannot make a cascade
    ///     unauthoritative.
    /// </param>
    public static bool ShouldCommitCascadeState(
        bool referenceReplayCompleted, bool terrainCasts, bool terrainReplayCompleted)
    {
        return referenceReplayCompleted && (!terrainCasts || terrainReplayCompleted);
    }

    /// <summary>
    ///     Builds the cascade re-render pose key: quantized sun direction, snapped coverage
    ///     center, radius, and content version — the map re-renders only when the key changes.
    /// </summary>
    /// <param name="snap">
    ///     Coverage-center quantum. Defaults to <see cref="CenterSnap" />; callers building a
    ///     PER-CASCADE key pass a step proportional to that cascade's radius, so a far cascade whose
    ///     texels span 64 world units does not re-render for a 512-unit camera move it cannot resolve.
    /// </param>
    public static ShadowKey BuildKey(
        Vector3 sunDirection, Vector3 sceneCenter, float radius, int contentVersion,
        float snap = CenterSnap)
    {
        var dir = Vector3.Normalize(sunDirection);
        var step = snap > 0f ? snap : CenterSnap;
        return new ShadowKey(
            (int)MathF.Round(dir.X * 1000f),
            (int)MathF.Round(dir.Y * 1000f),
            (int)MathF.Round(dir.Z * 1000f),
            (int)MathF.Round(sceneCenter.X / step),
            (int)MathF.Round(sceneCenter.Y / step),
            (int)MathF.Round(sceneCenter.Z / step),
            (int)MathF.Round(radius),
            contentVersion);
    }

    /// <summary>
    ///     Folds the difference between the CURRENT frame's render origin and the origin the map
    ///     was rendered with into the sampling matrix, so a cached map stays valid when the scene's
    ///     snapped camera-relative origin moves (or when a capture samples a live-rendered map):
    ///     the PS transforms its origin-relative <c>vWorldPos</c> with the returned matrix.
    /// </summary>
    public static Matrix4x4 FoldSampleMatrix(Matrix4x4 renderViewProj, Vector3 renderOrigin, Vector3 currentOrigin)
    {
        var delta = currentOrigin - renderOrigin;
        return delta == Vector3.Zero
            ? renderViewProj
            : Matrix4x4.CreateTranslation(delta) * renderViewProj;
    }

    /// <summary>
    ///     One fitted light frustum: <see cref="ViewProj" /> maps render-origin-relative world
    ///     positions into shadow clip space (xy in [-1,1], z reversed in [0,1]).
    ///     <see cref="TexelWorldSize" /> is the world units one shadow-map texel covers (ortho
    ///     width ÷ resolution); <see cref="NormalizedDepthBias" /> is the constant depth-compare
    ///     bias in normalized depth units (applied in the PS on top of the rasterizer bias),
    ///     derived from the texel size and the light depth range.
    ///     <see cref="CardRight" />/<see cref="CardUp" /> span the plane PERPENDICULAR to the light
    ///     — the billboard basis the shadow pass re-faces SpeedTree leaf cards with, so a card that
    ///     happens to be edge-on to the sun (camera-facing) still rasterizes a footprint.
    /// </summary>
    internal readonly record struct LightFrustum(
        Matrix4x4 ViewProj,
        float TexelWorldSize,
        float NormalizedDepthBias,
        Vector3 CardRight,
        Vector3 CardUp);

    /// <summary>
    ///     Cache key for the rendered shadow map: the map is re-rendered only when the light
    ///     direction, the (coarsely snapped) coverage center, the coverage radius, or the
    ///     scene content version changes. Quantization keeps sub-texel camera drift and
    ///     denormal sun-direction noise from thrashing the cache.
    /// </summary>
    internal readonly record struct ShadowKey(
        int DirX,
        int DirY,
        int DirZ,
        int CenterX,
        int CenterY,
        int CenterZ,
        int Radius,
        int ContentVersion);
}
