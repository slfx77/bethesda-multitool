using System.Collections.Immutable;
using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Lighting;

/// <summary>
///     Non-runtime CPU oracle for the recovered FNV property-light influence, final geometry-bound
///     sort, and active-non-shadow filter. The current viewer cannot route this result until retail
///     candidate sourcing and world-bound offset parity are proven.
/// </summary>
internal static class FnvRetailLightAssociationOracle
{
    internal const bool RuntimeSupported = false;

    /// <summary>
    ///     Evaluates the local-light sphere/bound relation used by the recovered association calls.
    ///     All recovered callers use bound scale 1. <paramref name="effectiveRadius" /> is the
    ///     base LIGH radius plus signed REFR ExtraRadius; <c>TESObjectLIGH::GenDynamic</c> copies it
    ///     into <c>NiLight::m_kSpec.rgb</c> before association.
    /// </summary>
    internal static FnvRetailLightInfluenceEvaluation EvaluateInfluence(
        Vector3 niLightWorldTranslate,
        Vector3 globalSceneOffset,
        FnvRetailGeometryBound geometryBound,
        float effectiveRadius)
    {
        ValidateFinite(niLightWorldTranslate, nameof(niLightWorldTranslate));
        ValidateFinite(globalSceneOffset, nameof(globalSceneOffset));
        ValidateFinite(geometryBound.Center, nameof(geometryBound));
        if (!float.IsFinite(geometryBound.Radius) || geometryBound.Radius < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(geometryBound));
        }

        if (!float.IsFinite(effectiveRadius) || effectiveRadius <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(effectiveRadius));
        }

        var delta = niLightWorldTranslate + globalSceneOffset - geometryBound.Center;
        var centerDistance = delta.Length();
        var surfaceDistance = centerDistance - geometryBound.Radius;
        var score = surfaceDistance / effectiveRadius;
        return new FnvRetailLightInfluenceEvaluation(
            delta,
            centerDistance,
            surfaceDistance,
            score,
            surfaceDistance < effectiveRadius);
    }

    /// <summary>
    ///     Replays <c>BSShaderLightingProperty::ResortLights</c>: stable ascending score for this
    ///     geometry bound. Equal scores retain the current attached-list order. No candidate cap is
    ///     applied here; retail caps later while constructing a render pass.
    /// </summary>
    internal static ImmutableArray<FnvRetailAttachedLightCandidate> StableSortForGeometry(
        IEnumerable<FnvRetailAttachedLightCandidate> attachedInCurrentOrder,
        Vector3 globalSceneOffset,
        FnvRetailGeometryBound geometryBound)
    {
        ArgumentNullException.ThrowIfNull(attachedInCurrentOrder);
        ValidateBoundAndOffset(globalSceneOffset, geometryBound);
        var sorted = attachedInCurrentOrder.ToList();
        foreach (var candidate in sorted)
        {
            ValidateCandidate(candidate);
        }

        for (var candidateIndex = 1; candidateIndex < sorted.Count; candidateIndex++)
        {
            var candidate = sorted[candidateIndex];
            var candidateScore = EvaluateInfluence(
                candidate.NiLightWorldTranslate,
                globalSceneOffset,
                geometryBound,
                candidate.EffectiveRadius).Score;
            for (var earlierIndex = 0; earlierIndex < candidateIndex; earlierIndex++)
            {
                var earlier = sorted[earlierIndex];
                var earlierScore = EvaluateInfluence(
                    earlier.NiLightWorldTranslate,
                    globalSceneOffset,
                    geometryBound,
                    earlier.EffectiveRadius).Score;
                if (candidateScore >= earlierScore) continue;

                sorted.RemoveAt(candidateIndex);
                sorted.Insert(earlierIndex, candidate);
                break;
            }
        }

        return sorted.ToImmutableArray();
    }

    /// <summary>Exact recovered active, non-shadow predicate; input order is preserved.</summary>
    internal static ImmutableArray<FnvRetailAttachedLightCandidate> FilterActiveNonShadowInOrder(
        IEnumerable<FnvRetailAttachedLightCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var active = ImmutableArray.CreateBuilder<FnvRetailAttachedLightCandidate>();
        foreach (var candidate in candidates)
        {
            // These are the three recovered runtime fields. Influence inputs and the
            // diagnostic emitter FormID are deliberately irrelevant to this predicate.
            if (candidate.FrustumCull != byte.MaxValue &&
                (candidate.NiLightFlags & 0x01u) == 0 &&
                candidate.CastShadow != 1)
            {
                active.Add(candidate);
            }
        }

        return active.ToImmutable();
    }

    private static void ValidateCandidate(FnvRetailAttachedLightCandidate candidate)
    {
        ValidateFinite(candidate.NiLightWorldTranslate, nameof(candidate));
        if (!float.IsFinite(candidate.EffectiveRadius) || candidate.EffectiveRadius <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(candidate));
        }
    }

    private static void ValidateBoundAndOffset(
        Vector3 globalSceneOffset,
        FnvRetailGeometryBound geometryBound)
    {
        ValidateFinite(globalSceneOffset, nameof(globalSceneOffset));
        ValidateFinite(geometryBound.Center, nameof(geometryBound));
        if (!float.IsFinite(geometryBound.Radius) || geometryBound.Radius < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(geometryBound));
        }
    }

    private static void ValidateFinite(Vector3 value, string parameterName)
    {
        if (!float.IsFinite(value.X) ||
            !float.IsFinite(value.Y) ||
            !float.IsFinite(value.Z))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}

internal readonly record struct FnvRetailGeometryBound(Vector3 Center, float Radius);

internal readonly record struct FnvRetailAttachedLightCandidate(
    uint EmitterReferenceFormId,
    Vector3 NiLightWorldTranslate,
    float EffectiveRadius,
    byte FrustumCull = 0,
    uint NiLightFlags = 0,
    byte CastShadow = 0);

internal readonly record struct FnvRetailLightInfluenceEvaluation(
    Vector3 Delta,
    float CenterDistance,
    float SurfaceDistance,
    float Score,
    bool BoundWithinEffectiveRadius);
