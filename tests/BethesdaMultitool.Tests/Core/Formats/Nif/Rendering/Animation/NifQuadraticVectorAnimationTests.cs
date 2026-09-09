using System.Buffers.Binary;
using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Animation;

public sealed class NifQuadraticVectorAnimationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReaderPreservesTangentsAndTheFollowingField(bool bigEndian)
    {
        var data = CreateKeyGroup(bigEndian);
        var position = 0;
        Assert.True(NifKeyGroupReader.TryReadVector3Keys(
            data, ref position, data.Length, bigEndian, out var interpolation, out var keys));

        Assert.Equal(88, position);
        Assert.Equal(0xDEADBEEFu, ReadUInt(data.AsSpan(position), bigEndian));
        Assert.Equal(NifKeyInterpolation.Quadratic, interpolation);
        Assert.All(keys, static key => Assert.True(key.HasQuadraticTangents));
        Assert.Equal(new Vector3(77f), keys[0].Forward);
        Assert.Equal(new Vector3(20f, 40f, 60f), keys[0].Backward);
        Assert.Equal(Vector3.Zero, keys[1].Forward);
        Assert.Equal(new Vector3(88f), keys[1].Backward);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void ReaderRejectsNonFiniteAuthoredTangents(float invalid)
    {
        var data = CreateKeyGroup(false);
        WriteFloat(data.AsSpan(24), invalid, false);
        var position = 0;

        Assert.False(NifKeyGroupReader.TryReadVector3Keys(
            data, ref position, data.Length, false, out _, out var keys));
        Assert.Empty(keys);
    }

    [Fact]
    public void LegacyAndNativeSamplersUseTheAuthoredHandlesWithoutDurationScaling()
    {
        var data = CreateKeyGroup(false);
        var position = 0;
        Assert.True(NifKeyGroupReader.TryReadVector3Keys(
            data, ref position, data.Length, false, out var interpolation, out var keys));
        // Four-second segment at one quarter: 0 + .25*(20 + .25*(-10)) = 4.375.
        var expected = new Vector3(4.375f, 8.75f, 13.125f);
        Assert.Equal(expected, NifTrackSampler.SampleTranslation(keys, 3f, interpolation));
        Assert.Equal(Vector3.Zero, NifTrackSampler.SampleTranslation(keys, 1f, interpolation));
        Assert.Equal(new Vector3(10f, 20f, 30f), NifTrackSampler.SampleTranslation(keys, 7f, interpolation));

        var clip = Clip(keys.Select(static key => new BethesdaViewerVector3Key(
            key.Time, key.Value, key.Forward, key.Backward, key.HasQuadraticTangents)).ToArray());
        var evaluator = new BethesdaViewerAnimationPoseEvaluator([Matrix4x4.Identity], [null], clip);
        var worlds = new Matrix4x4[1];
        evaluator.EvaluateNodeWorlds(3f, worlds);
        Assert.Equal(expected, worlds[0].Translation);
    }

    [Theory]
    [InlineData(0f, 0f, 1.5625f)]
    [InlineData(80f, -80f, 16.5625f)]
    public void AuthoredZeroHandlesAndOvershootAreNotReplacedByLinearOrClamped(
        float outgoing, float incoming, float expected)
    {
        NifVec3Key[] keys =
        [
            new(0f, Vector3.Zero, new Vector3(123f), new Vector3(outgoing), true),
            new(1f, new Vector3(10f), new Vector3(incoming), new Vector3(456f), true)
        ];
        Assert.Equal(new Vector3(expected),
            NifTrackSampler.SampleTranslation(keys, .25f, NifKeyInterpolation.Quadratic));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void ValueOnlyUnsupportedMetadataKeepsItsExistingLabeledApproximation(int rawInterpolation)
    {
        NifVec3Key[] keys = [new(0f, Vector3.Zero), new(1f, new Vector3(10f))];
        Assert.Equal(new Vector3(2.5f),
            NifTrackSampler.SampleTranslation(keys, .25f, (NifKeyInterpolation)rawInterpolation));
    }

    [Fact]
    public void ExternalClockNormalizationPreservesDimensionlessTangents()
    {
        var scene = new BethesdaViewerScene("test.nif", BethesdaViewerScenePurpose.RawNif);
        scene.AddNode("Rock", BethesdaViewerScene.RootNodeIndex, Matrix4x4.Identity, Matrix4x4.Identity,
            BethesdaViewerNodeRole.Skeleton, "Rock");
        var data = CreateKeyGroup(false);
        var position = 0;
        Assert.True(NifKeyGroupReader.TryReadVector3Keys(data, ref position, data.Length, false, out _, out var keys));
        var source = new NifNameTargetedAnimationClip("Curve", 2f, 2f, 6f, NifCycleType.Clamp, null,
        [
            new NifNodeTrack("Rock", 1f, 0f, NifKeyInterpolation.Linear, [],
                NifKeyInterpolation.Quadratic, keys, NifKeyInterpolation.Linear, [])
        ], [], 0);

        var clip = Assert.IsType<BethesdaViewerAnimationClip>(
            BethesdaViewerNameTargetedAnimationAdapter.TryCreateClip(scene, source, false, out var report));
        Assert.Equal(1, report.BoundTrackCount);
        var track = Assert.Single(clip.NodeTracks);
        Assert.Equal(keys[0].Backward, track.TranslationKeys[0].Backward);
        Assert.Equal(0f, track.TranslationKeys[0].Time);
        Assert.Equal(2f, track.TranslationKeys[1].Time);
        var evaluator = new BethesdaViewerAnimationPoseEvaluator(
            scene.Nodes.Select(static node => node.LocalTransform).ToArray(),
            scene.Nodes.Select(static node => node.ParentIndex).ToArray(), clip);
        var worlds = new Matrix4x4[scene.Nodes.Count];
        evaluator.EvaluateNodeWorlds(.5f, worlds);
        Assert.Equal(new Vector3(4.375f, 8.75f, 13.125f), worlds[track.NodeIndex].Translation);
    }

    [Fact]
    public void NativeValidatorRejectsAClaimedNonFiniteTangent()
    {
        var clip = Clip([
            new BethesdaViewerVector3Key(2f, Vector3.Zero, new Vector3(float.NaN), Vector3.Zero, true),
            new BethesdaViewerVector3Key(6f, Vector3.One, Vector3.Zero, Vector3.Zero, true)
        ]);
        Assert.False(BethesdaViewerAnimationValidator.TryValidate(clip, 1, 0, out var error));
        Assert.Contains("malformed", error, StringComparison.Ordinal);
    }

    private static BethesdaViewerAnimationClip Clip(BethesdaViewerVector3Key[] keys)
    {
        return new BethesdaViewerAnimationClip(
            "Curve", 2f, 6f, false,
            [
                new BethesdaViewerNodeAnimationTrack(0, 1f, 0f, BethesdaViewerKeyInterpolation.Linear, [],
                    BethesdaViewerKeyInterpolation.Quadratic, keys, BethesdaViewerKeyInterpolation.Linear, [])
            ], [], []);
    }

    private static byte[] CreateKeyGroup(bool bigEndian)
    {
        var data = new byte[92];
        WriteUInt(data, 2, bigEndian);
        WriteUInt(data.AsSpan(4), 2, bigEndian);
        ReadOnlySpan<float> values =
        [
            2f, 0f, 0f, 0f, 77f, 77f, 77f, 20f, 40f, 60f,
            6f, 10f, 20f, 30f, 0f, 0f, 0f, 88f, 88f, 88f
        ];
        for (var index = 0; index < values.Length; index++)
        {
            WriteFloat(data.AsSpan(8 + index * 4), values[index], bigEndian);
        }

        WriteUInt(data.AsSpan(88), 0xDEADBEEF, bigEndian);
        return data;
    }

    private static uint ReadUInt(ReadOnlySpan<byte> data, bool bigEndian)
    {
        return bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data)
            : BinaryPrimitives.ReadUInt32LittleEndian(data);
    }

    private static void WriteFloat(Span<byte> data, float value, bool bigEndian)
    {
        WriteUInt(data, BitConverter.SingleToUInt32Bits(value), bigEndian);
    }

    private static void WriteUInt(Span<byte> data, uint value, bool bigEndian)
    {
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt32BigEndian(data, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data, value);
        }
    }
}