using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     Bucket B, the rotation-path hops of cut-1b slice 9 (plan section 3; RE-17, RE-24): the 170-degree control and the
///     RE-17 shortest-path control, measured on the reader's documents through Shared's public
///     <see cref="ScenePoseEvaluator" /> (<see cref="NifAnimationPoseSampler" />).
/// </summary>
/// <remarks>
///     <para>
///         170 degrees: FNV PC <c>meshes/creatures/queenant/idleanims/specialidle_ antenna.kf</c> (block 17, keys 10 and
///         11, 174.5 degrees, bound in <c>queenant/skeleton.nif</c>), its <c>.nif</c> twin FNV X360
///         <c>meshes/armor/headgear/slavehats/nvslave_02_go.nif</c> (the <c>(controllers)</c> clip, block 161, keys 12
///         and 13, 174.276 degrees) and a synthetic 170-degree pair. The document's rotation between the two keys,
///         sampled at 33 points, must follow the engine's counter-warped nlerp (RE-17: the Float32 counter-warp of the
///         segment fraction, then normalization) within <see cref="RotationToleranceDegrees" />. The comparator detects
///         nlerp: Shared's plain slerp and plain nlerp of the pair differ by more than 6 degrees (the plan measured 6.76,
///         7.33 and 7.36 at 170, 174.276 and 174.5 degrees; the receipt records the measured figure beside each), and
///         both a plain slerp and a plain nlerp evaluation, compared with the samples the same way, miss by at least 100
///         times the tolerance.
///     </para>
///     <para>
///         RE-17 shortest path: FNV PC <c>meshes/creatures/protectron/h2hrecoil.kf</c> (block 15, keys 27 and 28, raw
///         dot -0.99998) and <c>meshes/creatures/nvmantis/idleanims/specialidle_hitarmleft.kf</c> (block 20, keys 0 and
///         1, raw dot -0.869). The typed keys are chain-aligned, so the rotation the evaluator traverses over the
///         segment (accumulated over an adaptive subdivision until adjacent samples are under 5 degrees apart) is the
///         pair's short angle, 0.743 and 59.3 degrees, not 359 and 301. Control: the flip disabled (the two raw keys,
///         each exactly normalized but not chain-aligned, as a two-key track) traverses the long way.
///     </para>
///     <para>Receipt: <c>TestOutput/cut1b-slice9-&lt;date&gt;/NifAnimationRotationPathOracleTests/</c>.</para>
/// </remarks>
[Trait("Category", TestCategories.BucketB)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class NifAnimationRotationPathOracleTests
{
    /// <summary>The bound between Shared's samples and the engine's counter-warped nlerp, in degrees.</summary>
    public const double RotationToleranceDegrees = 1e-3;

    private const string Hop = nameof(NifAnimationRotationPathOracleTests);
    private const double ControlFactor = 100;
    private const double DetectsNlerpDegrees = 6;
    private const int SegmentSamples = 32;
    private const double TraversalStepDegrees = 5;
    private const string KfControl = "170 degrees, unit, clip types whole";
    private const string NifControl = "170 degrees, unit, `.nif`";
    private const string RecoilControl = "dot < 0, unit, clip types whole";
    private const string MantisControl = "dot < 0, moderate";

    [Fact]
    public void OneHundredSeventyDegreePair_Kf_FollowsTheEngineCounterWarpedNlerp()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        AssertLargeAnglePair(KfControl, 17, 10, 174.5, 7.36);
    }

    [Fact]
    public void OneHundredSeventyDegreePair_Nif_FollowsTheEngineCounterWarpedNlerp()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        AssertLargeAnglePair(NifControl, 161, 12, 174.276, 7.33);
    }

    [Fact]
    public void OneHundredSeventyDegreePair_Synthetic_FollowsTheEngineCounterWarpedNlerp()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var axis = Vector3.Normalize(new Vector3(1, 2, 3));
        var half = 85.0 * Math.PI / 180.0;
        var first = new[] { 0f, 0f, 0f, 1f };
        var second = new[]
        {
            (float)(axis.X * Math.Sin(half)), (float)(axis.Y * Math.Sin(half)), (float)(axis.Z * Math.Sin(half)),
            (float)Math.Cos(half)
        };
        var track = new SceneTransformTrack(0, SceneTransformProperty.Rotation, [0f, 1f],
            first.Concat(second).ToArray(), SceneInterpolation.GamebryoCounterWarpedNlerp);

        var outcome = MeasurePair(SyntheticDocument(track), 0, track, 0, IdentityRest, null);

        Record("synthetic 170-degree pair", null, outcome, 170, 6.76);
        AssertPair(outcome, "synthetic");
        Assert.True(outcome.SlerpVersusNlerp >= 6.76 - 0.005,
            $"slerp and nlerp differ by {outcome.SlerpVersusNlerp} degrees at 170 degrees (plan: 6.76)");
    }

    [Fact]
    public void ShortestPath_H2hRecoil_TraversesTheShortAngle_AndTheTypedKeyIsTheFlippedRawKey()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        AssertShortestPath(RecoilControl, 15, 27, 0.743, evaluatorShowsTheLongWay: false);
    }

    [Fact]
    public void ShortestPath_SpecialIdleHitArmLeft_TraversesTheShortAngle_AndTheUnflippedPairTheLongWay()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        AssertShortestPath(MantisControl, 20, 0, 59.3, evaluatorShowsTheLongWay: true);
    }

    private static SceneTrs IdentityRest => new(Vector3.Zero, Quaternion.Identity, Vector3.One);

    private static void AssertLargeAnglePair(string control, int dataBlock, int key, double planDegrees,
        double planSlerpVersusNlerp)
    {
        var read = Cut1bAnimationStage.LoadControl(control);
        var (clip, track) = FindRotationTrack(read, dataBlock);
        Assert.Equal(SceneInterpolation.GamebryoCounterWarpedNlerp, track.Interpolation);
        var rest = read.Graph.Nodes[track.NodeIndex].LocalTrs;
        Assert.True(rest is not null, $"{read}: the target node has no authored TRS.");

        var outcome = MeasurePair(NifAnimationPoseSampler.Isolated(read, clip, track), track.NodeIndex, track, key,
            rest.Value, clip.Clock);

        Record(control, read, outcome, planDegrees, planSlerpVersusNlerp);
        Assert.True(Math.Abs(outcome.PairDegrees - planDegrees) < 0.05,
            $"{read}: keys {key} and {key + 1} are {outcome.PairDegrees} degrees apart, the plan says {planDegrees}.");
        AssertPair(outcome, read.ToString());
    }

    private static void AssertPair(NifAnimationPairOutcome outcome, string subject)
    {
        Assert.True(outcome.EngineWorst <= RotationToleranceDegrees,
            $"{subject}: the samples miss the engine's counter-warped nlerp by up to {outcome.EngineWorst} degrees.");
        Assert.True(outcome.SlerpWorst >= ControlFactor * RotationToleranceDegrees,
            $"{subject}: a plain slerp evaluation misses by only {outcome.SlerpWorst} degrees.");
        Assert.True(outcome.NlerpWorst >= ControlFactor * RotationToleranceDegrees,
            $"{subject}: a plain nlerp evaluation misses by only {outcome.NlerpWorst} degrees.");
        Assert.True(outcome.SlerpVersusNlerp > DetectsNlerpDegrees,
            $"{subject}: slerp and nlerp differ by only {outcome.SlerpVersusNlerp} degrees.");
    }

    /// <summary>
    ///     The typed pair is chain-aligned and the evaluator turns the plan's short angle across it. The control is the
    ///     unflipped raw pair. Where the raw keys are far from antipodal (the mantis pair, dot -0.869) the evaluator must
    ///     take the long way over it. Where they are nearly antipodal (h2hrecoil, dot -0.99998) the unflipped chord passes
    ///     next to the zero quaternion, which the engine-model FastNormalize cannot renormalize, so the evaluator shows no
    ///     sweep there (measured 0.7426 degrees, the same as the aligned pair): the evaluator cannot discriminate that pair,
    ///     and the control is key-level instead: the typed second key must be the negated raw key, which an unflipped read
    ///     fails. Both readings are recorded either way.
    /// </summary>
    private static void AssertShortestPath(string control, int dataBlock, int key, double planDegrees,
        bool evaluatorShowsTheLongWay)
    {
        var read = Cut1bAnimationStage.LoadControl(control);
        var (clip, track) = FindRotationTrack(read, dataBlock);
        Assert.Equal(SceneInterpolation.GamebryoCounterWarpedNlerp, track.Interpolation);
        var rest = read.Graph.Nodes[track.NodeIndex].LocalTrs;
        Assert.True(rest is not null, $"{read}: the target node has no authored TRS.");
        var first = Key(track, key);
        var second = Key(track, key + 1);
        var alignedDot = Dot(first, second);
        var raw = RawKeys(read, dataBlock);
        var rawDot = Dot(raw[key], raw[key + 1]);

        var document = NifAnimationPoseSampler.Isolated(read, clip, track);
        var typed = Traverse(document, track.NodeIndex, rest.Value, clip.Clock, track.Clock, track.Times[key],
            track.Times[key + 1]);

        var rawFirst = NifAnimationEngineRules.NormalizeExactXyzw(raw[key]);
        var rawSecond = NifAnimationEngineRules.NormalizeExactXyzw(raw[key + 1]);
        var unflipped = new SceneTransformTrack(0, SceneTransformProperty.Rotation, [0f, 1f],
            rawFirst.Concat(rawSecond).ToArray(), SceneInterpolation.GamebryoCounterWarpedNlerp);
        var unflippedDegrees = Traverse(SyntheticDocument(unflipped), 0, IdentityRest, null, null, 0f, 1f);

        Cut1bHopReceipt.Control(Hop, $"flipDisabled ({control})", new JsonObject
        {
            ["entry"] = read.File.Entry,
            ["sha256"] = read.File.Sha256,
            ["dataBlock"] = dataBlock,
            ["keys"] = new JsonArray(key, key + 1),
            ["rawDot"] = rawDot,
            ["alignedDot"] = alignedDot,
            ["planShortDegrees"] = planDegrees,
            ["typedTraversedDegrees"] = typed,
            ["unflippedTraversedDegrees"] = unflippedDegrees,
            ["typedSecondDotNegatedRaw"] = -Dot(second, rawSecond),
            ["evaluatorShowsTheLongWay"] = evaluatorShowsTheLongWay,
            ["detected"] = evaluatorShowsTheLongWay ? unflippedDegrees >= 180 : -Dot(second, rawSecond) > 0.9999999
        });
        Assert.True(rawDot < 0, $"{read}: the raw keys' dot is {rawDot}, so the pair shows no flip.");
        Assert.True(alignedDot >= 0, $"{read}: the typed keys' dot is {alignedDot}: not chain-aligned.");
        Assert.True(Math.Abs(typed - planDegrees) < 0.05,
            $"{read}: the evaluator traverses {typed} degrees over keys {key} and {key + 1}, the plan's short " +
            $"angle is {planDegrees}.");
        if (evaluatorShowsTheLongWay)
        {
            Assert.True(unflippedDegrees >= 180,
                $"{read}: with the flip disabled the evaluator traverses only {unflippedDegrees} degrees, not the long way.");
        }
        else
        {
            Assert.True(-Dot(second, rawSecond) > 0.9999999,
                $"{read}: the typed key {key + 1} is not the negated raw key (dot with the negated raw key {-Dot(second, rawSecond)}).");
        }
    }

    /// <summary>
    ///     Samples the segment between keys <paramref name="key" /> and key + 1 at 33 points through the evaluator and
    ///     measures the samples against the engine's counter-warped nlerp, a plain slerp and a plain nlerp of the keys.
    /// </summary>
    private static NifAnimationPairOutcome MeasurePair(ModelDocument document, int node, SceneTransformTrack track, int key,
        SceneTrs rest, SceneAnimationClock? clipClock)
    {
        var first = Key(track, key);
        var second = Key(track, key + 1);
        var t0 = track.Times[key];
        var t1 = track.Times[key + 1];
        var absolute = new List<float>();
        var mapped = new List<float>();
        for (var step = 0; step <= SegmentSamples; step++)
        {
            var local = (float)(t0 + ((double)t1 - t0) * step / SegmentSamples);
            var time = NifAnimationPoseSampler.Absolute(local, clipClock, track.Clock);
            absolute.Add(time);
            mapped.Add(NifAnimationPoseSampler.Mapped(time, clipClock, track.Clock));
        }

        var matrices = NifAnimationPoseSampler.Sample(document, node, absolute);
        double engineWorst = 0, slerpWorst = 0, nlerpWorst = 0;
        var wFirst = new[] { first[3], first[0], first[1], first[2] };
        var wSecond = new[] { second[3], second[0], second[1], second[2] };
        for (var index = 0; index < matrices.Count; index++)
        {
            var observed = NifAnimationPoseSampler.RotationRows(matrices[index], rest);
            Assert.NotNull(observed);
            var amount = (mapped[index] - t0) / (t1 - t0);
            var engine = NifAnimationEngineRules.CounterWarpedComponents(wFirst, wSecond, amount);
            engineWorst = Math.Max(engineWorst, NifAnimationPoseSampler.AngleDegrees(
                Rows(engine[1], engine[2], engine[3], engine[0]), observed));
            slerpWorst = Math.Max(slerpWorst, NifAnimationPoseSampler.AngleDegrees(Slerp(first, second, amount),
                observed));
            nlerpWorst = Math.Max(nlerpWorst, NifAnimationPoseSampler.AngleDegrees(Nlerp(first, second, amount),
                observed));
        }

        double slerpVersusNlerp = 0;
        for (var step = 0; step <= 1024; step++)
        {
            var amount = step / 1024.0;
            slerpVersusNlerp = Math.Max(slerpVersusNlerp, NifAnimationPoseSampler.AngleDegrees(
                Slerp(first, second, amount), Nlerp(first, second, amount)));
        }

        var pair = 2.0 * Math.Acos(Math.Min(1.0, Math.Abs(Dot(first, second)) / (Norm(first) * Norm(second)))) *
                   180.0 / Math.PI;
        return new NifAnimationPairOutcome(pair, matrices.Count, engineWorst, slerpWorst, nlerpWorst, slerpVersusNlerp);
    }

    /// <summary>
    ///     The rotation the evaluator traverses between two track-local times: the sum of the angles between adjacent
    ///     samples, each interval bisected until its two samples are under <see cref="TraversalStepDegrees" /> apart.
    /// </summary>
    private static double Traverse(ModelDocument document, int node, SceneTrs rest, SceneAnimationClock? clipClock,
        SceneAnimationClock? trackClock, float from, float to)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var evaluator = new ScenePoseEvaluator(document, cancellationToken);
        var pose = evaluator.CreateWorkspace(0, cancellationToken);

        double[][] RowsAt(float local)
        {
            var time = NifAnimationPoseSampler.Absolute(local, clipClock, trackClock);
            evaluator.EvaluateClip(pose, 0, time, cancellationToken);
            var rows = NifAnimationPoseSampler.RotationRows(pose.LocalMatrices.Span[node], rest);
            Assert.NotNull(rows);
            return rows;
        }

        var total = 0.0;
        var pending = new Stack<(float From, double[][] FromRows, float To, double[][] ToRows, int Depth)>();
        pending.Push((from, RowsAt(from), to, RowsAt(to), 0));
        while (pending.TryPop(out var interval))
        {
            var angle = NifAnimationPoseSampler.AngleDegrees(interval.FromRows, interval.ToRows);
            var middle = (float)(((double)interval.From + interval.To) * 0.5);
            if (angle < TraversalStepDegrees || interval.Depth >= 40 || middle <= interval.From ||
                middle >= interval.To)
            {
                total += angle;
                continue;
            }

            var middleRows = RowsAt(middle);
            pending.Push((interval.From, interval.FromRows, middle, middleRows, interval.Depth + 1));
            pending.Push((middle, middleRows, interval.To, interval.ToRows, interval.Depth + 1));
        }

        return total;
    }

    /// <summary>The reader's rotation track whose interpolator's data is the given block (the probe's facts name it).</summary>
    private static (SceneAnimation Clip, SceneTransformTrack Track) FindRotationTrack(Cut1bAnimationRead read,
        int dataBlock)
    {
        var facts = NifAnimationExpectations.Facts(read.Expectation);
        foreach (var clip in read.Result.Clips)
        {
            var entries = NifAnimationDocumentOracle.Extras(clip)["tracks"]?.AsArray() ?? new JsonArray();
            for (var index = 0; index < entries.Count && index < clip.TransformTracks.Count; index++)
            {
                var interpolator = entries[index]!["interpolator"]!.GetValue<int>();
                if (clip.TransformTracks[index].Property == SceneTransformProperty.Rotation &&
                    facts.TryGetValue(interpolator, out var fact) && fact["data"]?.GetValue<int>() == dataBlock)
                {
                    return (clip, clip.TransformTracks[index]);
                }
            }
        }

        Assert.Fail($"{read}: no typed rotation track reads data block {dataBlock}.");
        throw new InvalidOperationException("Unreachable.");
    }

    /// <summary>The probe's raw rotation keys of a data block (W, X, Y, Z floats, file order).</summary>
    private static List<float[]> RawKeys(Cut1bAnimationRead read, int dataBlock)
    {
        var payloads = NifAnimationExpectations.Payloads(read.Expectation);
        Assert.True(payloads.TryGetValue(dataBlock, out var payload), $"{read}: no probe payload for {dataBlock}.");
        return payload["rotation"]!["keys"]!.AsArray()
            .Select(static key => NifAnimationExpectations.BitsArray(key!["valueBits"])
                .Select(BitConverter.UInt32BitsToSingle).ToArray())
            .ToList();
    }

    private static ModelDocument SyntheticDocument(SceneTransformTrack track)
    {
        return new ModelDocument("nif", "synthetic", [new SceneDefinition("scene", [0])],
            [new SceneNode("node", IdentityRest)], [],
            animations: [new SceneAnimation("clip", [], null, [track])]);
    }

    private static void Record(string control, Cut1bAnimationRead? read, NifAnimationPairOutcome outcome, double planDegrees,
        double planSlerpVersusNlerp)
    {
        Cut1bHopReceipt.Control(Hop, control, new JsonObject
        {
            ["entry"] = read?.File.Entry,
            ["sha256"] = read?.File.Sha256,
            ["pairDegrees"] = outcome.PairDegrees,
            ["planPairDegrees"] = planDegrees,
            ["samples"] = outcome.Samples,
            ["toleranceDegrees"] = RotationToleranceDegrees,
            ["engineWorstDegrees"] = outcome.EngineWorst,
            ["plainSlerpWorstDegrees"] = outcome.SlerpWorst,
            ["plainNlerpWorstDegrees"] = outcome.NlerpWorst,
            ["slerpVersusNlerpDegrees"] = outcome.SlerpVersusNlerp,
            ["planSlerpVersusNlerpDegrees"] = planSlerpVersusNlerp,
            ["detected"] = outcome.SlerpWorst >= ControlFactor * RotationToleranceDegrees &&
                           outcome.NlerpWorst >= ControlFactor * RotationToleranceDegrees &&
                           outcome.SlerpVersusNlerp > DetectsNlerpDegrees,
            ["note"] = string.Create(CultureInfo.InvariantCulture,
                $"engine within {outcome.EngineWorst:G4} degrees; slerp misses by {outcome.SlerpWorst:G4}, nlerp by " +
                $"{outcome.NlerpWorst:G4}")
        });
    }

    /// <summary>Key <paramref name="key" /> of a rotation track (X, Y, Z, W).</summary>
    private static float[] Key(SceneTransformTrack track, int key)
    {
        return [track.Values[key * 4], track.Values[key * 4 + 1], track.Values[key * 4 + 2], track.Values[key * 4 + 3]];
    }

    private static double Dot(float[] a, float[] b)
    {
        return (double)a[0] * b[0] + (double)a[1] * b[1] + (double)a[2] * b[2] + (double)a[3] * b[3];
    }

    private static double Norm(float[] q)
    {
        return Math.Sqrt(Dot(q, q));
    }

    /// <summary>A plain slerp (double) of two X, Y, Z, W keys, as rows.</summary>
    private static double[][] Slerp(float[] first, float[] second, double amount)
    {
        var a = Normalized(first);
        var b = Normalized(second);
        var dot = Math.Clamp(a[0] * b[0] + a[1] * b[1] + a[2] * b[2] + a[3] * b[3], -1.0, 1.0);
        if (dot < 0)
        {
            b = b.Select(static c => -c).ToArray();
            dot = -dot;
        }

        var omega = Math.Acos(dot);
        var sine = Math.Sin(omega);
        var wa = sine < 1e-12 ? 1 - amount : Math.Sin((1 - amount) * omega) / sine;
        var wb = sine < 1e-12 ? amount : Math.Sin(amount * omega) / sine;
        return Rows(wa * a[0] + wb * b[0], wa * a[1] + wb * b[1], wa * a[2] + wb * b[2], wa * a[3] + wb * b[3]);
    }

    /// <summary>A plain nlerp (double) of two X, Y, Z, W keys, as rows.</summary>
    private static double[][] Nlerp(float[] first, float[] second, double amount)
    {
        var a = Normalized(first);
        var b = Normalized(second);
        return Rows(a[0] + amount * (b[0] - a[0]), a[1] + amount * (b[1] - a[1]), a[2] + amount * (b[2] - a[2]),
            a[3] + amount * (b[3] - a[3]));
    }

    private static double[] Normalized(float[] q)
    {
        var norm = Norm(q);
        return [q[0] / norm, q[1] / norm, q[2] / norm, q[3] / norm];
    }

    /// <summary>The rows of an X, Y, Z, W quaternion after a double normalization.</summary>
    private static double[][] Rows(double x, double y, double z, double w)
    {
        var norm = Math.Sqrt(x * x + y * y + z * z + w * w);
        return NifAnimationPoseSampler.RowsOf(x / norm, y / norm, z / norm, w / norm);
    }

}
