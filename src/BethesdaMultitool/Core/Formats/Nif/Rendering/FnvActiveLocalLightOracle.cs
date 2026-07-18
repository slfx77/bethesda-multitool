using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering;

/// <summary>
///     Non-runtime CPU oracle for the recovered FNV active local-light passes. Pass ID 220
///     (<c>BSSM_ADT2</c>, SLS2008/SLS2011) handles one property-associated local light; pass ID 143
///     (<c>BSSM_ADT4</c>, SLS2022/SLS2031) handles two or three. Production routing remains disabled
///     until the recovered per-geometry association and prepared-color inputs can be supplied and
///     the remaining retail batching and sampler contracts are recovered.
/// </summary>
internal static class FnvActiveLocalLightOracle
{
    internal const bool RuntimeSupported = false;
    internal const int OneLocalPassId = 220;
    internal const int TwoOrThreeLocalPassId = 143;
    internal const int AttenuationTextureSize = 128;

    /// <summary>
    ///     Reconstructs the PC-final 128x128 attenuation source texels. RGB contain the same byte;
    ///     active ID220 samples normalized red. This is deliberately only the source table: filtering
    ///     and maximum-anisotropy policy remain outside the proven runtime contract.
    /// </summary>
    internal static byte[] GeneratePcFinalAttenuationRed()
    {
        var result = new byte[AttenuationTextureSize * AttenuationTextureSize];
        const double center = (AttenuationTextureSize - 1) * 0.5;
        for (var y = 0; y < AttenuationTextureSize; y++)
        {
            var normalizedY = Math.Abs(y - center) / center;
            for (var x = 0; x < AttenuationTextureSize; x++)
            {
                var normalizedX = Math.Abs(x - center) / center;
                var squaredDistance = Math.Min(
                    (normalizedX * normalizedX) + (normalizedY * normalizedY),
                    1.0);
                result[(y * AttenuationTextureSize) + x] =
                    (byte)Math.Floor(byte.MaxValue * squaredDistance);
            }
        }

        return result;
    }

    internal static float NormalizeAttenuationTexel(byte value) => value / 255f;

    /// <summary>
    ///     Reconstructs the point-light color prepared by FNV's <c>SetLight1x2x</c> path for pass
    ///     IDs 220 and 143. <paramref name="dataColor" /> is the packed LIGH DATA RGB value (red in
    ///     bits 0..7). Non-HDR mode applies only an upper fade clamp; negative fade remains signed.
    ///     Point lights are hard-black whenever the geometry property's forced-darkness value is
    ///     below one. The shadow LOD dimmer is emitted as W and does not scale RGB.
    /// </summary>
    internal static FnvPreparedPointLightColor PreparePointLightColor(
        uint dataColor,
        bool negative,
        float dataFade,
        bool hdr,
        float forcedDarkness,
        float lodDimmer,
        float shadowLodDimmer)
    {
        ValidateFinite(dataFade, nameof(dataFade));
        ValidateFinite(forcedDarkness, nameof(forcedDarkness));
        ValidateFinite(lodDimmer, nameof(lodDimmer));
        ValidateFinite(shadowLodDimmer, nameof(shadowLodDimmer));

        var sign = negative ? -1f : 1f;
        var signedDataRgb = new Vector3(
            (byte)(dataColor & 0xff),
            (byte)((dataColor >> 8) & 0xff),
            (byte)((dataColor >> 16) & 0xff)) * (sign / byte.MaxValue);
        var dimmer = hdr ? dataFade : MathF.Min(dataFade, 1f);
        var rgb = signedDataRgb * (dimmer * forcedDarkness * lodDimmer);
        if (forcedDarkness < 1f)
        {
            rgb = Vector3.Zero;
        }

        return new FnvPreparedPointLightColor(rgb, shadowLodDimmer);
    }

    /// <summary>
    ///     Models the ID220 vertex shader at one vertex. The projected sun direction is normalized
    ///     in the vertex shader. The point direction is normalized in object space before the
    ///     authored T/B/N projection, then normalized again by the pixel oracle after raster
    ///     interpolation. The attenuation coordinate is
    ///     <c>.5 * ((lightPosition - vertexPosition) / radius) + .5</c>, with <c>w=.5</c>.
    /// </summary>
    internal static FnvActiveId220Interpolants BuildId220VertexInterpolants(
        Vector3 vertexPositionObject,
        Vector3 localLightPositionObject,
        float localLightRadius,
        Vector3 tangent,
        Vector3 bitangent,
        Vector3 vertexNormal,
        Vector3 sunLightData)
    {
        ValidateRadius(localLightRadius);
        var pointDelta = localLightPositionObject - vertexPositionObject;
        var normalizedPointDirection = NormalizeRequired(pointDelta, nameof(localLightPositionObject));
        var sunTangentSpace = NormalizeRequired(
            ProjectThroughAuthoredBasis(tangent, bitangent, vertexNormal, sunLightData),
            nameof(sunLightData));
        var localTangentSpace = ProjectThroughAuthoredBasis(
            tangent, bitangent, vertexNormal, normalizedPointDirection);
        var attenuationCoordinates = new Vector4(
            ((pointDelta / localLightRadius) * 0.5f) + new Vector3(0.5f),
            0.5f);
        return new FnvActiveId220Interpolants(
            sunTangentSpace,
            localTangentSpace,
            attenuationCoordinates);
    }

    /// <summary>
    ///     Evaluates the no-fog ID220 pixel equation from already-interpolated VS values and caller-
    ///     supplied attenuation samples. Dots and attenuation remain signed; only the aggregate RGB
    ///     light sum is clamped at zero.
    /// </summary>
    internal static FnvActiveId220Evaluation EvaluateId220Pixel(
        FnvClassicBasicShaderMode classifierMode,
        Vector3 normalMapSample,
        Vector3 interpolatedSunTangentSpace,
        Vector3 interpolatedLocalTangentSpace,
        float attenuationXyRed,
        float attenuationZRed,
        Vector3 ambientRgb,
        Vector3 sunRgb,
        Vector3 preparedLocalRgb,
        Vector3 baseRgb,
        Vector3 vertexRgb)
    {
        ValidateMode(classifierMode);
        ValidateFiniteVector(interpolatedSunTangentSpace, nameof(interpolatedSunTangentSpace));
        ValidateNormalizedTextureSample(attenuationXyRed, nameof(attenuationXyRed));
        ValidateNormalizedTextureSample(attenuationZRed, nameof(attenuationZRed));
        var normalizedDecodedNormal = DecodeNormal(normalMapSample);
        var normalizedLocal = NormalizeRequired(
            interpolatedLocalTangentSpace, nameof(interpolatedLocalTangentSpace));
        var sunDot = Vector3.Dot(normalizedDecodedNormal, interpolatedSunTangentSpace);
        var localDot = Vector3.Dot(normalizedDecodedNormal, normalizedLocal);
        var attenuation = 1f - attenuationXyRed - attenuationZRed;
        var totalBeforeClamp = ambientRgb +
                               (sunRgb * sunDot) +
                               (preparedLocalRgb * (localDot * attenuation));
        var shade = Vector3.Max(totalBeforeClamp, Vector3.Zero);
        var rgb = Vector3.Multiply(
            EffectiveBase(classifierMode, baseRgb, vertexRgb),
            shade);
        return new FnvActiveId220Evaluation(
            normalizedDecodedNormal,
            interpolatedSunTangentSpace,
            normalizedLocal,
            sunDot,
            localDot,
            attenuation,
            totalBeforeClamp,
            shade,
            rgb);
    }

    /// <summary>
    ///     Models the ID143 sun-direction VS output at one vertex. Retail normalizes this authored
    ///     T/B/N projection before interpolation; the pixel shader does not normalize it again.
    /// </summary>
    internal static Vector3 BuildId143SunVertexInterpolant(
        Vector3 tangent,
        Vector3 bitangent,
        Vector3 vertexNormal,
        Vector3 sunLightData) =>
        NormalizeRequired(
            ProjectThroughAuthoredBasis(tangent, bitangent, vertexNormal, sunLightData),
            nameof(sunLightData));

    /// <summary>
    ///     Models one enabled, pre-count-mask ID143 local-light VS direction at one vertex. Retail
    ///     also interpolates the object-space vertex position separately; radius is consumed only
    ///     by the pixel shader. Slot-count masking is represented by the pixel oracle's shipped
    ///     3/4 gate rather than by this per-slot diagnostic helper.
    /// </summary>
    internal static FnvActiveId143LocalInterpolants BuildId143LocalVertexInterpolants(
        Vector3 vertexPositionObject,
        Vector3 localLightPositionObject,
        Vector3 tangent,
        Vector3 bitangent,
        Vector3 vertexNormal)
    {
        var pointDelta = localLightPositionObject - vertexPositionObject;
        var normalizedPointDirection = NormalizeRequired(pointDelta, nameof(localLightPositionObject));
        return new FnvActiveId143LocalInterpolants(
            ProjectThroughAuthoredBasis(
                tangent, bitangent, vertexNormal, normalizedPointDirection));
    }

    /// <summary>
    ///     Evaluates the no-fog ID143 pixel equation. <paramref name="totalLightCountGate" /> is the
    ///     shader's <c>EmittanceColor.w</c>: values greater than 1/2/3 enable local slots 0/1/2.
    ///     Shipped ID143 uses 3 for two locals or 4 for three locals. Each signed contribution is
    ///     accumulated before the single component-wise clamp.
    /// </summary>
    internal static FnvActiveId143Evaluation EvaluateId143Pixel(
        FnvClassicBasicShaderMode classifierMode,
        Vector3 normalMapSample,
        Vector3 interpolatedSunTangentSpace,
        Vector3 interpolatedObjectPosition,
        FnvActiveId143LocalInterpolants local0,
        FnvActiveId143LocalConstants local0Constants,
        FnvActiveId143LocalInterpolants local1,
        FnvActiveId143LocalConstants local1Constants,
        FnvActiveId143LocalInterpolants local2,
        FnvActiveId143LocalConstants local2Constants,
        float totalLightCountGate,
        Vector3 ambientRgb,
        Vector3 sunRgb,
        Vector3 baseRgb,
        Vector3 vertexRgb)
    {
        ValidateMode(classifierMode);
        if (totalLightCountGate != 3f && totalLightCountGate != 4f)
        {
            throw new ArgumentOutOfRangeException(nameof(totalLightCountGate));
        }

        ValidateFiniteVector(interpolatedSunTangentSpace, nameof(interpolatedSunTangentSpace));
        ValidateFiniteVector(interpolatedObjectPosition, nameof(interpolatedObjectPosition));
        var normalizedDecodedNormal = DecodeNormal(normalMapSample);
        var sunDot = Vector3.Dot(normalizedDecodedNormal, interpolatedSunTangentSpace);
        var local0Evaluation = EvaluateId143Local(
            normalizedDecodedNormal,
            interpolatedObjectPosition,
            local0,
            local0Constants,
            totalLightCountGate > 1f);
        var local1Evaluation = EvaluateId143Local(
            normalizedDecodedNormal,
            interpolatedObjectPosition,
            local1,
            local1Constants,
            totalLightCountGate > 2f);
        var local2Evaluation = EvaluateId143Local(
            normalizedDecodedNormal,
            interpolatedObjectPosition,
            local2,
            local2Constants,
            totalLightCountGate > 3f);
        var totalBeforeClamp = ambientRgb +
                               (sunRgb * sunDot) +
                               local0Evaluation.Contribution +
                               local1Evaluation.Contribution +
                               local2Evaluation.Contribution;
        var shade = Vector3.Max(totalBeforeClamp, Vector3.Zero);
        var rgb = Vector3.Multiply(
            EffectiveBase(classifierMode, baseRgb, vertexRgb),
            shade);
        return new FnvActiveId143Evaluation(
            normalizedDecodedNormal,
            interpolatedSunTangentSpace,
            sunDot,
            local0Evaluation,
            local1Evaluation,
            local2Evaluation,
            totalBeforeClamp,
            shade,
            rgb);
    }

    private static FnvActiveId143LocalEvaluation EvaluateId143Local(
        Vector3 normalizedDecodedNormal,
        Vector3 interpolatedObjectPosition,
        in FnvActiveId143LocalInterpolants interpolants,
        in FnvActiveId143LocalConstants constants,
        bool enabled)
    {
        if (!enabled)
        {
            // Fail-closed diagnostic sentinel. These zeros deliberately do not claim literal
            // dormant-slot shader intermediates; the retail slot is not consumed for this gate.
            return new FnvActiveId143LocalEvaluation(
                false,
                Vector3.Zero,
                Vector3.Zero,
                0f,
                0f,
                Vector3.Zero);
        }

        ValidateRadius(constants.Radius);
        ValidateFiniteVector(constants.PositionObject, nameof(constants.PositionObject));
        var normalizedDirection = NormalizeRequired(
            interpolants.TangentSpaceDirection,
            nameof(interpolants.TangentSpaceDirection));
        var rawSignedDot = Vector3.Dot(normalizedDecodedNormal, normalizedDirection);
        var positionOverRadius =
            (constants.PositionObject - interpolatedObjectPosition) / constants.Radius;
        ValidateFiniteVector(positionOverRadius, nameof(constants.PositionObject));
        var rawAttenuation = 1f - Vector3.Dot(positionOverRadius, positionOverRadius);
        var contribution = constants.PreparedColorRgb * (rawSignedDot * rawAttenuation);
        return new FnvActiveId143LocalEvaluation(
            true,
            normalizedDirection,
            positionOverRadius,
            rawSignedDot,
            rawAttenuation,
            contribution);
    }

    private static Vector3 DecodeNormal(Vector3 normalMapSample) =>
        NormalizeRequired(
            (normalMapSample - new Vector3(0.5f)) * 2f,
            nameof(normalMapSample));

    private static Vector3 ProjectThroughAuthoredBasis(
        Vector3 tangent,
        Vector3 bitangent,
        Vector3 vertexNormal,
        Vector3 value) =>
        new(
            Vector3.Dot(tangent, value),
            Vector3.Dot(bitangent, value),
            Vector3.Dot(vertexNormal, value));

    private static Vector3 EffectiveBase(
        FnvClassicBasicShaderMode classifierMode,
        Vector3 baseRgb,
        Vector3 vertexRgb) =>
        classifierMode == FnvClassicBasicShaderMode.Sls1013VertexColor
            ? Vector3.Multiply(baseRgb, vertexRgb)
            : baseRgb;

    private static void ValidateMode(FnvClassicBasicShaderMode classifierMode)
    {
        if (classifierMode is not (
                FnvClassicBasicShaderMode.Sls1009 or
                FnvClassicBasicShaderMode.Sls1013VertexColor))
        {
            throw new ArgumentOutOfRangeException(
                nameof(classifierMode), classifierMode,
                "An ordinary classified FNV material is required.");
        }
    }

    private static void ValidateRadius(float radius)
    {
        if (!float.IsFinite(radius) || radius <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(radius));
        }
    }

    private static void ValidateNormalizedTextureSample(float sample, string parameterName)
    {
        if (!float.IsFinite(sample) || sample < 0f || sample > 1f)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static void ValidateFinite(float value, string parameterName)
    {
        if (!float.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static void ValidateFiniteVector(Vector3 value, string parameterName)
    {
        if (!float.IsFinite(value.X) ||
            !float.IsFinite(value.Y) ||
            !float.IsFinite(value.Z))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static Vector3 NormalizeRequired(Vector3 value, string parameterName)
    {
        var lengthSquared = value.LengthSquared();
        if (!float.IsFinite(lengthSquared) || lengthSquared <= 0f)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return value / MathF.Sqrt(lengthSquared);
    }
}

internal readonly record struct FnvActiveId220Interpolants(
    Vector3 SunTangentSpace,
    Vector3 LocalTangentSpace,
    Vector4 AttenuationCoordinates);

internal readonly record struct FnvPreparedPointLightColor(
    Vector3 Rgb,
    float ShadowLodDimmer);

internal readonly record struct FnvActiveId220Evaluation(
    Vector3 NormalizedDecodedNormal,
    Vector3 InterpolatedSunTangentSpace,
    Vector3 NormalizedLocalTangentSpace,
    float RawSignedSunDot,
    float RawSignedLocalDot,
    float RawAttenuation,
    Vector3 TotalBeforeClamp,
    Vector3 Shade,
    Vector3 Rgb);

internal readonly record struct FnvActiveId143LocalInterpolants(
    Vector3 TangentSpaceDirection);

internal readonly record struct FnvActiveId143LocalConstants(
    Vector3 PositionObject,
    float Radius,
    Vector3 PreparedColorRgb);

internal readonly record struct FnvActiveId143LocalEvaluation(
    bool Enabled,
    Vector3 NormalizedTangentSpaceDirection,
    Vector3 PointDeltaOverRadius,
    float RawSignedDot,
    float RawAttenuation,
    Vector3 Contribution);

internal readonly record struct FnvActiveId143Evaluation(
    Vector3 NormalizedDecodedNormal,
    Vector3 InterpolatedSunTangentSpace,
    float RawSignedSunDot,
    FnvActiveId143LocalEvaluation Local0,
    FnvActiveId143LocalEvaluation Local1,
    FnvActiveId143LocalEvaluation Local2,
    Vector3 TotalBeforeClamp,
    Vector3 Shade,
    Vector3 Rgb);
