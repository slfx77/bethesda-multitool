using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     The engine's orientation at each key of a quaternion key group, as <see cref="NifRotationKeyEngineRule" /> derives
///     it from the raw keys (RE-17): chain sign alignment, W clamp, exact normalization.
/// </summary>
/// <param name="Values">One unit quaternion per key, in key order, as System.Numerics X, Y, Z, W.</param>
/// <param name="ChainNegated">
///     Per key, true when the chain alignment negated it (key 0 never is). A chain, not a pairwise test: once a key is
///     negated, every later key is compared against the negated one.
/// </param>
internal sealed record NifEngineRotationKeys(Quaternion[] Values, bool[] ChainNegated)
{
    /// <summary>The number of keys the chain alignment negated.</summary>
    public int NegatedCount => ChainNegated.Count(static negated => negated);
}
