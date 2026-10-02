using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     RE-19 (docs/formats/nif-animation-engine-behavior-20260925.md): the engine's boundary tangents of a component TBC key
///     group (NiTCBFloatKey and NiTCBPosKey::FillDerivedVals), in the form Shared's
///     <see cref="SceneTbcEndpointTangents" /> takes. Pure.
/// </summary>
/// <remarks>
///     <para>
///         The engine gives each endpoint a phantom neighbour mirrored through it, rounded to Float32, with unit interval
///         lengths, and evaluates CalculateDVals with the endpoint key's own tension, continuity and bias:
///         StartOutgoing = Dv(P0, sub1: (P0 * 2) - P1, plus1: P1, 1, 1).Out and
///         EndIncoming = Dv(Pn-1, sub1: Pn-2, plus1: (Pn-1 * 2) - Pn-2, 1, 1).In, per component.
///     </para>
///     <para>
///         Every operation is a float operation on a float local, in the rule's order: no double intermediate and no
///         fused multiply-add. That reproduces the engines bit for bit; the algebraically equal closed forms
///         (1-T0)(1+C0*B0)(P1-P0) and (1-Tn-1)(1-Cn-1*Bn-1)(Pn-1 - Pn-2) are off by up to 16 ulp on 237 of 6,696 retail
///         endpoint values, because the mirrored neighbour is rounded to Float32.
///     </para>
///     <para>
///         The tangents are normalized-segment tangents: never multiplied by a segment duration and never computed with
///         the real first or last interval. A group of one key has no endpoints (the engine writes none, and Shared
///         yields zero tangents).
///     </para>
/// </remarks>
internal static class NifModelTbcEndpoints
{
    /// <summary>
    ///     Computes a TBC key group's boundary tangents, each stored component repeated <paramref name="replication" />
    ///     times (3 for a scale that becomes (s, s, s)).
    /// </summary>
    /// <param name="group">A float or Vector3 group of key type TBC (3).</param>
    /// <param name="replication">How many times each stored component is repeated in the Shared vector.</param>
    /// <param name="endpoints">The tangents; null for a group of fewer than two keys.</param>
    /// <returns>False when an endpoint tangent is not finite (Shared cannot hold it); true otherwise.</returns>
    /// <exception cref="ArgumentException">The group is not a float or Vector3 TBC group.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The replication is not positive.</exception>
    public static bool TryCompute(in NifKeyGroupView group, int replication, out SceneTbcEndpointTangents? endpoints)
    {
        if (group.Layout is not (NifKeyValueLayout.Float or NifKeyValueLayout.Vector3) ||
            group.KeyType != (uint)NifKeyInterpolation.Tbc)
        {
            throw new ArgumentException(
                $"TBC endpoints need a float or Vector3 group of key type 3, not a {group.Layout} group " +
                $"of key type {group.KeyType}.",
                nameof(group));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(replication);
        endpoints = null;
        var count = group.Count;
        if (count < 2)
        {
            return true;
        }

        var stored = group.ComponentCount;
        var width = stored * replication;
        var start = new float[width];
        var end = new float[width];
        var last = count - 1;
        var firstTension = Float(group.TensionBits(0));
        var firstContinuity = Float(group.ContinuityBits(0));
        var firstBias = Float(group.BiasBits(0));
        var lastTension = Float(group.TensionBits(last));
        var lastContinuity = Float(group.ContinuityBits(last));
        var lastBias = Float(group.BiasBits(last));
        for (var component = 0; component < stored; component++)
        {
            var startOutgoing = StartOutgoing(group.Value(0, component), group.Value(1, component),
                firstTension, firstContinuity, firstBias);
            var endIncoming = EndIncoming(group.Value(last - 1, component), group.Value(last, component),
                lastTension, lastContinuity, lastBias);
            if (!float.IsFinite(startOutgoing) || !float.IsFinite(endIncoming))
            {
                return false;
            }

            for (var copy = 0; copy < replication; copy++)
            {
                start[component * replication + copy] = startOutgoing;
                end[component * replication + copy] = endIncoming;
            }
        }

        endpoints = new SceneTbcEndpointTangents(start, end);
        return true;
    }

    /// <summary>
    ///     The tangent leaving the first key (key 0's DD): the mirrored neighbour (first * 2) - second, rounded to Float32,
    ///     and unit interval lengths.
    /// </summary>
    /// <param name="first">The first key's value (one component).</param>
    /// <param name="second">The second key's value (the same component).</param>
    /// <param name="tension">The first key's tension (its first stored TBC float).</param>
    /// <param name="continuity">The first key's continuity (its second stored TBC float).</param>
    /// <param name="bias">The first key's bias (its third stored TBC float).</param>
    /// <returns>The normalized-segment outgoing tangent.</returns>
    public static float StartOutgoing(float first, float second, float tension, float continuity, float bias)
    {
        var doubled = first * 2f;
        var mirrored = doubled - second;
        return Evaluate(first, tension, continuity, bias, mirrored, second, 1f, 1f).Outgoing;
    }

    /// <summary>
    ///     The tangent entering the last key (key n-1's DS): the mirrored neighbour (last * 2) - previous, rounded to
    ///     Float32, and unit interval lengths.
    /// </summary>
    /// <param name="previous">The second-to-last key's value (one component).</param>
    /// <param name="last">The last key's value (the same component).</param>
    /// <param name="tension">The last key's tension (its first stored TBC float).</param>
    /// <param name="continuity">The last key's continuity (its second stored TBC float).</param>
    /// <param name="bias">The last key's bias (its third stored TBC float).</param>
    /// <returns>The normalized-segment incoming tangent.</returns>
    public static float EndIncoming(float previous, float last, float tension, float continuity, float bias)
    {
        var doubled = last * 2f;
        var mirrored = doubled - previous;
        return Evaluate(last, tension, continuity, bias, previous, mirrored, 1f, 1f).Incoming;
    }

    /// <summary>
    ///     RE-19's Dv, the engine's CalculateDVals for one component, in Float32 with the rule's operation order:
    ///     h = (1 - T) * 0.5; incoming = ((1+C)h(1-B))fwd + ((1-C)h(1+B))bwd; outgoing = ((1-C)h(1-B))fwd + ((1+C)h(1+B))bwd;
    ///     each then times (2 / (pre + next)) times its own interval length.
    /// </summary>
    /// <param name="value">The key's value.</param>
    /// <param name="tension">The key's tension.</param>
    /// <param name="continuity">The key's continuity.</param>
    /// <param name="bias">The key's bias.</param>
    /// <param name="previous">The previous neighbour's value (sub1).</param>
    /// <param name="next">The next neighbour's value (plus1).</param>
    /// <param name="previousLength">The previous interval length (pre).</param>
    /// <param name="nextLength">The next interval length (next).</param>
    /// <returns>The key's incoming (DS) and outgoing (DD) tangents.</returns>
    public static (float Incoming, float Outgoing) Evaluate(
        float value,
        float tension,
        float continuity,
        float bias,
        float previous,
        float next,
        float previousLength,
        float nextLength)
    {
        var backward = value - previous;
        var forward = next - value;
        var onePlusBias = bias + 1f;
        var oneMinusBias = 1f - bias;
        var oneMinusTension = 1f - tension;
        var half = oneMinusTension * 0.5f;
        var onePlusContinuity = continuity + 1f;
        var oneMinusContinuity = 1f - continuity;
        var plusHalf = onePlusContinuity * half;
        var minusHalf = oneMinusContinuity * half;
        var incomingNextWeight = plusHalf * oneMinusBias;
        var incomingNext = incomingNextWeight * forward;
        var incomingPreviousWeight = minusHalf * onePlusBias;
        var incomingPrevious = incomingPreviousWeight * backward;
        var incoming = incomingNext + incomingPrevious;
        var outgoingNextWeight = minusHalf * oneMinusBias;
        var outgoingNext = outgoingNextWeight * forward;
        var outgoingPreviousWeight = plusHalf * onePlusBias;
        var outgoingPrevious = outgoingPreviousWeight * backward;
        var outgoing = outgoingNext + outgoingPrevious;
        var lengthSum = previousLength + nextLength;
        var scale = 2f / lengthSum;
        var incomingScale = scale * previousLength;
        var outgoingScale = scale * nextLength;
        return (incoming * incomingScale, outgoing * outgoingScale);
    }

    /// <summary>A float built from its stored bits.</summary>
    /// <param name="bits">The raw bits.</param>
    /// <returns>The float.</returns>
    private static float Float(uint bits)
    {
        return BitConverter.UInt32BitsToSingle(bits);
    }
}
