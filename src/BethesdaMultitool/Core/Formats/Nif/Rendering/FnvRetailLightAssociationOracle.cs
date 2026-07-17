using System.Collections.Immutable;
using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering;

/// <summary>
///     Non-runtime CPU oracle for the recovered FNV property-light influence, final geometry-bound
///     sort, and active-non-shadow filter. The current viewer cannot route this result until the
///     runtime meaning/writer of <c>NiLight+0x10C</c> and world-bound offset parity are proven.
/// </summary>
internal static class FnvRetailLightAssociationOracle
{
    internal const bool RuntimeSupported = false;

    /// <summary>
    ///     Evaluates the local-light sphere/bound relation used by the recovered association calls.
    ///     All recovered callers use bound scale 1. <paramref name="niLightTerm" /> is deliberately
    ///     not named radius: assembly consumes <c>NiLight+0x10C</c>, whose writer semantics remain
    ///     unresolved even though the equation is exact.
    /// </summary>
    internal static FnvRetailLightInfluenceEvaluation EvaluateInfluence(
        Vector3 niLightWorldTranslate,
        Vector3 globalSceneOffset,
        FnvRetailGeometryBound geometryBound,
        float niLightTerm)
    {
        ValidateFinite(niLightWorldTranslate, nameof(niLightWorldTranslate));
        ValidateFinite(globalSceneOffset, nameof(globalSceneOffset));
        ValidateFinite(geometryBound.Center, nameof(geometryBound));
        if (!float.IsFinite(geometryBound.Radius) || geometryBound.Radius < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(geometryBound));
        }

        if (!float.IsFinite(niLightTerm) || niLightTerm <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(niLightTerm));
        }

        var delta = niLightWorldTranslate + globalSceneOffset - geometryBound.Center;
        var centerDistance = delta.Length();
        var surfaceDistance = centerDistance - geometryBound.Radius;
        var score = surfaceDistance / niLightTerm;
        return new FnvRetailLightInfluenceEvaluation(
            delta,
            centerDistance,
            surfaceDistance,
            score,
            surfaceDistance < niLightTerm);
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
                candidate.NiLightTerm).Score;
            for (var earlierIndex = 0; earlierIndex < candidateIndex; earlierIndex++)
            {
                var earlier = sorted[earlierIndex];
                var earlierScore = EvaluateInfluence(
                    earlier.NiLightWorldTranslate,
                    globalSceneOffset,
                    geometryBound,
                    earlier.NiLightTerm).Score;
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
            ValidateCandidate(candidate);
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
        if (candidate.EmitterReferenceFormId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(candidate));
        }

        ValidateFinite(candidate.NiLightWorldTranslate, nameof(candidate));
        if (!float.IsFinite(candidate.NiLightTerm) || candidate.NiLightTerm <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(candidate));
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
    float NiLightTerm,
    byte FrustumCull = 0,
    uint NiLightFlags = 0,
    byte CastShadow = 0);

internal readonly record struct FnvRetailLightInfluenceEvaluation(
    Vector3 Delta,
    float CenterDistance,
    float SurfaceDistance,
    float Score,
    bool BoundWithinLightTerm);
