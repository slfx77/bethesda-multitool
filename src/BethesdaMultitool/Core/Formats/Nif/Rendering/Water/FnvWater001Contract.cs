using System.Numerics;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Water;

/// <summary>
///     Why the bounded FNV <c>WATER001</c> route did not run. The route is deliberately fail-closed:
///     its refraction source is an opaque-scene approximation, not the retail engine's selectively
///     populated RefractionMap, and it is valid only for one horizontal generated-cell plane.
/// </summary>
internal enum FnvWater001FallbackReason
{
    None = 0,
    NotFalloutNewVegas,
    NonClassicWaterShader,
    Lava,
    OrthographicProjection,
    SceneDepthUnavailable,
    SnapshotUnavailable,
    SnapshotDimensionsInvalid,
    NoVisibleCellWater,
    MissingAppearance,
    MissingAuthoredClassicInputs,
    MissingWaterTypeContext,
    MissingEffectiveWaterType,
    MixedVisibleWaterTypes,
    MixedVisiblePlaneHeights,
    CameraNotAbovePlane,
    InvalidDepthFalloffRange,
    InvalidUnderwaterFogFar,
    InvalidUnderwaterFogRange,
    InvalidAboveWaterFogAmount,
    InvalidRefractionDistortion,
}

/// <summary>
///     Host-visible result of the conservative preflight. A positive result permits the host to
///     snapshot opaque scene color; the renderer repeats every spatial/material check at draw time.
/// </summary>
internal readonly record struct FnvWater001Preflight(
    bool Candidate,
    FnvWater001FallbackReason Reason,
    float PlaneHeight,
    uint? EffectiveWaterFormId)
{
    internal static FnvWater001Preflight Fallback(FnvWater001FallbackReason reason) =>
        new(false, reason, 0f, null);

    internal string ReasonCode => Reason switch
    {
        FnvWater001FallbackReason.None => "eligible",
        FnvWater001FallbackReason.NotFalloutNewVegas => "not-fallout-new-vegas",
        FnvWater001FallbackReason.NonClassicWaterShader => "non-classic-water-shader",
        FnvWater001FallbackReason.Lava => "lava",
        FnvWater001FallbackReason.OrthographicProjection => "orthographic-projection",
        FnvWater001FallbackReason.SceneDepthUnavailable => "scene-depth-unavailable",
        FnvWater001FallbackReason.SnapshotUnavailable => "opaque-snapshot-unavailable",
        FnvWater001FallbackReason.SnapshotDimensionsInvalid => "opaque-snapshot-dimensions-invalid",
        FnvWater001FallbackReason.NoVisibleCellWater => "no-visible-cell-water",
        FnvWater001FallbackReason.MissingAppearance => "missing-water-appearance",
        FnvWater001FallbackReason.MissingAuthoredClassicInputs => "missing-authored-classic-refraction-inputs",
        FnvWater001FallbackReason.MissingWaterTypeContext => "missing-water-type-context",
        FnvWater001FallbackReason.MissingEffectiveWaterType => "missing-effective-water-type",
        FnvWater001FallbackReason.MixedVisibleWaterTypes => "mixed-visible-water-types",
        FnvWater001FallbackReason.MixedVisiblePlaneHeights => "mixed-visible-plane-heights",
        FnvWater001FallbackReason.CameraNotAbovePlane => "camera-not-above-water-plane",
        FnvWater001FallbackReason.InvalidDepthFalloffRange => "invalid-depth-falloff-range",
        FnvWater001FallbackReason.InvalidUnderwaterFogFar => "invalid-underwater-fog-far",
        FnvWater001FallbackReason.InvalidUnderwaterFogRange => "invalid-underwater-fog-range",
        FnvWater001FallbackReason.InvalidAboveWaterFogAmount => "invalid-above-water-fog-amount",
        FnvWater001FallbackReason.InvalidRefractionDistortion => "invalid-refraction-distortion",
        _ => "unknown",
    };
}

/// <summary>Pure scalar inputs for the global WATER001 eligibility gate.</summary>
internal readonly record struct FnvWater001EligibilityInput(
    BethesdaGame Game,
    WaterShaderVariant ShaderVariant,
    bool IsLava,
    bool IsPerspectiveProjection,
    bool HasSceneDepth,
    bool RequireSnapshot,
    bool HasSnapshot,
    uint SnapshotWidth,
    uint SnapshotHeight,
    int VisibleCellCount,
    bool HasAppearance,
    bool HasAuthoredClassicInputs,
    bool HasWaterTypeContext,
    bool HasEffectiveWaterType,
    bool HasMixedWaterTypes,
    bool HasMixedPlaneHeights,
    float PlaneHeight,
    float CameraHeight,
    float DepthFalloffStart,
    float DepthFalloffEnd,
    float UnderwaterFogNear,
    float UnderwaterFogFar,
    float AboveWaterFogAmount,
    float RefractionDistortionAmount,
    uint? EffectiveWaterFormId);

internal readonly record struct FnvWater001DisplacedDepthTap(
    float SceneNdc,
    float SceneDistance,
    float WaterDistance,
    float ScenePointZ);

/// <summary>
///     Pure recovered WATER001 math and eligibility checks. The GPU implementation lives in
///     <c>water_fnv001.frag.hlsl</c>; these methods provide deterministic CPU oracles and keep host preflight
///     decisions independent of D3D12 resource ownership.
/// </summary>
internal static class FnvWater001Contract
{
    internal static FnvWater001Preflight Evaluate(in FnvWater001EligibilityInput input)
    {
        if (input.Game != BethesdaGame.FalloutNewVegas)
            return FnvWater001Preflight.Fallback(FnvWater001FallbackReason.NotFalloutNewVegas);
        if (input.ShaderVariant != WaterShaderVariant.FnvWater000)
            return FnvWater001Preflight.Fallback(FnvWater001FallbackReason.NonClassicWaterShader);
        if (input.IsLava)
            return FnvWater001Preflight.Fallback(FnvWater001FallbackReason.Lava);
        if (!input.IsPerspectiveProjection)
            return FnvWater001Preflight.Fallback(FnvWater001FallbackReason.OrthographicProjection);
        if (!input.HasSceneDepth)
            return FnvWater001Preflight.Fallback(FnvWater001FallbackReason.SceneDepthUnavailable);
        if (input.RequireSnapshot && !input.HasSnapshot)
            return FnvWater001Preflight.Fallback(FnvWater001FallbackReason.SnapshotUnavailable);
        if (input.RequireSnapshot && (input.SnapshotWidth == 0 || input.SnapshotHeight == 0))
            return FnvWater001Preflight.Fallback(FnvWater001FallbackReason.SnapshotDimensionsInvalid);
        if (input.VisibleCellCount <= 0)
            return FnvWater001Preflight.Fallback(FnvWater001FallbackReason.NoVisibleCellWater);
        if (!input.HasAppearance)
            return FnvWater001Preflight.Fallback(FnvWater001FallbackReason.MissingAppearance);
        if (!input.HasAuthoredClassicInputs)
            return FnvWater001Preflight.Fallback(FnvWater001FallbackReason.MissingAuthoredClassicInputs);
        if (!input.HasWaterTypeContext)
            return FnvWater001Preflight.Fallback(FnvWater001FallbackReason.MissingWaterTypeContext);
        if (!input.HasEffectiveWaterType)
            return FnvWater001Preflight.Fallback(FnvWater001FallbackReason.MissingEffectiveWaterType);
        if (input.HasMixedWaterTypes)
            return FnvWater001Preflight.Fallback(FnvWater001FallbackReason.MixedVisibleWaterTypes);
        if (input.HasMixedPlaneHeights)
            return FnvWater001Preflight.Fallback(FnvWater001FallbackReason.MixedVisiblePlaneHeights);
        if (!float.IsFinite(input.PlaneHeight) || !float.IsFinite(input.CameraHeight) ||
            input.CameraHeight <= input.PlaneHeight)
        {
            return FnvWater001Preflight.Fallback(FnvWater001FallbackReason.CameraNotAbovePlane);
        }
        if (!float.IsFinite(input.DepthFalloffStart) || !float.IsFinite(input.DepthFalloffEnd) ||
            input.DepthFalloffEnd <= input.DepthFalloffStart)
        {
            return FnvWater001Preflight.Fallback(FnvWater001FallbackReason.InvalidDepthFalloffRange);
        }
        if (!float.IsFinite(input.UnderwaterFogFar) || input.UnderwaterFogFar <= 0f)
            return FnvWater001Preflight.Fallback(FnvWater001FallbackReason.InvalidUnderwaterFogFar);
        if (!float.IsFinite(input.UnderwaterFogNear) ||
            input.UnderwaterFogFar <= input.UnderwaterFogNear)
        {
            return FnvWater001Preflight.Fallback(FnvWater001FallbackReason.InvalidUnderwaterFogRange);
        }
        if (!float.IsFinite(input.AboveWaterFogAmount) ||
            input.AboveWaterFogAmount < 0f || input.AboveWaterFogAmount > 1f)
        {
            return FnvWater001Preflight.Fallback(FnvWater001FallbackReason.InvalidAboveWaterFogAmount);
        }
        if (!float.IsFinite(input.RefractionDistortionAmount) || input.RefractionDistortionAmount < 0f)
            return FnvWater001Preflight.Fallback(FnvWater001FallbackReason.InvalidRefractionDistortion);

        return new FnvWater001Preflight(true, FnvWater001FallbackReason.None,
            input.PlaneHeight, input.EffectiveWaterFormId);
    }

    /// <summary>
    ///     Reconstructs the retail depth target's two independent lanes from the main perspective
    ///     depth ray. No saturation is applied: WATER001 performs its correction/saturation later.
    /// </summary>
    internal static bool TryReconstructDepthLanes(
        Vector3 eye,
        Vector3 waterPoint,
        float sceneDistance,
        float waterDistance,
        float planeHeight,
        float underwaterFogFar,
        out Vector2 depth,
        out Vector3 scenePoint)
    {
        depth = default;
        scenePoint = default;
        if (!IsFinite(eye) || !IsFinite(waterPoint) ||
            !float.IsFinite(sceneDistance) || !float.IsFinite(waterDistance) ||
            !float.IsFinite(planeHeight) || !float.IsFinite(underwaterFogFar) ||
            waterDistance <= 0f || sceneDistance <= waterDistance || underwaterFogFar <= 0f ||
            eye.Z <= planeHeight || MathF.Abs(waterPoint.Z - planeHeight) > 1e-3f)
        {
            return false;
        }

        var rayScale = sceneDistance / waterDistance;
        scenePoint = eye + (waterPoint - eye) * rayScale;
        if (!IsFinite(scenePoint) || scenePoint.Z >= planeHeight)
        {
            depth = default;
            scenePoint = default;
            return false;
        }

        depth = new Vector2(
            Vector3.Distance(scenePoint, waterPoint) / underwaterFogFar,
            (waterPoint.Z - scenePoint.Z) / underwaterFogFar);
        if (!IsFinite(depth) || depth.X < 0f || depth.Y <= 0f)
        {
            depth = default;
            scenePoint = default;
            return false;
        }
        return true;
    }

    internal static Vector2 CorrectDepthLanes(Vector2 depth, float noiseFade) =>
        Vector2.Clamp(Vector2.Lerp(Vector2.One, depth, noiseFade), Vector2.Zero, Vector2.One);

    internal static Vector2 DistortionDelta(
        Vector2 rawDepth,
        float depthFactor,
        float distortionScale,
        Vector3 normal) =>
        rawDepth.Y * depthFactor * distortionScale * new Vector2(normal.X, normal.Y);

    /// <summary>Projects the displaced world point exactly as the water VS does, then validates one
    /// bilinear sample footprint against the single-sample opaque snapshot.</summary>
    internal static bool TryProjectSnapshotUv(
        Matrix4x4 viewProjection,
        Vector3 renderOrigin,
        Vector3 displacedWorldPoint,
        uint snapshotWidth,
        uint snapshotHeight,
        out Vector2 uv)
    {
        uv = default;
        if (snapshotWidth == 0 || snapshotHeight == 0 || !IsFinite(displacedWorldPoint) ||
            !IsFinite(renderOrigin))
        {
            return false;
        }

        var p = displacedWorldPoint - renderOrigin;
        // System.Numerics matrices are uploaded byte-for-byte and interpreted column-major by HLSL.
        // Therefore GPU mul(uViewProj, p) is exactly CPU Vector4.Transform(p, viewProjection), as in
        // the renderer's other projection oracles. A hand-written matrix-times-column multiply would
        // apply the opposite convention and validate UVs that the shader samples somewhere else.
        var clip = Vector4.Transform(new Vector4(p, 1f), viewProjection);
        if (!IsFinite(clip) || clip.W <= 1e-5f || clip.Z < 0f || clip.Z > clip.W)
        {
            return false;
        }

        var inverseW = 1f / clip.W;
        uv = new Vector2(
            clip.X * inverseW * 0.5f + 0.5f,
            0.5f - clip.Y * inverseW * 0.5f);
        var halfTexel = new Vector2(0.5f / snapshotWidth, 0.5f / snapshotHeight);
        if (!IsFinite(uv) || uv.X < halfTexel.X || uv.Y < halfTexel.Y ||
            uv.X > 1f - halfTexel.X || uv.Y > 1f - halfTexel.Y)
        {
            uv = default;
            return false;
        }
        return true;
    }

    /// <summary>
    ///     Validates the scene-depth tap at a distorted refraction UV. The bounded main-scene
    ///     snapshot contains opaque foregrounds that retail's selective RefractionMap omits; a tap
    ///     is safe only when it remains behind the displaced surface and reconstructs below water.
    /// </summary>
    internal static bool IsValidDisplacedRefractionSample(
        float sceneNdc,
        float sceneDistance,
        float waterDistance,
        float scenePointZ,
        float planeHeight) =>
        sceneNdc > 0f && sceneNdc <= 1f &&
        float.IsFinite(sceneDistance) && float.IsFinite(waterDistance) &&
        float.IsFinite(scenePointZ) && float.IsFinite(planeHeight) &&
        waterDistance > 0f && sceneDistance > waterDistance && scenePointZ < planeHeight;

    /// <summary>
    ///     The color snapshot uses bilinear filtering, so all four depth texels in the matching
    ///     <c>uv * size - 0.5</c> footprint must be safe. One foreground/above-water contributor is
    ///     enough to reject distortion and retain the original-pixel refraction sample.
    /// </summary>
    internal static bool IsValidDisplacedRefractionFootprint(
        ReadOnlySpan<FnvWater001DisplacedDepthTap> taps,
        float planeHeight)
    {
        if (taps.Length != 4) return false;
        foreach (var tap in taps)
        {
            if (!IsValidDisplacedRefractionSample(
                    tap.SceneNdc,
                    tap.SceneDistance,
                    tap.WaterDistance,
                    tap.ScenePointZ,
                    planeHeight))
            {
                return false;
            }
        }
        return true;
    }

    internal static float AboveWaterFogWeight(
        float correctedPathDepth,
        float underwaterFogNear,
        float underwaterFogFar,
        float aboveWaterFogAmount)
    {
        var range = underwaterFogFar - underwaterFogNear;
        var waterFog = 1f - Math.Clamp(
            underwaterFogFar * (1f - correctedPathDepth) / range, 0f, 1f);
        return waterFog * aboveWaterFogAmount;
    }

    private static bool IsFinite(Vector2 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y);

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFinite(Vector4 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) && float.IsFinite(value.W);
}
