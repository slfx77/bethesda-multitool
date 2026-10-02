using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelAnimationReaderTestSupport;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelAnimationTargetTestSupport;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelAnimationTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     Cut-1b slice 13, the XYZ-Euler rotation (<see cref="NifModelCurveMapping.MapEulerRotation" />,
///     <see cref="NifModelEulerRotation" /> and the reader; RE-20 of docs/formats/nif-animation-engine-behavior-20260925.md):
///     the composition against a test-side implementation of RE-20 rules 7 and 9 sampled through Shared's public
///     <see cref="ScenePoseEvaluator" />, the per-axis rules 3 and 4, the fail-closed guards of rule 2 and the clock guard
///     of rule 7, each with a control that fails. Fixtures are written field by field in both byte orders.
/// </summary>
public sealed class NifModelEulerRotationTests
{
    private const uint Linear = 1;
    private const uint XyzEuler = 4;
    private const float Tolerance = 1e-6f;

    /// <summary>
    ///     Three keyed LINEAR axes with their own times (Y ends at 0.8, so rule 6 holds it afterwards), read through the
    ///     reader into a clip over [0, 1] and sampled at six times: Shared's pose equals the RE-20 evaluation (rule 7 per
    ///     axis, then the rule 9 W/X/Y/Z formula) within 1e-6, and the node gets no quaternion Rotation track. Control: the
    ///     qX * qY * qZ order is more than 1e-3 away at every sample.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Composition_MatchesRe20Rule9_ThroughTheEvaluator(bool bigEndian)
    {
        (float Time, float Angle)[] x = [(0f, 0.2f), (1f, 0.9f)];
        (float Time, float Angle)[] y = [(0f, -0.4f), (0.4f, 0.3f), (0.8f, -0.1f)];
        (float Time, float Angle)[] z = [(0f, 1.1f), (1f, -0.6f)];
        var (state, graph) = ReadGraph(SequenceFixture(bigEndian,
            EulerAxes(Axis(Linear, x), Axis(Linear, y), Axis(Linear, z)), 0f, 1f));

        var result = NifModelAnimationReader.ReadNif(state, graph, TestContext.Current.CancellationToken);

        var clip = Assert.Single(result.Clips);
        var track = Assert.Single(clip.EulerRotationTracks);
        Assert.Equal(1, track.NodeIndex);
        Assert.Equal(SceneEulerOrder.Xyz, track.Order);
        Assert.DoesNotContain(clip.TransformTracks, static t => t.Property == SceneTransformProperty.Rotation);
        Assert.Equal(SceneAnimationChannelState.Keyed,
            Assert.Single(clip.TransformTracks, static t => t.Property == SceneTransformProperty.Translation).State);
        var document = Assemble(graph, result.Clips);
        SceneValidation.ValidateStructure(document, TestContext.Current.CancellationToken);

        foreach (var time in new[] { 0f, 0.25f, 0.5f, 0.75f, 0.9f, 1f })
        {
            var actual = Quaternion.CreateFromRotationMatrix(NifModelAnimationReaderTestSupport.SampleLocal(document, 0, time, 1));
            var expected = Rule9(Rule7(x, time), Rule7(y, time), Rule7(z, time));
            AssertSameRotation(expected, actual, Tolerance, time);

            // Control: the other composition order.
            var control = Quaternion.CreateFromAxisAngle(Vector3.UnitX, (float)Rule7(x, time)) *
                          Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)Rule7(y, time)) *
                          Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)Rule7(z, time));
            Assert.True(Distance(control, actual) > 1e-3f, $"qX * qY * qZ coincides with the pose at t = {time}.");
        }
    }

    /// <summary>
    ///     Rule 3: an axis with no keys is a Constant curve at angle 0. Rule 4: an axis with one key holds it at every
    ///     time, before and after the key. Controls: a Y axis held at 0.5 instead of 0 changes the pose, and a two-key X
    ///     axis does not hold.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyAxis_IsConstantZero_AndOneKeyAxisHolds(bool bigEndian)
    {
        var mapped = NifModelCurveMapping.MapEulerRotation(
            EulerView(bigEndian, EulerAxes(Axis(Linear, (0.3f, 0.7f)), EmptyAxis, EmptyAxis)));

        Assert.False(mapped.IsBlocked);
        var euler = mapped.Euler!;
        Assert.Equal(1, euler.X.KeyCount);
        Assert.Equal(Linear, euler.X.KeyType);
        Assert.True(euler.Y.IsEmpty);
        Assert.Equal(0u, euler.Y.KeyType);
        Assert.True(euler.Z.IsEmpty);
        var y = euler.Y.ToCurve();
        Assert.Equal(SceneAnimationChannelState.Constant, y.State);
        Assert.Equal([0f], y.StaticValue);
        Assert.Empty(y.Times);
        var x = euler.X.ToCurve();
        Assert.Equal(SceneAnimationChannelState.Keyed, x.State);
        Assert.Equal([0.3f], x.Times);
        Assert.Equal([0.7f], x.Values);
        var document = EulerDocument(euler.ToTrack(0));
        SceneValidation.ValidateStructure(document, TestContext.Current.CancellationToken);
        var held = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.7f);
        foreach (var time in new[] { 0f, 0.3f, 2f })
        {
            AssertSameRotation(held, Quaternion.CreateFromRotationMatrix(NifModelAnimationTestSupport.SampleLocal(document, time)), Tolerance, time);
        }

        var tilted = new SceneEulerRotationTrack(0, SceneEulerOrder.Xyz, euler.X.ToCurve(),
            new SceneCurve(1, [], [], SceneInterpolation.Linear, SceneAnimationChannelState.Constant, [0.5f]),
            euler.Z.ToCurve());
        var tiltedPose = Quaternion.CreateFromRotationMatrix(NifModelAnimationTestSupport.SampleLocal(EulerDocument(tilted), 0f));
        Assert.True(Distance(held, tiltedPose) > 1e-3f, "A Y axis held at 0.5 must change the pose.");

        var moving = NifModelCurveMapping.MapEulerRotation(EulerView(bigEndian,
            EulerAxes(Axis(Linear, (0.3f, 0.7f), (1.3f, 1.7f)), EmptyAxis, EmptyAxis))).Euler!;
        var movingDocument = EulerDocument(moving.ToTrack(0));
        var atFirst = Quaternion.CreateFromRotationMatrix(NifModelAnimationTestSupport.SampleLocal(movingDocument, 0.3f));
        var later = Quaternion.CreateFromRotationMatrix(NifModelAnimationTestSupport.SampleLocal(movingDocument, 0.8f));
        Assert.True(Distance(atFirst, later) > 1e-3f, "A two-key axis must not hold its first key.");
    }

    /// <summary>
    ///     Rule 2: a stored record count other than 1 fails closed (the engine evaluates record 0 only), and an axis key
    ///     type outside LINEAR, QUADRATIC, TBC and CONST fails closed. Controls: one record maps, and the same axis with
    ///     key type LINEAR maps.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecordCountAndAxisKeyType_FailClosed(bool bigEndian)
    {
        var two = EulerView(bigEndian, EulerAxes(Axis(Linear, (0f, 0.2f)), EmptyAxis, EmptyAxis, 2));
        Assert.Equal(2u, two.EulerRecordCount);
        Assert.Equal(NifModelCurveBlock.EulerRecordCount, NifModelCurveMapping.MapEulerRotation(two).Block);
        var one = EulerView(bigEndian, EulerAxes(Axis(Linear, (0f, 0.2f)), EmptyAxis, EmptyAxis));
        Assert.False(NifModelCurveMapping.MapEulerRotation(one).IsBlocked);

        var bytes = new NifTestBlockWriter(bigEndian).F32s(0f, 1f).ToArray();
        var empty = new NifKeyGroupView(bytes, bigEndian, NifKeyValueLayout.Float, 0, 0, 0, 0, 0);
        var unknownAxis = new NifKeyGroupView(bytes, bigEndian, NifKeyValueLayout.Float, 0, 1, 7, 0, 8);
        var unknown = new NifRotationKeysView(0, 1, XyzEuler, default,
            new NifEulerRotationRecordView(0, false, 0, empty, unknownAxis, empty), bytes.Length);
        Assert.True(unknown.IsEuler);
        Assert.Equal(NifModelCurveBlock.EulerAxisKeyType, NifModelCurveMapping.MapEulerRotation(unknown).Block);

        var linearAxis = new NifKeyGroupView(bytes, bigEndian, NifKeyValueLayout.Float, 0, 1, Linear, 0, 8);
        var known = new NifRotationKeysView(0, 1, XyzEuler, default,
            new NifEulerRotationRecordView(0, false, 0, empty, linearAxis, empty), bytes.Length);
        var mapped = NifModelCurveMapping.MapEulerRotation(known);
        Assert.False(mapped.IsBlocked);
        Assert.Equal(1, mapped.Euler!.Y.KeyCount);
    }

    /// <summary>
    ///     Rule 7: a sequence whose clock starts at 0 while the X axis's two keys start at 0.5 could sample below the
    ///     first key, where the engine extrapolates and Shared holds, so the rotation channel stays native with the rule
    ///     7 reason on the interpolator and its data, the clip keeps translation and scale, and no Euler track exists.
    ///     Controls: a clock starting exactly at the first key maps (the retail case), and a free-running controller
    ///     with a phase but a start at the first key maps too, because a Shared clock maps into [start, stop].
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClockBelowTheFirstKey_KeepsTheRotationNative(bool bigEndian)
    {
        var late = Axis(Linear, (0.5f, 0.2f), (1f, 0.9f));
        var (state, graph) = ReadGraph(SequenceFixture(bigEndian, EulerAxes(late, EmptyAxis, EmptyAxis), 0f, 1f));

        var result = NifModelAnimationReader.ReadNif(state, graph, TestContext.Current.CancellationToken);

        var clip = Assert.Single(result.Clips);
        Assert.Empty(clip.EulerRotationTracks);
        Assert.Equal(new[] { SceneTransformProperty.Translation, SceneTransformProperty.Scale },
            clip.TransformTracks.Select(static track => track.Property));
        var reason = NifModelAnimationReasons.Reason(NifModelCurveBlock.EulerSampledBeforeFirstKey);
        Assert.Contains("RE-20 rule 7", reason, StringComparison.Ordinal);
        foreach (var block in new[] { 4, 5 })
        {
            var rotation = Assert.Single(DecisionsFor(result, block),
                static decision => decision.Property == SceneTransformProperty.Rotation);
            Assert.False(rotation.IsTyped);
            Assert.Equal(reason, rotation.Reason);
            Assert.Equal("eulerSampledBeforeFirstKey", rotation.Code);
            Assert.True(result.Dispositions[block].IsTyped);
        }

        SceneValidation.ValidateStructure(Assemble(graph, result.Clips), TestContext.Current.CancellationToken);

        var (okState, okGraph) = ReadGraph(SequenceFixture(bigEndian, EulerAxes(late, EmptyAxis, EmptyAxis), 0.5f, 1f));
        var ok = NifModelAnimationReader.ReadNif(okState, okGraph, TestContext.Current.CancellationToken);
        Assert.Single(Assert.Single(ok.Clips).EulerRotationTracks);
        Assert.DoesNotContain(ok.Decisions, static decision => !decision.IsTyped &&
            decision.Property == SceneTransformProperty.Rotation);

        var (freeState, freeGraph) = ReadGraph(ControllerFixture(bigEndian, EulerAxes(late, EmptyAxis, EmptyAxis),
            0.5f, 1f, 0.25f));
        var free = NifModelAnimationReader.ReadNif(freeState, freeGraph, TestContext.Current.CancellationToken);
        var freeClip = Assert.Single(free.Clips);
        var freeTrack = Assert.Single(freeClip.EulerRotationTracks);
        Assert.NotNull(freeTrack.Clock);
        Assert.Equal(0.5f, freeTrack.Clock!.StartSeconds);
        Assert.Equal(0.25f, freeTrack.Clock.PhaseSeconds);
        SceneValidation.ValidateStructure(Assemble(freeGraph, free.Clips), TestContext.Current.CancellationToken);

        var (earlyState, earlyGraph) = ReadGraph(ControllerFixture(bigEndian, EulerAxes(late, EmptyAxis, EmptyAxis),
            0.25f, 1f, 0f));
        var early = NifModelAnimationReader.ReadNif(earlyState, earlyGraph, TestContext.Current.CancellationToken);
        Assert.Empty(Assert.Single(early.Clips).EulerRotationTracks);
        Assert.Contains(DecisionsFor(early, 3), static decision => decision.Code == "eulerSampledBeforeFirstKey");
    }

    /// <summary>RE-20 rule 7 for a LINEAR axis, in double: one key holds (rule 4), after the last key holds (rule 6).</summary>
    private static double Rule7((float Time, float Angle)[] keys, float time)
    {
        if (keys.Length == 1)
        {
            return keys[0].Angle;
        }

        if (time > keys[^1].Time)
        {
            return keys[^1].Angle;
        }

        var k = 1;
        while (time > keys[k].Time)
        {
            k++;
        }

        var u = ((double)time - keys[k - 1].Time) / ((double)keys[k].Time - keys[k - 1].Time);
        return (1d - u) * keys[k - 1].Angle + u * keys[k].Angle;
    }

    /// <summary>RE-20 rule 9 in double: W, X, Y, Z from the half-angle sines and cosines, returned as Shared's X, Y, Z, W.</summary>
    private static Quaternion Rule9(double x, double y, double z)
    {
        double cx = Math.Cos(x / 2), sx = Math.Sin(x / 2);
        double cy = Math.Cos(y / 2), sy = Math.Sin(y / 2);
        double cz = Math.Cos(z / 2), sz = Math.Sin(z / 2);
        var w = cx * cy * cz + sx * sy * sz;
        var qx = sx * cy * cz - cx * sy * sz;
        var qy = cx * sy * cz + sx * cy * sz;
        var qz = cx * cy * sz - sx * sy * cz;
        return new Quaternion((float)qx, (float)qy, (float)qz, (float)w);
    }

    /// <summary>Asserts two unit quaternions name one rotation within a per-component tolerance (q and -q are one rotation).</summary>
    private static void AssertSameRotation(Quaternion expected, Quaternion actual, float tolerance, float time)
    {
        if (Quaternion.Dot(expected, actual) < 0f)
        {
            actual = -actual;
        }

        Assert.True(Distance(expected, actual) <= tolerance,
            $"At t = {time}: expected {expected}, actual {actual}, difference {Distance(expected, actual)}.");
    }

    /// <summary>The largest per-component difference between two quaternions, sign-aligned.</summary>
    private static float Distance(Quaternion expected, Quaternion actual)
    {
        if (Quaternion.Dot(expected, actual) < 0f)
        {
            actual = -actual;
        }

        return MathF.Max(MathF.Max(MathF.Abs(expected.X - actual.X), MathF.Abs(expected.Y - actual.Y)),
            MathF.Max(MathF.Abs(expected.Z - actual.Z), MathF.Abs(expected.W - actual.W)));
    }

    /// <summary>A float KeyGroup: Num Keys, Interpolation when there are keys, then time and value per key.</summary>
    private static Action<NifTestBlockWriter> Axis(uint keyType, params (float Time, float Angle)[] keys)
    {
        return w =>
        {
            w.U32((uint)keys.Length);
            if (keys.Length != 0)
            {
                w.U32(keyType);
                foreach (var key in keys)
                {
                    w.F32s(key.Time, key.Angle);
                }
            }
        };
    }

    /// <summary>A float KeyGroup with no keys (Num Keys = 0, no Interpolation word).</summary>
    private static void EmptyAxis(NifTestBlockWriter w)
    {
        w.U32(0);
    }

    /// <summary>
    ///     An XYZ_ROTATION rotation part at 20.2.0.7 (no legacy word): Num Rotation Keys (the record count), Rotation Type
    ///     4, then each record's X, Y and Z groups (the same three groups repeated for every record).
    /// </summary>
    private static Action<NifTestBlockWriter> EulerAxes(Action<NifTestBlockWriter> x, Action<NifTestBlockWriter> y,
        Action<NifTestBlockWriter> z, uint records = 1)
    {
        return w =>
        {
            w.U32(records).U32(XyzEuler);
            for (var record = 0; record < records; record++)
            {
                x(w);
                y(w);
                z(w);
            }
        };
    }

    /// <summary>A rotation part written in the given byte order and read back through the slice-1 view reader.</summary>
    private static NifRotationKeysView EulerView(bool bigEndian, Action<NifTestBlockWriter> rotation)
    {
        var writer = new NifTestBlockWriter(bigEndian);
        rotation(writer);
        var bytes = writer.ToArray();
        var pos = 0;
        Assert.True(NifKeyGroupReader.TryReadRotationView(bytes, ref pos, bytes.Length, bigEndian, Version20207,
            out var view));
        Assert.Equal(bytes.Length, pos);
        return view;
    }

    /// <summary>A single-node, one-triangle document whose clip holds one Euler track on node 0 (no clip clock).</summary>
    private static ModelDocument EulerDocument(SceneEulerRotationTrack track)
    {
        SceneVertex[] vertices =
        [
            new(Vector3.Zero, Vector3.UnitZ, Vector4.One, Vector2.Zero),
            new(Vector3.UnitX, Vector3.UnitZ, Vector4.One, Vector2.UnitX),
            new(Vector3.UnitY, Vector3.UnitZ, Vector4.One, Vector2.UnitY)
        ];
        var mesh = new SceneMesh("triangle", [new ScenePrimitive("triangle", vertices, [0, 1, 2])]);
        return new ModelDocument("nif", "fixture", [new SceneDefinition("scene", [0])],
            [new SceneNode("node", IdentityRest, meshIndex: 0)], [mesh],
            animations: [new SceneAnimation("clip", [], eulerRotationTracks: [track])]);
    }

    /// <summary>
    ///     Root (node 0, manager 2) with child Bone (node 1); manager 2 lists sequence 3 'Turn' (CLAMP over the given
    ///     interval) driving Bone through interpolator 4, whose NiTransformData 5 holds the given rotation part and two
    ///     LINEAR translation keys.
    /// </summary>
    private static byte[] SequenceFixture(bool bigEndian, Action<NifTestBlockWriter> rotation, float start, float stop)
    {
        var builder = new NifTestFileBuilder(bigEndian, Bs);
        var root = builder.AddString("Root");
        var bone = builder.AddString("Bone");
        var turn = builder.AddString("Turn");
        var type = builder.AddString(TransformController);
        Add(builder, 0, "NiNode", Node(root, [1], 2));
        Add(builder, 1, "NiNode", Node(bone, []));
        Add(builder, 2, "NiControllerManager", Manager(0, [3], -1));
        Add(builder, 3, "NiControllerSequence",
            Sequence(turn, [Controlled(4, bone, type)], manager: 2, start: start, stop: stop));
        Add(builder, 4, "NiTransformInterpolator", TransformInterpolator(5));
        Add(builder, 5, "NiTransformData", TransformData(rotation, (0f, 0f, 0f, 0f), (1f, 1f, 2f, 3f)));
        return builder.Build();
    }

    /// <summary>
    ///     Root (node 0) with child Spin (node 1) driven by a free-running CLAMP NiTransformController (block 2) with the
    ///     given clock, through interpolator 3 over NiTransformData 4 holding the given rotation part.
    /// </summary>
    private static byte[] ControllerFixture(bool bigEndian, Action<NifTestBlockWriter> rotation, float start,
        float stop, float phase)
    {
        var builder = new NifTestFileBuilder(bigEndian, Bs);
        var root = builder.AddString("Root");
        var spin = builder.AddString("Spin");
        Add(builder, 0, "NiNode", Node(root, [1]));
        Add(builder, 1, "NiNode", Node(spin, [], 2));
        Add(builder, 2, "NiTransformController",
            TransformControllerBlock(1, 3, Active | ClampCycle, 1f, phase, start, stop));
        Add(builder, 3, "NiTransformInterpolator", TransformInterpolator(4));
        Add(builder, 4, "NiTransformData", TransformData(rotation, (0f, 0f, 0f, 0f), (1f, 1f, 0f, 0f)));
        return builder.Build();
    }
}
