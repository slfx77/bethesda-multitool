using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Animation;
using Slfx77.Multitool.Core.Models;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelAnimationTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     Cut-1b slice 2, <see cref="NifModelCurveMapping" />: one closed-form test per key-group mapping row (plan section
///     1.2 with RE-17 and RE-18), each sampled through Shared's public <see cref="ScenePoseEvaluator" /> or
///     <see cref="SceneTbcTangents" />, and each with a control: the wrong mapping built from the same stored words and
///     shown to give a measurably different value.
/// </summary>
public sealed class NifModelCurveMappingTests
{
    private const uint Linear = 1;
    private const uint Quadratic = 2;
    private const uint Tbc = 3;
    private const uint XyzEuler = 4;
    private const uint Constant = 5;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Linear_Vector3_KeepsTheStoredKeys_AndSamplesLinearly(bool bigEndian)
    {
        var group = KeyGroup(bigEndian, NifKeyValueLayout.Vector3, Linear,
            [Bits(0f), Bits(0f), Bits(0f), Bits(0f)],
            [Bits(2f), Bits(4f), Bits(-2f), Bits(8f)]);

        var curve = Keyed(NifModelCurveMapping.MapTranslation(group));

        Assert.Equal(SceneInterpolation.Linear, curve.Interpolation);
        Assert.Equal(3, curve.ComponentCount);
        Assert.Equal([0f, 2f], curve.Times);
        Assert.Equal([0f, 0f, 0f, 4f, -2f, 8f], curve.Values);
        var mapped = SampleLocal(Document(IdentityRest, Track(curve, SceneTransformProperty.Translation)), 0.5f);
        Assert.Equal(new Vector3(1f, -0.5f, 2f), mapped.Translation);

        // Control: the same keys under Step hold key 0 at t = 0.5.
        var step = new SceneTransformTrack(0, SceneTransformProperty.Translation, curve.Times, curve.Values,
            SceneInterpolation.Step);
        Assert.Equal(Vector3.Zero, SampleLocal(Document(IdentityRest, step), 0.5f).Translation);
    }

    [Fact]
    public void Const_Scale_IsStep_AndHoldsTheEarlierKey()
    {
        var group = KeyGroup(false, NifKeyValueLayout.Float, Constant, [Bits(0f), Bits(2f)], [Bits(1f), Bits(4f)]);

        var curve = Keyed(NifModelCurveMapping.MapScale(group));

        Assert.Equal(SceneInterpolation.Step, curve.Interpolation);
        var mapped = SampleLocal(Document(IdentityRest, Track(curve, SceneTransformProperty.Scale)), 0.5f);
        Assert.Equal(2f, mapped.M11);
        Assert.Equal(4f, SampleLocal(Document(IdentityRest, Track(curve, SceneTransformProperty.Scale)), 1f).M11);

        // Control: the same keys read as LINEAR are halfway at t = 0.5.
        var linear = new SceneTransformTrack(0, SceneTransformProperty.Scale, curve.Times, curve.Values);
        Assert.Equal(3f, SampleLocal(Document(IdentityRest, linear), 0.5f).M11);
    }

    [Fact]
    public void Scale_IsReplicatedToAllThreeAxes()
    {
        var group = KeyGroup(false, NifKeyValueLayout.Float, Linear, [Bits(0f), Bits(2f)], [Bits(1f), Bits(4f)]);

        var curve = Keyed(NifModelCurveMapping.MapScale(group));

        Assert.Equal(3, curve.ComponentCount);
        Assert.Equal([2f, 2f, 2f, 4f, 4f, 4f], curve.Values);
        var mapped = SampleLocal(Document(IdentityRest, Track(curve, SceneTransformProperty.Scale)), 0.5f);
        Assert.Equal(new Vector3(3f, 3f, 3f), new Vector3(mapped.M11, mapped.M22, mapped.M33));

        // Controls: the stored single component is not a Shared scale on its own, and filling only X leaves Y and Z at 1.
        var unreplicated = new SceneTransformTrack(0, SceneTransformProperty.Scale, curve.Times, [2f, 4f]);
        Assert.Throws<InvalidDataException>(() => SampleLocal(Document(IdentityRest, unreplicated), 0.5f));
        var xOnly = new SceneTransformTrack(0, SceneTransformProperty.Scale, curve.Times, [2f, 1f, 1f, 4f, 1f, 1f]);
        Assert.Equal(1f, SampleLocal(Document(IdentityRest, xOnly), 0.5f).M22);
    }

    /// <summary>
    ///     RE-18 on an asymmetric fixture over a two-second segment: key 0 stores Forward 100 and Backward 2, key 1 stores
    ///     Forward 5 and Backward -100. As mapped, the segment leaves key 0 with Backward (2) and enters key 1 with Forward
    ///     (5), in normalized-segment units, so at s = 0.5: 0 + 2/8 + 1/2 - 5/8 = 0.125.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Quadratic_ForwardIsIncoming_BackwardIsOutgoing_InSegmentUnits(bool bigEndian)
    {
        var group = KeyGroup(bigEndian, NifKeyValueLayout.Vector3, Quadratic,
            [Bits(0f), Bits(0f), Bits(0f), Bits(0f), Bits(100f), Bits(0f), Bits(0f), Bits(2f), Bits(0f), Bits(0f)],
            [Bits(2f), Bits(1f), Bits(0f), Bits(0f), Bits(5f), Bits(0f), Bits(0f), Bits(-100f), Bits(0f), Bits(0f)]);

        var curve = Keyed(NifModelCurveMapping.MapTranslation(group));

        Assert.Equal(SceneInterpolation.Hermite, curve.Interpolation);
        Assert.Equal(
            [100f, 0f, 0f, 0f, 0f, 0f, 2f, 0f, 0f, 5f, 0f, 0f, 1f, 0f, 0f, -100f, 0f, 0f],
            curve.Values);
        Assert.Equal(0.125f, SampleLocal(Document(IdentityRest, Track(curve, SceneTransformProperty.Translation)), 1f).M41);

        // Control 1: Forward and Backward exchanged, 100/8 + 1/2 + 100/8 = 25.5.
        var exchanged = new SceneTransformTrack(0, SceneTransformProperty.Translation, curve.Times,
            [2f, 0f, 0f, 0f, 0f, 0f, 100f, 0f, 0f, -100f, 0f, 0f, 1f, 0f, 0f, 5f, 0f, 0f], SceneInterpolation.Hermite);
        Assert.Equal(25.5f, SampleLocal(Document(IdentityRest, exchanged), 1f).M41);

        // Control 2: the same triple read as per-second tangents (scaled by the 2 s interval), 4/8 + 1/2 - 10/8 = -0.25.
        var perSecond = new SceneTransformTrack(0, SceneTransformProperty.Translation, curve.Times, curve.Values,
            SceneInterpolation.CubicSpline);
        Assert.Equal(-0.25f, SampleLocal(Document(IdentityRest, perSecond), 1f).M41);
    }

    /// <summary>
    ///     The plan's retail Hermite control: FO3 <c>meshes/traps/fxgastrapblast.nif</c> (SHA-256 3b5ce0bb...5c67) block 7,
    ///     the two-key QUADRATIC scale, bits copied from the committed cut-1b probe payload. Key 0 stores Forward -0.0 and
    ///     Backward 13.718552, key 1 Forward 13.718552 and Backward -0.0. As mapped the segment is almost a straight line
    ///     (4.429638 at s = 0.25); exchanged it becomes an ease (3.143524).
    /// </summary>
    [Fact]
    public void Quadratic_FxGasTrapBlastScale_IsALineAsShipped_AndAnEaseWhenExchanged()
    {
        var group = KeyGroup(false, NifKeyValueLayout.Float, Quadratic,
            [0x00000000, 0x3F800000, 0x80000000, 0x415B7F30],
            [0x3ECCCCCD, 0x416B7F33, 0x415B7F30, 0x80000000]);

        var curve = Keyed(NifModelCurveMapping.MapScale(group));

        uint[] expectedBits =
        [
            0x80000000, 0x80000000, 0x80000000, 0x3F800000, 0x3F800000, 0x3F800000, 0x415B7F30, 0x415B7F30, 0x415B7F30,
            0x415B7F30, 0x415B7F30, 0x415B7F30, 0x416B7F33, 0x416B7F33, 0x416B7F33, 0x80000000, 0x80000000, 0x80000000
        ];
        Assert.Equal(expectedBits, curve.Values.Select(Bits));
        var mapped = SampleLocal(Document(IdentityRest, Track(curve, SceneTransformProperty.Scale)), 0.1f).M11;
        Assert.Equal(4.429638385772705f, mapped, 5e-6f);

        // Control: Forward and Backward exchanged. Both tangents that the segment reads become -0.0.
        var exchangedValues = (float[])curve.Values.Clone();
        for (var key = 0; key < 2; key++)
        {
            for (var axis = 0; axis < 3; axis++)
            {
                (exchangedValues[key * 9 + axis], exchangedValues[key * 9 + 6 + axis]) =
                    (exchangedValues[key * 9 + 6 + axis], exchangedValues[key * 9 + axis]);
            }
        }

        var exchanged = new SceneTransformTrack(0, SceneTransformProperty.Scale, curve.Times, exchangedValues,
            SceneInterpolation.Hermite);
        var eased = SampleLocal(Document(IdentityRest, exchanged), 0.1f).M11;
        Assert.Equal(3.143524169921875f, eased, 5e-6f);
        Assert.True(MathF.Abs(mapped - eased) > 1f);
    }

    /// <summary>
    ///     The TBC order rule: the stored floats are tension, continuity, bias. Key 1 of a translation group stores
    ///     (0, 0.5, 0.25) between intervals of 1.25 s and 0.75 s. Shared's incoming tangent at key 1 is then
    ///     (0.5 * 0.5 * 1.25 * 1 + 0.5 * 1.5 * 0.75 * 2) * 2 * 1.25 / 2 = 1.796875; nif.xml's order (the second float as
    ///     bias) gives 1.484375. The outgoing tangent is symmetric in continuity and bias and cannot discriminate.
    /// </summary>
    [Fact]
    public void Tbc_UsesTensionContinuityBiasInFileOrder()
    {
        var group = KeyGroup(false, NifKeyValueLayout.Vector3, Tbc,
            [Bits(0f), Bits(0f), Bits(0f), Bits(0f), Bits(0f), Bits(0f), Bits(0f)],
            [Bits(1.25f), Bits(1f), Bits(0f), Bits(0f), Bits(0f), Bits(0.5f), Bits(0.25f)],
            [Bits(2f), Bits(3f), Bits(0f), Bits(0f), Bits(0f), Bits(0f), Bits(0f)]);

        var curve = Keyed(NifModelCurveMapping.MapTranslation(group));

        Assert.Equal(SceneInterpolation.Tbc, curve.Interpolation);
        var parameters = Assert.IsType<SceneTbcParameters[]>(curve.TbcParameters);
        Assert.Equal((0f, 0.5f, 0.25f), (parameters[1].Tension, parameters[1].Continuity, parameters[1].Bias));
        Assert.Equal(1.796875f, IncomingX(curve, parameters));

        var nifXmlOrder = parameters.Select(static p => new SceneTbcParameters(p.Tension, p.Bias, p.Continuity)).ToArray();
        Assert.Equal(1.484375f, IncomingX(curve, nifXmlOrder));

        // Through the evaluator, in the first segment (u = 0.5): 1/8 + 1/2 - 1.796875/8 = 0.400390625; the control
        // gives 0.439453125.
        Assert.Equal(0.400390625f,
            SampleLocal(Document(IdentityRest, Track(curve, SceneTransformProperty.Translation)), 0.625f).M41);
        var control = new SceneTransformTrack(0, SceneTransformProperty.Translation, curve.Times, curve.Values,
            SceneInterpolation.Tbc, tbcParameters: nifXmlOrder, tbcEndpoints: curve.TbcEndpoints);
        Assert.Equal(0.439453125f, SampleLocal(Document(IdentityRest, control), 0.625f).M41);
    }

    /// <summary>
    ///     RE-17 through Shared's named policy: identity at t = 0 and 170 degrees about +Z at t = 1. The counter-warped
    ///     nlerp at t = 0.25 is 2 * atan2(t' sin 85, 1 - t' + t' cos 85) with t' = t + k t (t - 1)(2t - 1) and
    ///     k = 0.5854922 (1 - 0.8227969 cos 85)^2, which is 44.2418 degrees; slerp gives 42.5 and plain nlerp 35.77.
    /// </summary>
    [Fact]
    public void LinearRotation_UsesGamebryoCounterWarpedNlerp_At170Degrees()
    {
        var w = (float)Math.Cos(85d * Math.PI / 180d);
        var z = (float)Math.Sin(85d * Math.PI / 180d);
        var rotation = RotationKeys(Linear, QuatKey(0f, 1f, 0f, 0f, 0f), QuatKey(1f, w, 0f, 0f, z));

        var curve = Keyed(NifModelCurveMapping.MapRotation(rotation, NifModelSquadPolicy.Pc));

        Assert.Equal(SceneInterpolation.GamebryoCounterWarpedNlerp, curve.Interpolation);
        var document = Document(IdentityRest, Track(curve, SceneTransformProperty.Rotation));
        foreach (var (time, expected) in new[] { (0.25f, 44.241829420405324), (0.75f, 125.75817232340418) })
        {
            Assert.Equal(expected, SampleZAngleDegrees(document, time), 1e-3);
        }

        // Control: the same keys under Linear (Shared's slerp) turn 170 * t degrees, 1.74 degrees away at t = 0.25.
        var slerp = new SceneTransformTrack(0, SceneTransformProperty.Rotation, curve.Times, curve.Values);
        var slerpAngle = SampleZAngleDegrees(Document(IdentityRest, slerp), 0.25f);
        Assert.Equal(42.5, slerpAngle, 1e-3);
        Assert.True(Math.Abs(slerpAngle - 44.241829420405324) > 1.5);
    }

    /// <summary>
    ///     RE-17's chain alignment: key 1 is stored as the negated 90-degree rotation (dot -0.707 with key 0). After the
    ///     load-time flip the segment turns the short way, 45 degrees at t = 0.5; the raw keys under the same policy turn
    ///     the long way, to -135 degrees.
    /// </summary>
    [Fact]
    public void LinearRotation_ChainAlignmentTurnsTheShortWay()
    {
        var half = (float)Math.Sqrt(0.5);
        var rotation = RotationKeys(Linear, QuatKey(0f, 1f, 0f, 0f, 0f), QuatKey(1f, -half, 0f, 0f, -half));

        var curve = Keyed(NifModelCurveMapping.MapRotation(rotation, NifModelSquadPolicy.Pc));

        Assert.True(curve.Values[6] > 0f && curve.Values[7] > 0f, "Key 1 should be flipped to +90 degrees.");
        Assert.Equal(45d, SampleZAngleDegrees(Document(IdentityRest, Track(curve, SceneTransformProperty.Rotation)), 0.5f),
            1e-3);

        var raw = new SceneTransformTrack(0, SceneTransformProperty.Rotation, curve.Times,
            [0f, 0f, 0f, 1f, 0f, 0f, -half, -half], SceneInterpolation.GamebryoCounterWarpedNlerp);
        Assert.Equal(-135d, SampleZAngleDegrees(Document(IdentityRest, raw), 0.5f), 1e-3);
    }

    /// <summary>
    ///     The permutation: the file stores (w, x, y, z) = (0.5, -0.5, 0.5, 0.5), which maps to Shared's
    ///     (x, y, z, w) = (-0.5, 0.5, 0.5, 0.5) with no conjugation and carries the unit-X vertex to (0, 0, -1). Reading the
    ///     file order as X, Y, Z, W carries it to (0, 0, 1); the conjugate carries it to (0, -1, 0).
    /// </summary>
    [Fact]
    public void Rotation_IsPermutedToXyzw_WithoutConjugation()
    {
        var rotation = RotationKeys(Linear, QuatKey(0f, 0.5f, -0.5f, 0.5f, 0.5f));

        var curve = Keyed(NifModelCurveMapping.MapRotation(rotation, NifModelSquadPolicy.Pc));

        Assert.Equal([-0.5f, 0.5f, 0.5f, 0.5f], curve.Values);
        AssertNear(new Vector3(0f, 0f, -1f),
            SamplePoint(Document(IdentityRest, Track(curve, SceneTransformProperty.Rotation)), 0f, 1));

        var unpermuted = new SceneTransformTrack(0, SceneTransformProperty.Rotation, curve.Times, [0.5f, -0.5f, 0.5f, 0.5f],
            SceneInterpolation.GamebryoCounterWarpedNlerp);
        AssertNear(new Vector3(0f, 0f, 1f), SamplePoint(Document(IdentityRest, unpermuted), 0f, 1));
        var conjugated = new SceneTransformTrack(0, SceneTransformProperty.Rotation, curve.Times, [0.5f, -0.5f, -0.5f, 0.5f],
            SceneInterpolation.GamebryoCounterWarpedNlerp);
        AssertNear(new Vector3(0f, -1f, 0f), SamplePoint(Document(IdentityRest, conjugated), 0f, 1));
    }

    [Fact]
    public void ConstRotation_IsStepOverTheEngineKeys()
    {
        var half = (float)Math.Sqrt(0.5);
        var rotation = RotationKeys(Constant, QuatKey(0f, 1f, 0f, 0f, 0f), QuatKey(1f, half, 0f, 0f, half));

        var curve = Keyed(NifModelCurveMapping.MapRotation(rotation, NifModelSquadPolicy.Pc));

        Assert.Equal(SceneInterpolation.Step, curve.Interpolation);
        var document = Document(IdentityRest, Track(curve, SceneTransformProperty.Rotation));
        Assert.Equal(0d, SampleZAngleDegrees(document, 0.75f), 1e-3);
        Assert.Equal(90d, SampleZAngleDegrees(document, 1f), 1e-3);

        // Control: the same keys interpolated are 45 degrees along at t = 0.5 instead of holding key 0.
        var interpolated = new SceneTransformTrack(0, SceneTransformProperty.Rotation, curve.Times, curve.Values,
            SceneInterpolation.GamebryoCounterWarpedNlerp);
        Assert.Equal(45d, SampleZAngleDegrees(Document(IdentityRest, interpolated), 0.5f), 1e-3);
        Assert.Equal(0d, SampleZAngleDegrees(document, 0.5f), 1e-3);
    }

    /// <summary>
    ///     Since slices 13 and 16b every rotation key type maps: an XYZ_ROTATION part is three scalar axes through
    ///     <see cref="NifModelCurveMapping.MapEulerRotation" /> (the quaternion mapping refuses it as an argument), and a
    ///     TBC or QUADRATIC quaternion group is a Squad curve (its arithmetic is pinned in NifModelSquadRotationTests).
    ///     Controls: the Euler mapping refuses a quaternion view, and a single-key TBC group is the raw key as its own
    ///     inner points (the engine's fill does not run for one key), which a LINEAR group of the same key is not.
    /// </summary>
    [Fact]
    public void EveryRotationKeyType_HasItsMapping()
    {
        var euler = ReadRotation(new NifAnimationByteWriter(false)
            .Words(1, XyzEuler)
            .Words(1, Linear, Bits(0f), Bits(0.5f))
            .Words(0)
            .Words(0)
            .ToArray());
        Assert.True(euler.IsEuler);
        Assert.Throws<ArgumentException>(() => NifModelCurveMapping.MapRotation(euler, NifModelSquadPolicy.Pc));
        var axes = NifModelCurveMapping.MapEulerRotation(euler);
        Assert.False(axes.IsBlocked);
        Assert.Equal(1, axes.Euler!.X.KeyCount);
        Assert.Equal(Linear, axes.Euler.X.KeyType);
        Assert.True(axes.Euler.Y.IsEmpty);
        Assert.True(axes.Euler.Z.IsEmpty);

        uint[] key = QuatKey(0f, 1f, 0f, 0f, 0f);
        uint[] tbcKey = [.. key, 0, 0, 0];
        var tbc = Keyed(NifModelCurveMapping.MapRotation(RotationKeys(Tbc, tbcKey), NifModelSquadPolicy.Pc));
        Assert.Equal(SceneInterpolation.GamebryoSquad, tbc.Interpolation);
        Assert.Equal(SceneGamebryoSquadPolicy.PcFloat32, tbc.SquadPolicy);
        Assert.Equal(Tbc, tbc.KeyType);
        Assert.Equal([0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f], tbc.Values);
        var quadratic = Keyed(NifModelCurveMapping.MapRotation(RotationKeys(Quadratic, key), NifModelSquadPolicy.Pc));
        Assert.Equal(SceneInterpolation.GamebryoSquad, quadratic.Interpolation);
        Assert.Equal(Quadratic, quadratic.KeyType);

        Assert.Throws<ArgumentException>(() => NifModelCurveMapping.MapEulerRotation(RotationKeys(Linear, key)));
        var linear = Keyed(NifModelCurveMapping.MapRotation(RotationKeys(Linear, key), NifModelSquadPolicy.Pc));
        Assert.Equal(SceneInterpolation.GamebryoCounterWarpedNlerp, linear.Interpolation);
        Assert.Equal(4, linear.Values.Length);
    }

    /// <summary>A key type the mapping does not know fails closed; the control is the same group as LINEAR.</summary>
    [Fact]
    public void UnknownKeyTypes_FailClosed()
    {
        var data = new NifAnimationByteWriter(false).Words(Bits(0f), Bits(1f)).ToArray();
        var unknownFloat = new NifKeyGroupView(data, false, NifKeyValueLayout.Float, 0, 1, 7, 0, 8);
        Assert.Equal(NifModelCurveBlock.UnknownKeyType, NifModelCurveMapping.MapScale(unknownFloat).Block);
        var eulerTypedFloat = new NifKeyGroupView(data, false, NifKeyValueLayout.Float, 0, 1, XyzEuler, 0, 8);
        Assert.Equal(NifModelCurveBlock.UnknownKeyType, NifModelCurveMapping.MapScale(eulerTypedFloat).Block);
        var unknownRotation = new NifRotationKeysView(0, 1, 9, default, default, 0);
        Assert.Equal(NifModelCurveBlock.UnknownKeyType,
            NifModelCurveMapping.MapRotation(unknownRotation, NifModelSquadPolicy.Pc).Block);

        var linearFloat = new NifKeyGroupView(data, false, NifKeyValueLayout.Float, 0, 1, Linear, 0, 8);
        Assert.False(NifModelCurveMapping.MapScale(linearFloat).IsBlocked);
    }

    /// <summary>
    ///     Keys Shared cannot hold are refused, not repaired: a repeated time and a non-finite value. The controls are
    ///     the same groups with the time or value made valid.
    /// </summary>
    [Fact]
    public void RepeatedTimesAndNonFiniteValues_AreBlocked()
    {
        var repeated = KeyGroup(false, NifKeyValueLayout.Float, Linear, [Bits(1f), Bits(1f)], [Bits(1f), Bits(2f)]);
        Assert.Equal(NifModelCurveBlock.InvalidKeyTimes, NifModelCurveMapping.MapScale(repeated).Block);
        var increasing = KeyGroup(false, NifKeyValueLayout.Float, Linear, [Bits(1f), Bits(1f)], [Bits(2f), Bits(2f)]);
        Assert.False(NifModelCurveMapping.MapScale(increasing).IsBlocked);

        var nan = KeyGroup(false, NifKeyValueLayout.Float, Linear, [Bits(0f), 0x7FC00000]);
        Assert.Equal(NifModelCurveBlock.NonFiniteValue, NifModelCurveMapping.MapScale(nan).Block);
        var finite = KeyGroup(false, NifKeyValueLayout.Float, Linear, [Bits(0f), Bits(1f)]);
        Assert.False(NifModelCurveMapping.MapScale(finite).IsBlocked);
    }

    [Fact]
    public void EmptyGroups_HaveNoKeys()
    {
        var empty = KeyGroup(false, NifKeyValueLayout.Vector3, Linear);
        Assert.True(NifModelCurveMapping.MapTranslation(empty).IsEmpty);
        Assert.True(NifModelCurveMapping.MapRotation(RotationKeys(Linear), NifModelSquadPolicy.Pc).IsEmpty);

        // Control: one key is not empty.
        var one = KeyGroup(false, NifKeyValueLayout.Vector3, Linear, [Bits(0f), Bits(1f), Bits(2f), Bits(3f)]);
        Assert.False(NifModelCurveMapping.MapTranslation(one).IsEmpty);
    }

    /// <summary>The curve of a result that must have mapped.</summary>
    private static NifModelCurve Keyed(NifModelCurveResult result)
    {
        Assert.Equal(NifModelCurveBlock.None, result.Block);
        return Assert.IsType<NifModelCurve>(result.Curve);
    }

    /// <summary>A keyed track of node 0 for a mapped curve.</summary>
    private static SceneTransformTrack Track(NifModelCurve curve, SceneTransformProperty property)
    {
        return new NifModelTransformChannel(property, SceneAnimationChannelState.Keyed, null, curve, null).ToTrack(0);
    }

    /// <summary>Shared's incoming tangent at key 1, component x, through its public TBC tangent evaluator.</summary>
    private static float IncomingX(NifModelCurve curve, SceneTbcParameters[] parameters)
    {
        Span<float> incoming = stackalloc float[3];
        Span<float> outgoing = stackalloc float[3];
        SceneTbcTangents.Get(curve.Times, curve.Values, curve.ComponentCount, parameters, curve.TbcEndpoints, 1,
            incoming, outgoing, TestContext.Current.CancellationToken);
        return incoming[0];
    }

    /// <summary>Allows only Float32 rounding of a normalized quaternion and its matrix product.</summary>
    private static void AssertNear(Vector3 expected, Vector3 actual)
    {
        Assert.True(Vector3.Distance(expected, actual) < 1e-5f, $"Expected {expected}, actual {actual}.");
    }
}
