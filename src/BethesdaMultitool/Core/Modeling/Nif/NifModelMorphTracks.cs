using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     The morph tracks of one target geometry in one clip, assembled by <see cref="Assemble" /> from the mapped slots
///     (cut-1b slice 7, plan section 1.8 with the SA5 contract): ONE whole-vector <see cref="SceneMorphTrack" /> when every
///     non-Base target is keyed by ordinary keys with identical time bits and one key type (and, for TBC, identical
///     parameter bits, since the vector form names one parameter set per key), the same source priority, and the target
///     count equals the morph data's; otherwise one <see cref="SceneMorphTargetTrack" /> per typed target, each with its
///     own times and interpolation. A node never carries both forms in one clip.
/// </summary>
internal sealed class NifModelMorphTracks
{
    private readonly float[]? _times;
    private readonly float[]? _weights;
    private readonly SceneInterpolation _interpolation;
    private readonly SceneTbcParameters[]? _tbcParameters;
    private readonly SceneTbcEndpointTangents? _tbcEndpoints;
    private readonly SceneAnimationTrackSourcePolicy? _vectorPolicy;
    private readonly IReadOnlyList<SceneAnimationTrackSourcePolicy?> _policies;

    private NifModelMorphTracks(
        int targetCount,
        IReadOnlyList<NifModelMorphTargetResult> typed,
        IReadOnlyList<SceneAnimationTrackSourcePolicy?> policies,
        float[]? times,
        float[]? weights,
        SceneInterpolation interpolation,
        SceneTbcParameters[]? tbcParameters,
        SceneTbcEndpointTangents? tbcEndpoints,
        SceneAnimationTrackSourcePolicy? vectorPolicy)
    {
        TargetCount = targetCount;
        Typed = typed;
        _policies = policies;
        _times = times;
        _weights = weights;
        _interpolation = interpolation;
        _tbcParameters = tbcParameters;
        _tbcEndpoints = tbcEndpoints;
        _vectorPolicy = vectorPolicy;
    }

    /// <summary>The morph data's target count (its morph count minus the Base).</summary>
    public int TargetCount { get; }

    /// <summary>The typed slots, in morph order.</summary>
    public IReadOnlyList<NifModelMorphTargetResult> Typed { get; }

    /// <summary>True when the slots merge into one whole-vector track.</summary>
    public bool IsVector => _times is not null;

    /// <summary>True when at least one track is emitted.</summary>
    public bool HasTracks => Typed.Count > 0;

    /// <summary>
    ///     Decides the form and prepares the merged vector when the slots fit it. The whole-vector merge interleaves the
    ///     targets' key values key-major (for Hermite, each key's incoming tangents, values, then outgoing tangents) and
    ///     concatenates their TBC endpoint tangents; nothing is resampled.
    /// </summary>
    /// <param name="morphCount">The morph data's morph count (the Base included).</param>
    /// <param name="results">The mapped non-Base slots, native ones included, each morph index at most once.</param>
    /// <param name="policies">One source policy per result, index for index (null entries allowed).</param>
    /// <returns>The assembled tracks.</returns>
    /// <exception cref="ArgumentException">A morph index is the Base, repeats, or exceeds the morph count; the lists differ in length.</exception>
    public static NifModelMorphTracks Assemble(int morphCount, IReadOnlyList<NifModelMorphTargetResult> results,
        IReadOnlyList<SceneAnimationTrackSourcePolicy?> policies)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(policies);
        if (results.Count != policies.Count)
        {
            throw new ArgumentException("Every slot needs exactly one source policy entry.", nameof(policies));
        }

        var seen = new HashSet<int>();
        foreach (var result in results)
        {
            if (result.MorphIndex < 1 || result.MorphIndex >= morphCount || !seen.Add(result.MorphIndex))
            {
                throw new ArgumentException(
                    $"Morph {result.MorphIndex} is the Base, repeats, or exceeds the {morphCount} morphs.",
                    nameof(results));
            }
        }

        var order = Enumerable.Range(0, results.Count).OrderBy(index => results[index].MorphIndex).ToArray();
        var typed = new List<NifModelMorphTargetResult>();
        var typedPolicies = new List<SceneAnimationTrackSourcePolicy?>();
        foreach (var index in order)
        {
            if (results[index].IsTyped)
            {
                typed.Add(results[index]);
                typedPolicies.Add(policies[index]);
            }
        }

        var targetCount = morphCount - 1;
        if (typed.Count == results.Count && typed.Count == targetCount && targetCount > 0 &&
            TryMergeVector(typed, typedPolicies, out var times, out var weights, out var interpolation,
                out var tbcParameters, out var tbcEndpoints))
        {
            return new NifModelMorphTracks(targetCount, typed.AsReadOnly(), typedPolicies.AsReadOnly(), times, weights,
                interpolation, tbcParameters, tbcEndpoints, typedPolicies[0]);
        }

        return new NifModelMorphTracks(targetCount, typed.AsReadOnly(), typedPolicies.AsReadOnly(), null, null,
            SceneInterpolation.Linear, null, null, null);
    }

    /// <summary>The whole-vector track on one node.</summary>
    /// <param name="nodeIndex">The exact document node.</param>
    /// <param name="clock">The track clock (a free-running controller), or null (a sequence).</param>
    /// <returns>The track.</returns>
    /// <exception cref="InvalidOperationException">The slots did not merge (<see cref="IsVector" /> is false).</exception>
    public SceneMorphTrack VectorTrack(int nodeIndex, SceneAnimationClock? clock)
    {
        if (_times is null || _weights is null)
        {
            throw new InvalidOperationException("The morph slots did not merge into a whole-vector track.");
        }

        return new SceneMorphTrack(nodeIndex, TargetCount, _times, _weights, _interpolation, clock, _tbcParameters,
            _tbcEndpoints, _vectorPolicy);
    }

    /// <summary>One per-target track per typed slot on one node, in morph order.</summary>
    /// <param name="nodeIndex">The exact document node.</param>
    /// <param name="clock">The track clock (a free-running controller), or null (a sequence).</param>
    /// <returns>The tracks; empty when no slot typed.</returns>
    public IReadOnlyList<SceneMorphTargetTrack> TargetTracks(int nodeIndex, SceneAnimationClock? clock)
    {
        var tracks = new SceneMorphTargetTrack[Typed.Count];
        for (var index = 0; index < tracks.Length; index++)
        {
            var slot = Typed[index];
            tracks[index] = new SceneMorphTargetTrack(nodeIndex, slot.TargetIndex, slot.Curve!, clock,
                _policies[index]);
        }

        return tracks;
    }

    /// <summary>The whole-vector merge rule: ordinary keys, one key type, identical time bits, TBC bits and priorities.</summary>
    private static bool TryMergeVector(
        IReadOnlyList<NifModelMorphTargetResult> typed,
        IReadOnlyList<SceneAnimationTrackSourcePolicy?> policies,
        out float[] times,
        out float[] weights,
        out SceneInterpolation interpolation,
        out SceneTbcParameters[]? tbcParameters,
        out SceneTbcEndpointTangents? tbcEndpoints)
    {
        times = [];
        weights = [];
        interpolation = SceneInterpolation.Linear;
        tbcParameters = null;
        tbcEndpoints = null;
        if (typed[0].KeyCurve is not { } first)
        {
            return false;
        }

        var priority = policies[0]?.Priority;
        for (var index = 0; index < typed.Count; index++)
        {
            if (typed[index].KeyCurve is not { } curve || curve.KeyType != first.KeyType ||
                curve.ComponentCount != 1 || curve.Interpolation != first.Interpolation ||
                curve.Times.Length != first.Times.Length || policies[index]?.Priority != priority)
            {
                return false;
            }

            for (var key = 0; key < curve.Times.Length; key++)
            {
                if (BitConverter.SingleToUInt32Bits(curve.Times[key]) !=
                    BitConverter.SingleToUInt32Bits(first.Times[key]))
                {
                    return false;
                }
            }

            if (first.Interpolation == SceneInterpolation.Tbc && !SameTbc(first, curve))
            {
                return false;
            }
        }

        var targets = typed.Count;
        var keyCount = first.Times.Length;
        interpolation = first.Interpolation;
        times = (float[])first.Times.Clone();
        var cubic = interpolation is SceneInterpolation.Hermite or SceneInterpolation.CubicSpline;
        var parts = cubic ? 3 : 1;
        weights = new float[keyCount * parts * targets];
        for (var key = 0; key < keyCount; key++)
        {
            for (var part = 0; part < parts; part++)
            {
                for (var target = 0; target < targets; target++)
                {
                    weights[(key * parts + part) * targets + target] = typed[target].KeyCurve!.Values[key * parts + part];
                }
            }
        }

        if (interpolation == SceneInterpolation.Tbc)
        {
            tbcParameters = (SceneTbcParameters[])first.TbcParameters!.Clone();
            if (first.TbcEndpoints is not null)
            {
                var start = new float[targets];
                var end = new float[targets];
                for (var target = 0; target < targets; target++)
                {
                    var endpoints = typed[target].KeyCurve!.TbcEndpoints!;
                    start[target] = endpoints.StartOutgoing[0];
                    end[target] = endpoints.EndIncoming[0];
                }

                tbcEndpoints = new SceneTbcEndpointTangents(start, end);
            }
        }

        return true;
    }

    /// <summary>True when two TBC curves store bit-identical parameters and endpoint presence.</summary>
    private static bool SameTbc(NifModelCurve first, NifModelCurve other)
    {
        if (first.TbcParameters is null || other.TbcParameters is null ||
            first.TbcParameters.Length != other.TbcParameters.Length ||
            (first.TbcEndpoints is null) != (other.TbcEndpoints is null))
        {
            return false;
        }

        for (var key = 0; key < first.TbcParameters.Length; key++)
        {
            var a = first.TbcParameters[key];
            var b = other.TbcParameters[key];
            if (BitConverter.SingleToUInt32Bits(a.Tension) != BitConverter.SingleToUInt32Bits(b.Tension) ||
                BitConverter.SingleToUInt32Bits(a.Continuity) != BitConverter.SingleToUInt32Bits(b.Continuity) ||
                BitConverter.SingleToUInt32Bits(a.Bias) != BitConverter.SingleToUInt32Bits(b.Bias))
            {
                return false;
            }
        }

        return true;
    }
}
