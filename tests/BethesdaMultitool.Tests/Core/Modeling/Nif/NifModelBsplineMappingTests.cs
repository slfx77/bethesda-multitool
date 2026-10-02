using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Animation;
using Slfx77.Multitool.Core.Models;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelAnimationTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     Cut-1b slice 2, plan section 1.4 (<see cref="NifModelBsplineMapping" />): transform B-spline channels onto Shared
///     curves, sampled through the public <see cref="SceneBSplineCurve.Sample" />. The oracle for a decoded control is the
///     runtime formula (NifBsplineTransformReader: bias + s / 32767 * multiplier, in Float32) and, between controls, the
///     renderer's own <see cref="NifOpenUniformCubicBspline" /> over the runtime-decoded controls. Controls: the 32768
///     divisor, a wrong handle offset, an unpermuted rotation and an unreplicated scale.
/// </summary>
public sealed class NifModelBsplineMappingTests
{
    private const int ControlCount = 5;
    private const float TranslationBias = 10f;
    private const float TranslationMultiplier = 100f;
    private const float RotationBias = 0f;
    private const float RotationMultiplier = 1f;
    private const float ScaleBias = 1f;
    private const float ScaleMultiplier = 0.5f;

    /// <summary>Translation controls (x, y, z), handle 0.</summary>
    private static readonly short[][] TranslationShorts =
    [
        [32767, -32768, 12345], [1000, -1000, 20000], [-12000, 30000, 5], [7, -7, 16384], [32767, 0, -32767]
    ];

    /// <summary>Rotation controls as stored (w, x, y, z), handle 15.</summary>
    private static readonly short[][] RotationShorts =
    [
        [30000, 1000, -2000, 3000], [25000, 5000, -6000, 7000], [20000, 9000, -10000, 11000],
        [15000, 13000, -14000, 15000], [10000, 17000, -18000, 19000]
    ];

    /// <summary>Scale controls, handle 35.</summary>
    private static readonly short[] ScaleShorts = [16000, -16000, 32767, 100, -32768];

    [Fact]
    public void CompactTranslation_KeepsTheShorts_AndDecodesWithThe32767RuntimeFormula()
    {
        var spline = Mapped(NifModelBsplineMapping.MapTransformChannel(
            CompactInterpolator(), SceneTransformProperty.Translation, CompactData(), ControlCount));

        Assert.True(spline.IsQuantized);
        Assert.Equal((3, 3, ControlCount), (spline.Degree, spline.ComponentCount, spline.ControlPointCount));
        Assert.Equal((0f, 2f), (spline.StartSeconds, spline.StopSeconds));
        Assert.Equal((TranslationBias, TranslationMultiplier), (spline.Bias!.Value, spline.Multiplier!.Value));
        Assert.Equal(TranslationShorts.SelectMany(static control => control), spline.QuantizedControlPoints);

        var start = Sample(spline, 0f, 3);
        var expected = TranslationShorts[0].Select(static s => Runtime(s, TranslationBias, TranslationMultiplier)).ToArray();
        Assert.Equal(expected, start);

        // Control 1: the 32768 divisor moves control 0's x from 110 to 109.99695.
        Assert.NotEqual(TranslationBias + TranslationShorts[0][0] / 32768f * TranslationMultiplier, start[0]);
        // Control 2: a handle one scalar late reads (-32768, 12345, 1000) as control 0.
        Assert.NotEqual(Runtime(TranslationShorts[0][1], TranslationBias, TranslationMultiplier), start[0]);

        // Between controls: Shared equals the renderer's basis over the runtime-decoded controls, and the 32768 decode
        // misses by more than 1e-3 on every axis at t = 0.1.
        var runtime = NifOpenUniformCubicBspline.Sample(Decoded(32767f), 0f, 2f, 0.1f);
        var wrongDivisor = NifOpenUniformCubicBspline.Sample(Decoded(32768f), 0f, 2f, 0.1f);
        var interior = Sample(spline, 0.1f, 3);
        float[] runtimeAxes = [runtime.X, runtime.Y, runtime.Z];
        float[] wrongAxes = [wrongDivisor.X, wrongDivisor.Y, wrongDivisor.Z];
        for (var axis = 0; axis < 3; axis++)
        {
            Assert.Equal(runtimeAxes[axis], interior[axis], 1e-4f);
            Assert.True(MathF.Abs(wrongAxes[axis] - interior[axis]) > 1e-3f);
        }
    }

    /// <summary>
    ///     Each rotation control is stored W, X, Y, Z and becomes X, Y, Z, W, untouched otherwise: no sign alignment and
    ///     no normalization (RE-17 rule step 5).
    /// </summary>
    [Fact]
    public void CompactRotation_IsPermutedPerControl()
    {
        var spline = Mapped(NifModelBsplineMapping.MapTransformChannel(
            CompactInterpolator(), SceneTransformProperty.Rotation, CompactData(), ControlCount));

        Assert.Equal(4, spline.ComponentCount);
        var permuted = RotationShorts.SelectMany(static c => new[] { c[1], c[2], c[3], c[0] });
        Assert.Equal(permuted, spline.QuantizedControlPoints);
        var start = Sample(spline, 0f, 4);
        var stored = RotationShorts[0];
        var expected = new[] { stored[1], stored[2], stored[3], stored[0] }
            .Select(static s => Runtime(s, RotationBias, RotationMultiplier));
        Assert.Equal(expected, start);

        // Control: the unpermuted order would put the stored W in X.
        Assert.NotEqual(Runtime(stored[0], RotationBias, RotationMultiplier), start[0]);
    }

    [Fact]
    public void CompactScale_IsReplicatedThreeTimes()
    {
        var spline = Mapped(NifModelBsplineMapping.MapTransformChannel(
            CompactInterpolator(), SceneTransformProperty.Scale, CompactData(), ControlCount));

        Assert.Equal(3, spline.ComponentCount);
        Assert.Equal(ScaleShorts.SelectMany(static s => new[] { s, s, s }), spline.QuantizedControlPoints);
        var runtimeControls = ScaleShorts.Select(static s => Runtime(s, ScaleBias, ScaleMultiplier)).ToArray();
        var interior = Sample(spline, 0.1f, 3);
        var runtime = NifOpenUniformCubicBspline.Sample(runtimeControls, 0f, 2f, 0.1f);
        Assert.All(interior, value => Assert.Equal(runtime, value, 1e-6f));

        // Control: the stored one-component curve is not a Shared scale channel.
        var unreplicated = new SceneBSplineCurve(3, 1, 0f, 2f, ScaleShorts, ScaleBias, ScaleMultiplier);
        var track = new SceneTransformTrack(0, SceneTransformProperty.Scale, [], [], SceneInterpolation.BSpline,
            spline: unreplicated);
        Assert.Throws<InvalidDataException>(() => SampleLocal(Document(IdentityRest, track), 0.1f));
    }

    [Fact]
    public void FloatTransform_KeepsFloat32Controls_AndPermutesRotation()
    {
        var floats = new List<float>();
        floats.AddRange(TranslationShorts.SelectMany(static c => c.Select(static s => s / 1000f)));
        floats.AddRange(RotationShorts.SelectMany(static c => c.Select(static s => s / 32767f)));
        floats.AddRange(ScaleShorts.Select(static s => 1f + s / 65534f));
        var writer = new NifAnimationByteWriter(true);
        foreach (var value in floats)
        {
            writer.F32(value);
        }

        var bytes = writer.ToArray();
        var data = new NifBsplineDataView(bytes, true, 0, floats.Count, bytes.Length, 0, bytes.Length);
        var interpolator = CompactInterpolator() with
        {
            TypeName = "NiBSplineTransformInterpolator", Compact = false, OffsetBits = [], HalfRangeBits = []
        };

        var rotation = Mapped(NifModelBsplineMapping.MapTransformChannel(
            interpolator, SceneTransformProperty.Rotation, data, ControlCount));

        Assert.False(rotation.IsQuantized);
        var storedRotation = floats.Skip(15).ToArray();
        var expected = Enumerable.Range(0, ControlCount).SelectMany(c => new[]
        {
            storedRotation[c * 4 + 1], storedRotation[c * 4 + 2], storedRotation[c * 4 + 3], storedRotation[c * 4]
        });
        Assert.Equal(expected, rotation.ControlPoints);
        Assert.Equal(expected.Take(4), Sample(rotation, 0f, 4));

        // Control: the unpermuted first control starts with the stored W.
        Assert.NotEqual(storedRotation[0], Sample(rotation, 0f, 4)[0]);
    }

    [Fact]
    public void AbsentHandle_MeansNoCurve_AndTheStaticDecides()
    {
        var interpolator = CompactInterpolator(scaleHandle: NifModelBsplineMapping.AbsentHandle) with
        {
            StaticValueBits =
            [
                0xFF7FFFFF, 0xFF7FFFFF, 0xFF7FFFFF, 0xFF7FFFFF, 0xFF7FFFFF, 0xFF7FFFFF, 0xFF7FFFFF, Bits(1.5f)
            ]
        };

        Assert.True(NifModelBsplineMapping.MapTransformChannel(
            interpolator, SceneTransformProperty.Scale, CompactData(), ControlCount).IsEmpty);
        var channel = Channel(NifModelTransformChannelMapping.MapBspline(
            interpolator, SceneTransformProperty.Scale, CompactData(), ControlCount));
        Assert.Equal(SceneAnimationChannelState.Constant, channel.State);
        Assert.Equal([1.5f, 1.5f, 1.5f], channel.StaticValue);
        Assert.Equal(1.5f, SampleLocal(Document(IdentityRest, channel.ToTrack(0)), 0.4f).M22);

        // Control: the stored handle keys the channel, and the static becomes its fallback.
        var keyed = Channel(NifModelTransformChannelMapping.MapBspline(
            interpolator with { Handles = [0, 15, 35] }, SceneTransformProperty.Scale, CompactData(), ControlCount));
        Assert.Equal(SceneAnimationChannelState.Keyed, keyed.State);
        Assert.NotNull(keyed.Spline);
        Assert.Equal([1.5f, 1.5f, 1.5f], keyed.StaticValue);
    }

    [Theory]
    [InlineData(3u, NifModelCurveBlock.BsplineControlPointCount)]
    [InlineData(4u, NifModelCurveBlock.None)]
    internal void FewerThanFourControls_AreBlocked(uint controlPointCount, NifModelCurveBlock expected)
    {
        var result = NifModelBsplineMapping.MapTransformChannel(
            CompactInterpolator(), SceneTransformProperty.Scale, CompactData(), controlPointCount);

        Assert.Equal(expected, result.Block);
    }

    [Fact]
    public void AdmissionFailures_AreBlocked_AndTheirControlsMap()
    {
        var data = CompactData();
        Assert.Equal(NifModelCurveBlock.BsplineControlsOutOfRange, NifModelBsplineMapping.MapTransformChannel(
            CompactInterpolator(rotationHandle: 30), SceneTransformProperty.Rotation, data, ControlCount).Block);
        Assert.Equal(NifModelCurveBlock.BsplineNegativeHalfRange, NifModelBsplineMapping.MapTransformChannel(
            CompactInterpolator(rotationMultiplier: -1f), SceneTransformProperty.Rotation, data, ControlCount).Block);
        Assert.Equal(NifModelCurveBlock.BsplineMissingData, NifModelBsplineMapping.MapTransformChannel(
            CompactInterpolator(), SceneTransformProperty.Rotation, null, ControlCount).Block);
        Assert.Equal(NifModelCurveBlock.BsplineMissingData, NifModelBsplineMapping.MapTransformChannel(
            CompactInterpolator(), SceneTransformProperty.Rotation, data, null).Block);
        Assert.Equal(NifModelCurveBlock.BsplineInvalidInterval, NifModelBsplineMapping.MapTransformChannel(
            CompactInterpolator(stop: 0f), SceneTransformProperty.Rotation, data, ControlCount).Block);

        Assert.Equal(NifModelCurveBlock.None, NifModelBsplineMapping.MapTransformChannel(
            CompactInterpolator(), SceneTransformProperty.Rotation, data, ControlCount).Block);
    }

    /// <summary>
    ///     A compact rotation reaches the evaluator as a keyed B-spline track: at the start it is the first control,
    ///     normalized by Shared after the component spline.
    /// </summary>
    [Fact]
    public void CompactRotation_EvaluatesAsTheNormalizedFirstControl()
    {
        var channel = Channel(NifModelTransformChannelMapping.MapBspline(
            CompactInterpolator(), SceneTransformProperty.Rotation, CompactData(), ControlCount));
        var stored = RotationShorts[0];
        var expected = Quaternion.Normalize(new Quaternion(Runtime(stored[1], 0f, 1f), Runtime(stored[2], 0f, 1f),
            Runtime(stored[3], 0f, 1f), Runtime(stored[0], 0f, 1f)));

        var actual = SamplePoint(Document(IdentityRest, channel.ToTrack(0)), 0f, 2);

        Assert.True(Vector3.Distance(Vector3.Transform(Vector3.UnitY, expected), actual) < 1e-5f);
        // Control: the unpermuted control is nearly a half turn about X and carries the unit-Y vertex to about -Y.
        var unpermuted = Quaternion.Normalize(new Quaternion(Runtime(stored[0], 0f, 1f), Runtime(stored[1], 0f, 1f),
            Runtime(stored[2], 0f, 1f), Runtime(stored[3], 0f, 1f)));
        Assert.True(Vector3.Distance(Vector3.Transform(Vector3.UnitY, unpermuted), actual) > 1f);
    }

    /// <summary>The runtime decode (NifBsplineTransformReader.ReadCompressed), restated in Float32.</summary>
    private static float Runtime(short value, float bias, float multiplier)
    {
        return bias + value / 32767f * multiplier;
    }

    /// <summary>The translation controls decoded with the given divisor.</summary>
    private static Vector3[] Decoded(float divisor)
    {
        return TranslationShorts.Select(c => new Vector3(
            TranslationBias + c[0] / divisor * TranslationMultiplier,
            TranslationBias + c[1] / divisor * TranslationMultiplier,
            TranslationBias + c[2] / divisor * TranslationMultiplier)).ToArray();
    }

    /// <summary>Shared's public sample of a curve.</summary>
    private static float[] Sample(SceneBSplineCurve spline, float seconds, int width)
    {
        var destination = new float[width];
        spline.Sample(seconds, destination, TestContext.Current.CancellationToken);
        return destination;
    }

    /// <summary>The curve of a result that must have mapped.</summary>
    private static SceneBSplineCurve Mapped(NifModelBsplineResult result)
    {
        Assert.Equal(NifModelCurveBlock.None, result.Block);
        return Assert.IsType<SceneBSplineCurve>(result.Spline);
    }

    /// <summary>The channel of a result that must have mapped.</summary>
    private static NifModelTransformChannel Channel(NifModelTransformChannelResult result)
    {
        Assert.Equal(NifModelCurveBlock.None, result.Block);
        return Assert.IsType<NifModelTransformChannel>(result.Channel);
    }

    /// <summary>The 40 compact controls (translation, rotation, scale) as a little-endian NiBSplineData.</summary>
    private static NifBsplineDataView CompactData()
    {
        var writer = new NifAnimationByteWriter(false);
        foreach (var value in TranslationShorts.SelectMany(static c => c)
                     .Concat(RotationShorts.SelectMany(static c => c)).Concat(ScaleShorts))
        {
            writer.I16(value);
        }

        var bytes = writer.ToArray();
        return new NifBsplineDataView(bytes, false, 0, 0, 0, bytes.Length / 2, bytes.Length);
    }

    /// <summary>An NiBSplineCompTransformInterpolator whose statics are all #INV_FLT#.</summary>
    private static NifBsplineInterpolatorView CompactInterpolator(
        uint translationHandle = 0,
        uint rotationHandle = 15,
        uint scaleHandle = 35,
        float stop = 2f,
        float rotationMultiplier = RotationMultiplier)
    {
        return new NifBsplineInterpolatorView(
            "NiBSplineCompTransformInterpolator",
            NifBsplineInterpolatorKind.Transform,
            true,
            Bits(0f),
            Bits(stop),
            1,
            2,
            [0xFF7FFFFF, 0xFF7FFFFF, 0xFF7FFFFF, 0xFF7FFFFF, 0xFF7FFFFF, 0xFF7FFFFF, 0xFF7FFFFF, 0xFF7FFFFF],
            [translationHandle, rotationHandle, scaleHandle],
            [Bits(TranslationBias), Bits(RotationBias), Bits(ScaleBias)],
            [Bits(TranslationMultiplier), Bits(rotationMultiplier), Bits(ScaleMultiplier)]);
    }
}
