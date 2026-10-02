using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Animation;
using Slfx77.Multitool.Core.Models;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelAnimationTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     Cut-1b slice 2, plan section 1.3 (<see cref="NifModelChannelStateMapping" /> and its composition in
///     <see cref="NifModelTransformChannelMapping" />): Keyed, Constant and NotDriven from the keys and the static
///     NiQuatTransform, sampled through Shared's public <see cref="ScenePoseEvaluator" /> on a node whose rest translation
///     is (1, 2, 3). Controls: the sentinel typed as Constant, a valid static typed as NotDriven, and the unpermuted
///     static rotation.
/// </summary>
public sealed class NifModelChannelStateMappingTests
{
    private const uint Sentinel = 0xFF7FFFFF;
    private static readonly Vector3 RestTranslation = new(1f, 2f, 3f);

    [Fact]
    public void SentinelStatic_WithoutKeys_IsNotDriven()
    {
        var channel = Channel(NifModelTransformChannelMapping.MapKeyframe(
            Interpolator(Sentinel, Sentinel, Sentinel), null, SceneTransformProperty.Translation, NifModelSquadPolicy.Pc));

        Assert.Equal(SceneAnimationChannelState.NotDriven, channel.State);
        Assert.Null(channel.StaticValue);
        Assert.Equal(RestTranslation, SampleLocal(Document(Rest(), channel.ToTrack(0)), 0.5f).Translation);

        // Control: the sentinel typed as Constant drives the node to (-FLT_MAX, -FLT_MAX, -FLT_MAX).
        var constant = new SceneTransformTrack(0, SceneTransformProperty.Translation, [], [],
            state: SceneAnimationChannelState.Constant, staticValue: [-float.MaxValue, -float.MaxValue, -float.MaxValue]);
        Assert.Equal(-float.MaxValue, SampleLocal(Document(Rest(), constant), 0.5f).M41);
    }

    [Fact]
    public void ValidStatic_WithoutKeys_IsConstant()
    {
        var channel = Channel(NifModelTransformChannelMapping.MapKeyframe(
            Interpolator(Bits(1.5f), Bits(-2f), Bits(4f)), null, SceneTransformProperty.Translation, NifModelSquadPolicy.Pc));

        Assert.Equal(SceneAnimationChannelState.Constant, channel.State);
        Assert.Equal([1.5f, -2f, 4f], channel.StaticValue);
        Assert.Equal(new Vector3(1.5f, -2f, 4f), SampleLocal(Document(Rest(), channel.ToTrack(0)), 0.5f).Translation);

        // Control: the same channel typed as NotDriven leaves the rest translation.
        var notDriven = new SceneTransformTrack(0, SceneTransformProperty.Translation, [], [],
            state: SceneAnimationChannelState.NotDriven);
        Assert.Equal(RestTranslation, SampleLocal(Document(Rest(), notDriven), 0.5f).Translation);
    }

    /// <summary>
    ///     With keys, a valid static is kept as the fallback and a sentinel static is dropped; either way the keys drive
    ///     the node, never the fallback.
    /// </summary>
    [Fact]
    public void Keys_MakeTheChannelKeyed_AndKeepAValidStaticAsFallback()
    {
        var data = TranslationData([Bits(0f), Bits(0f), Bits(0f), Bits(0f)], [Bits(1f), Bits(8f), Bits(8f), Bits(8f)]);

        var withFallback = Channel(NifModelTransformChannelMapping.MapKeyframe(
            Interpolator(Bits(5f), Bits(6f), Bits(7f)), data, SceneTransformProperty.Translation, NifModelSquadPolicy.Pc));
        var withoutFallback = Channel(NifModelTransformChannelMapping.MapKeyframe(
            Interpolator(Sentinel, Sentinel, Sentinel), data, SceneTransformProperty.Translation, NifModelSquadPolicy.Pc));

        Assert.Equal(SceneAnimationChannelState.Keyed, withFallback.State);
        Assert.Equal([5f, 6f, 7f], withFallback.StaticValue);
        Assert.Equal(SceneAnimationChannelState.Keyed, withoutFallback.State);
        Assert.Null(withoutFallback.StaticValue);
        Assert.Equal(new Vector3(4f, 4f, 4f),
            SampleLocal(Document(Rest(), withFallback.ToTrack(0)), 0.5f).Translation);

        // Control: typed as Constant, the fallback would have driven the node instead of the keys.
        var constant = new SceneTransformTrack(0, SceneTransformProperty.Translation, [], [],
            state: SceneAnimationChannelState.Constant, staticValue: withFallback.StaticValue);
        Assert.Equal(new Vector3(5f, 6f, 7f), SampleLocal(Document(Rest(), constant), 0.5f).Translation);
    }

    [Fact]
    public void MixedSentinelComponents_Throw()
    {
        Assert.Throws<InvalidDataException>(() => NifModelTransformChannelMapping.MapKeyframe(
            Interpolator(Sentinel, Bits(1f), Sentinel), null, SceneTransformProperty.Translation, NifModelSquadPolicy.Pc));
        Assert.Throws<InvalidDataException>(() => NifModelChannelStateMapping.Map(
            SceneTransformProperty.Rotation, [Bits(1f), Sentinel, 0, 0], true));

        // Control: all four rotation words sentinel is an ordinary NotDriven channel.
        Assert.Equal(SceneAnimationChannelState.NotDriven, NifModelChannelStateMapping.Map(
            SceneTransformProperty.Rotation, [Sentinel, Sentinel, Sentinel, Sentinel], false).State);
    }

    [Fact]
    public void ZeroStaticQuaternion_Throws()
    {
        Assert.Throws<InvalidDataException>(() => NifModelChannelStateMapping.Map(
            SceneTransformProperty.Rotation, [0, 0, 0, 0], false));
        Assert.Throws<InvalidDataException>(() => NifModelChannelStateMapping.Map(
            SceneTransformProperty.Rotation, [0x80000000, 0, 0x80000000, 0], true));

        // Control: the identity stored as w = 1 is a valid Constant (0, 0, 0, 1).
        var identity = NifModelChannelStateMapping.Map(SceneTransformProperty.Rotation, [Bits(1f), 0, 0, 0], false);
        Assert.Equal(SceneAnimationChannelState.Constant, identity.State);
        Assert.Equal([0f, 0f, 0f, 1f], identity.StaticValue);
    }

    [Fact]
    public void NonFiniteStatic_Throws()
    {
        Assert.Throws<InvalidDataException>(() => NifModelChannelStateMapping.Map(
            SceneTransformProperty.Scale, [0x7FC00000], false));
        Assert.Throws<InvalidDataException>(() => NifModelChannelStateMapping.Map(
            SceneTransformProperty.Scale, [0x7F800000], true));

        // Control: +FLT_MAX is finite and not the #INV_FLT# sentinel, so it is kept as authored.
        Assert.Equal([float.MaxValue, float.MaxValue, float.MaxValue],
            NifModelChannelStateMapping.Map(SceneTransformProperty.Scale, [0x7F7FFFFF], false).StaticValue);
    }

    /// <summary>
    ///     A static rotation stored (w, x, y, z) = (0.5, -0.5, 0.5, 0.5) becomes Shared's (-0.5, 0.5, 0.5, 0.5) and carries
    ///     the unit-X vertex to (0, 0, -1); a static scale s becomes (s, s, s).
    /// </summary>
    [Fact]
    public void StaticRotation_IsPermuted_AndStaticScaleReplicated()
    {
        var interpolator = new NifTransformInterpolatorView(Sentinel, Sentinel, Sentinel,
            Bits(0.5f), Bits(-0.5f), Bits(0.5f), Bits(0.5f), Bits(2.5f), -1);

        var rotation = Channel(NifModelTransformChannelMapping.MapKeyframe(interpolator, null, SceneTransformProperty.Rotation, NifModelSquadPolicy.Pc));
        var scale = Channel(NifModelTransformChannelMapping.MapKeyframe(interpolator, null, SceneTransformProperty.Scale, NifModelSquadPolicy.Pc));

        Assert.Equal([-0.5f, 0.5f, 0.5f, 0.5f], rotation.StaticValue);
        Assert.Equal([2.5f, 2.5f, 2.5f], scale.StaticValue);
        var identity = new SceneTrs(Vector3.Zero, Quaternion.Identity, Vector3.One);
        var point = SamplePoint(Document(identity, rotation.ToTrack(0)), 0f, 1);
        Assert.True(Vector3.Distance(new Vector3(0f, 0f, -1f), point) < 1e-6f, $"Actual {point}.");
        Assert.Equal(2.5f, SampleLocal(Document(identity, scale.ToTrack(0)), 0f).M33);

        // Control: the file order read as X, Y, Z, W carries the vertex to (0, 0, 1).
        var unpermuted = new SceneTransformTrack(0, SceneTransformProperty.Rotation, [], [],
            state: SceneAnimationChannelState.Constant, staticValue: [0.5f, -0.5f, 0.5f, 0.5f]);
        Assert.True(Vector3.Distance(new Vector3(0f, 0f, 1f), SamplePoint(Document(identity, unpermuted), 0f, 1)) < 1e-6f);
    }

    /// <summary>
    ///     The static is classified first. An Euler rotation (slice 13) maps to axes, carries a valid static for native
    ///     state only, and a malformed static still throws; a blocked quaternion curve (a repeated key time) is reported
    ///     after the static check, and a malformed static throws there too. Control: the translation of the same block
    ///     has no keys and maps as Constant.
    /// </summary>
    [Fact]
    public void BlockedAndEulerCurves_AreReported_AfterTheStaticIsChecked()
    {
        var data = KeyframeData(new NifAnimationByteWriter(false)
            .Words(1, 4).Words(1, 1, Bits(0f), Bits(0.5f)).Words(0).Words(0)
            .Words(0)
            .Words(0)
            .ToArray());

        var mapped = NifModelTransformChannelMapping.MapKeyframe(
            new NifTransformInterpolatorView(0, 0, 0, Sentinel, Sentinel, Sentinel, Sentinel, Bits(1f), 1), data,
            SceneTransformProperty.Rotation, NifModelSquadPolicy.Pc);
        Assert.Equal(NifModelCurveBlock.None, mapped.Block);
        Assert.Null(mapped.Channel);
        Assert.True(mapped.IsEuler);
        Assert.Null(mapped.Euler!.StaticRotation);
        var withStatic = NifModelTransformChannelMapping.MapKeyframe(
            new NifTransformInterpolatorView(0, 0, 0, Bits(0.5f), Bits(-0.5f), Bits(0.5f), Bits(0.5f), Bits(1f), 1), data,
            SceneTransformProperty.Rotation, NifModelSquadPolicy.Pc);
        Assert.Equal([-0.5f, 0.5f, 0.5f, 0.5f], withStatic.Euler!.StaticRotation);
        Assert.Throws<InvalidDataException>(() => NifModelTransformChannelMapping.MapKeyframe(
            new NifTransformInterpolatorView(0, 0, 0, Sentinel, Bits(1f), Sentinel, Sentinel, Bits(1f), 1), data,
            SceneTransformProperty.Rotation, NifModelSquadPolicy.Pc));

        var repeated = KeyframeData(new NifAnimationByteWriter(false)
            .Words(2, 1).Words(QuatKey(0f, 1f, 0f, 0f, 0f)).Words(QuatKey(0f, 1f, 0f, 0f, 0f))
            .Words(0)
            .Words(0)
            .ToArray());
        var blocked = NifModelTransformChannelMapping.MapKeyframe(
            new NifTransformInterpolatorView(0, 0, 0, Sentinel, Sentinel, Sentinel, Sentinel, Bits(1f), 1), repeated,
            SceneTransformProperty.Rotation, NifModelSquadPolicy.Pc);
        Assert.Equal(NifModelCurveBlock.InvalidKeyTimes, blocked.Block);
        Assert.Null(blocked.Channel);
        Assert.False(blocked.IsEuler);
        Assert.Throws<InvalidDataException>(() => NifModelTransformChannelMapping.MapKeyframe(
            new NifTransformInterpolatorView(0, 0, 0, Sentinel, Bits(1f), Sentinel, Sentinel, Bits(1f), 1), repeated,
            SceneTransformProperty.Rotation, NifModelSquadPolicy.Pc));

        // Control: the translation of the same block has no keys and maps as Constant.
        var translation = Channel(NifModelTransformChannelMapping.MapKeyframe(
            new NifTransformInterpolatorView(0, 0, 0, Sentinel, Sentinel, Sentinel, Sentinel, Bits(1f), 1), data,
            SceneTransformProperty.Translation, NifModelSquadPolicy.Pc));
        Assert.Equal(SceneAnimationChannelState.Constant, translation.State);
    }

    /// <summary>An NiTransformData view over bytes laid out as a rotation part, a translation group and a scale group.</summary>
    private static NifKeyframeDataView KeyframeData(byte[] bytes)
    {
        var pos = 0;
        Assert.True(NifKeyGroupReader.TryReadRotationView(bytes, ref pos, bytes.Length, false, Version20207, out var rotation));
        Assert.True(NifKeyGroupReader.TryReadGroupView(bytes, ref pos, bytes.Length, false, NifKeyValueLayout.Vector3,
            out var translations));
        Assert.True(NifKeyGroupReader.TryReadGroupView(bytes, ref pos, bytes.Length, false, NifKeyValueLayout.Float,
            out var scales));
        Assert.Equal(bytes.Length, pos);
        return new NifKeyframeDataView(rotation, translations, scales, bytes.Length);
    }

    /// <summary>The rest pose: translation (1, 2, 3), identity rotation, unit scale.</summary>
    private static SceneTrs Rest()
    {
        return new SceneTrs(RestTranslation, Quaternion.Identity, Vector3.One);
    }

    /// <summary>An NiTransformInterpolator view with the given translation static and #INV_FLT# elsewhere.</summary>
    private static NifTransformInterpolatorView Interpolator(uint x, uint y, uint z)
    {
        return new NifTransformInterpolatorView(x, y, z, Sentinel, Sentinel, Sentinel, Sentinel, Sentinel, 1);
    }

    /// <summary>An NiTransformData view with LINEAR translation keys and no rotation or scale keys.</summary>
    private static NifKeyframeDataView TranslationData(params uint[][] keys)
    {
        var writer = new NifAnimationByteWriter(false).U32(0).U32((uint)keys.Length).U32(1);
        foreach (var key in keys)
        {
            writer.Words(key);
        }

        var bytes = writer.U32(0).ToArray();
        var pos = 0;
        Assert.True(NifKeyGroupReader.TryReadRotationView(bytes, ref pos, bytes.Length, false, Version20207, out var rotation));
        Assert.True(NifKeyGroupReader.TryReadGroupView(bytes, ref pos, bytes.Length, false, NifKeyValueLayout.Vector3,
            out var translations));
        Assert.True(NifKeyGroupReader.TryReadGroupView(bytes, ref pos, bytes.Length, false, NifKeyValueLayout.Float,
            out var scales));
        Assert.Equal(bytes.Length, pos);
        return new NifKeyframeDataView(rotation, translations, scales, bytes.Length);
    }

    /// <summary>The channel of a result that must have mapped.</summary>
    private static NifModelTransformChannel Channel(NifModelTransformChannelResult result)
    {
        Assert.Equal(NifModelCurveBlock.None, result.Block);
        return Assert.IsType<NifModelTransformChannel>(result.Channel);
    }
}
