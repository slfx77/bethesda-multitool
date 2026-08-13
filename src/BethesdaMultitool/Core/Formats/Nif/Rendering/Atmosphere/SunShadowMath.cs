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
        Matrix4x4 ViewProj, float TexelWorldSize, float NormalizedDepthBias,
        Vector3 CardRight, Vector3 CardUp);

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
    /// <param name="radius">Half-extent of the ortho footprint (world units) — covers the
    /// visibility square around the center.</param>
    /// <param name="resolution">Shadow map dimension in texels (square).</param>
    public static LightFrustum BuildLightFrustum(
        Vector3 sunDirection, Vector3 sceneCenter, Vector3 renderOrigin, float radius, int resolution)
    {
        var dir = Vector3.Normalize(sunDirection);
        var center = sceneCenter - renderOrigin;

        // Up axis for the light view: world Z (Bethesda up) unless the sun is near the zenith
        // (FO4's noon apex is ~86-90°, where a Z up vector degenerates in LookAt).
        var up = MathF.Abs(dir.Z) < 0.9f ? new Vector3(0f, 0f, 1f) : new Vector3(0f, 1f, 0f);

        // Eye far enough back along the light that the whole scene sphere sits in front of it.
        // View-space depth of scene geometry then spans [eyeDistance - radius, eyeDistance + radius].
        var eyeDistance = radius * 2f;
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
        var near = eyeDistance - radius * 1.25f;   // slack for geometry above the center plane
        var far = eyeDistance + radius * 1.25f;
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
    ///     Cache key for the rendered shadow map: the map is re-rendered only when the light
    ///     direction, the (coarsely snapped) coverage center, the coverage radius, or the
    ///     scene content version changes. Quantization keeps sub-texel camera drift and
    ///     denormal sun-direction noise from thrashing the cache.
    /// </summary>
    internal readonly record struct ShadowKey(
        int DirX, int DirY, int DirZ, int CenterX, int CenterY, int CenterZ, int Radius, int ContentVersion);

    /// <summary>Snap step for the coverage center inside the key. The frustum carries
    /// <c>CenterSnap</c> of extra radius so geometry stays covered while the camera walks
    /// between key re-snaps.</summary>
    public const float CenterSnap = 512f;

    /// <summary>
    ///     Half-extent along the light direction, as a multiple of the cascade radius. Mirrors the
    ///     +/-1.25*radius depth range <see cref="BuildLightFrustum" /> gives its ortho box.
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
    ///     Whether a world-space sphere can intersect the cascade of the given radius centred on the
    ///     shadow anchor. Conservative: a true answer may include spheres the ortho box would clip, but
    ///     a false answer guarantees the sphere cannot cast into that cascade.
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
    public static bool CascadeContains(
        Vector3 delta, Vector3 sunDirection, float radius, float sphereRadius, float slack)
    {
        var reach = sphereRadius + slack;
        var axial = Vector3.Dot(delta, sunDirection);
        if (MathF.Abs(axial) > (radius * CascadeDepthExtentFactor) + reach)
        {
            return false;
        }

        // Perpendicular distance from the light axis, via Pythagoras on the projection.
        var lateralSquared = MathF.Max(delta.LengthSquared() - (axial * axial), 0f);
        var lateralLimit = (radius * CascadeLateralReachFactor) + reach;
        return lateralSquared <= lateralLimit * lateralLimit;
    }

    /// <summary>Builds the cascade re-render pose key: quantized sun direction, snapped coverage
    /// center, radius, and content version — the map re-renders only when the key changes.</summary>
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
}
