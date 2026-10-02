using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Core.Modeling.Nif;
using Slfx77.Multitool.Core.Models;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelAnimationTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     RE-19 (<see cref="NifModelTbcEndpoints" />): the engine's TBC boundary tangents, pinned against hand-computed
///     closed forms with nonzero endpoint parameters (which no retail endpoint carries), against Float32 bits from a
///     separate binary32 model (numpy float32, every operation rounded in the rule's order), and through Shared's
///     evaluator. The controls are the rejected endpoint hypotheses RE-19 lists: zero, the plain chord, the chord times
///     (1 - T), and the chord or closed form computed without the Float32 mirrored neighbour.
/// </summary>
public sealed class NifModelTbcEndpointsTests
{
    private const uint Tbc = 3;

    /// <summary>
    ///     A scale group P = (1, 3, 7) at times 0, 1, 2 with key 0's floats (0.5, 0.5, 0.5) and key 2's
    ///     (-0.5, 0.25, -0.5). StartOutgoing = (1 - 0.5)(1 + 0.25)(3 - 1) = 1.25 and
    ///     EndIncoming = (1 + 0.5)(1 + 0.125)(7 - 3) = 6.75, both exact in Float32.
    /// </summary>
    [Fact]
    public void Endpoints_MatchTheClosedForm_WithNonzeroParameters()
    {
        var group = ScaleGroup();

        var curve = Assert.IsType<NifModelCurve>(NifModelCurveMapping.MapScale(group).Curve);

        var endpoints = Assert.IsType<SceneTbcEndpointTangents>(curve.TbcEndpoints);
        Assert.Equal([1.25f, 1.25f, 1.25f], endpoints.StartOutgoing);
        Assert.Equal([6.75f, 6.75f, 6.75f], endpoints.EndIncoming);

        // Controls: zero, the plain chord and the tension-only chord are each a different pair.
        foreach (var (start, end) in new[] { (0f, 0f), (2f, 4f), (1f, 6f) })
        {
            Assert.NotEqual((start, end), (endpoints.StartOutgoing[0], endpoints.EndIncoming[0]));
        }
    }

    /// <summary>
    ///     The endpoints reach Shared's sampling. In segment 0 at u = 0.5 with key 1's incoming tangent 3 (Shared's
    ///     interior formula, parameters zero): 0.5 + 1.25/8 + 1.5 - 3/8 = 1.78125; in segment 1 with key 1's outgoing 3
    ///     and EndIncoming 6.75: 1.5 + 3/8 + 3.5 - 6.75/8 = 4.53125. The plain chord endpoints give 1.875 and 4.875.
    /// </summary>
    [Fact]
    public void Endpoints_DriveTheFirstAndLastSegments()
    {
        var curve = Assert.IsType<NifModelCurve>(NifModelCurveMapping.MapScale(ScaleGroup()).Curve);
        var track = new NifModelTransformChannel(SceneTransformProperty.Scale, SceneAnimationChannelState.Keyed, null,
            curve, null).ToTrack(0);

        Assert.Equal(1.78125f, SampleLocal(Document(IdentityRest, track), 0.5f).M11);
        Assert.Equal(4.53125f, SampleLocal(Document(IdentityRest, track), 1.5f).M11);

        var chord = new SceneTransformTrack(0, SceneTransformProperty.Scale, curve.Times, curve.Values,
            SceneInterpolation.Tbc, tbcParameters: curve.TbcParameters,
            tbcEndpoints: new SceneTbcEndpointTangents([2f, 2f, 2f], [4f, 4f, 4f]));
        Assert.Equal(1.875f, SampleLocal(Document(IdentityRest, chord), 0.5f).M11);
        Assert.Equal(4.875f, SampleLocal(Document(IdentityRest, chord), 1.5f).M11);
    }

    /// <summary>
    ///     The mirrored neighbour is rounded to Float32 before the difference, as the engines do. On this group (all
    ///     parameters zero, as on every retail endpoint) the rule gives StartOutgoing 0xBEF808ED and EndIncoming
    ///     0xBD9773F4, while the Float32 chord gives 0xBEF808EE and 0xBD9773F0, and so does the difference taken in double
    ///     and rounded once (the closed form's value). Expected bits from the separate numpy float32 model.
    /// </summary>
    [Fact]
    public void Endpoints_RoundTheMirroredNeighbourToFloat32()
    {
        uint[] values = [0x3F984049, 0x3F347C1B, 0xBF623C3B, 0xBF752AB9];
        var group = KeyGroup(false, NifKeyValueLayout.Float, Tbc,
            [Bits(0f), values[0], 0, 0, 0],
            [Bits(1f), values[1], 0, 0, 0],
            [Bits(2f), values[2], 0, 0, 0],
            [Bits(3f), values[3], 0, 0, 0]);

        Assert.True(NifModelTbcEndpoints.TryCompute(group, 1, out var endpoints));

        Assert.Equal(0xBEF808EDu, Bits(endpoints!.StartOutgoing[0]));
        Assert.Equal(0xBD9773F4u, Bits(endpoints.EndIncoming[0]));

        var p = values.Select(BitConverter.UInt32BitsToSingle).ToArray();
        Assert.Equal(0xBEF808EEu, Bits(p[1] - p[0]));
        Assert.Equal(0xBEF808EEu, Bits((float)((double)p[1] - p[0])));
        Assert.Equal(0xBD9773F0u, Bits(p[3] - p[2]));
        Assert.Equal(0xBD9773F0u, Bits((float)((double)p[3] - p[2])));
    }

    /// <summary>
    ///     One key has no endpoints (the engine writes none); two keys have them. The control shows why the second case
    ///     matters: Shared refuses a multi-key TBC track without explicit endpoints.
    /// </summary>
    [Fact]
    public void OnlyMultiKeyGroups_CarryEndpoints()
    {
        var single = KeyGroup(false, NifKeyValueLayout.Float, Tbc, [Bits(0f), Bits(2f), 0, 0, 0]);
        var singleCurve = Assert.IsType<NifModelCurve>(NifModelCurveMapping.MapScale(single).Curve);
        Assert.Null(singleCurve.TbcEndpoints);
        var singleTrack = new NifModelTransformChannel(SceneTransformProperty.Scale, SceneAnimationChannelState.Keyed,
            null, singleCurve, null).ToTrack(0);
        Assert.Equal(2f, SampleLocal(Document(IdentityRest, singleTrack), 3f).M11);

        var pair = KeyGroup(false, NifKeyValueLayout.Float, Tbc, [Bits(0f), Bits(2f), 0, 0, 0], [Bits(1f), Bits(4f), 0, 0, 0]);
        var pairCurve = Assert.IsType<NifModelCurve>(NifModelCurveMapping.MapScale(pair).Curve);
        Assert.NotNull(pairCurve.TbcEndpoints);

        var withoutEndpoints = new SceneTransformTrack(0, SceneTransformProperty.Scale, pairCurve.Times, pairCurve.Values,
            SceneInterpolation.Tbc, tbcParameters: pairCurve.TbcParameters);
        Assert.Throws<InvalidDataException>(() => SampleLocal(Document(IdentityRest, withoutEndpoints), 0.5f));
    }

    /// <summary>The unit-parameter scale group of the closed-form tests.</summary>
    private static NifKeyGroupView ScaleGroup()
    {
        return KeyGroup(false, NifKeyValueLayout.Float, Tbc,
            [Bits(0f), Bits(1f), Bits(0.5f), Bits(0.5f), Bits(0.5f)],
            [Bits(1f), Bits(3f), 0, 0, 0],
            [Bits(2f), Bits(7f), Bits(-0.5f), Bits(0.25f), Bits(-0.5f)]);
    }
}
