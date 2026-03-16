using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Nif;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

public sealed class NifAnimationParserTests
{
    [Fact]
    public void ParseIdlePoseOverrides_PrefersIdleSequenceAndFallsBackToFirstKeyframe()
    {
        var data = new byte[256];

        WriteControllerSequence(
            data,
            offset: 0,
            nameIndex: 0,
            numBlocks: 0,
            interpolatorRef: -1,
            nodeNameIndex: -1);
        WriteControllerSequence(
            data,
            offset: 48,
            nameIndex: 1,
            numBlocks: 1,
            interpolatorRef: 2,
            nodeNameIndex: 2);

        WriteSentinelTransformInterpolator(data, 128, dataRef: 3);
        WriteTransformData(data, 168);

        var nif = new NifInfo
        {
            IsBigEndian = false
        };
        nif.Strings.AddRange(["walk", "idle", "Bip01 Head"]);
        nif.Blocks.AddRange(
        [
            new BlockInfo
            {
                Index = 0,
                TypeName = "NiControllerSequence",
                DataOffset = 0,
                Size = 44
            },
            new BlockInfo
            {
                Index = 1,
                TypeName = "NiControllerSequence",
                DataOffset = 48,
                Size = 73
            },
            new BlockInfo
            {
                Index = 2,
                TypeName = "NiTransformInterpolator",
                DataOffset = 128,
                Size = 36
            },
            new BlockInfo
            {
                Index = 3,
                TypeName = "NiTransformData",
                DataOffset = 168,
                Size = 68
            }
        ]);

        var overrides = NifAnimationParser.ParseIdlePoseOverrides(data, nif);

        var pose = Assert.Contains("Bip01 Head", overrides!);
        Assert.True(pose.HasTranslation);
        Assert.Equal(1f, pose.Tx, 3);
        Assert.Equal(2f, pose.Ty, 3);
        Assert.Equal(3f, pose.Tz, 3);
        Assert.True(pose.HasScale);
        Assert.Equal(1.25f, pose.Scale, 3);
    }

    [Fact]
    public void ParseIdlePoseOverrides_MergesInterpolatorBaseTranslationWhenKeyframesOnlyAnimateRotation()
    {
        var data = new byte[256];

        WriteControllerSequence(
            data,
            offset: 0,
            nameIndex: 0,
            numBlocks: 1,
            interpolatorRef: 1,
            nodeNameIndex: 2);

        WriteMixedTransformInterpolator(
            data,
            offset: 96,
            dataRef: 2,
            tx: 16.985f,
            ty: -12.076f,
            tz: 4.451f);
        WriteRotationOnlyTransformData(data, 136);

        var nif = new NifInfo
        {
            IsBigEndian = false
        };
        nif.Strings.AddRange(["idle", "walk", "Weapon"]);
        nif.Blocks.AddRange(
        [
            new BlockInfo
            {
                Index = 0,
                TypeName = "NiControllerSequence",
                DataOffset = 0,
                Size = 73
            },
            new BlockInfo
            {
                Index = 1,
                TypeName = "NiTransformInterpolator",
                DataOffset = 96,
                Size = 36
            },
            new BlockInfo
            {
                Index = 2,
                TypeName = "NiTransformData",
                DataOffset = 136,
                Size = 36
            }
        ]);

        var overrides = NifAnimationParser.ParseIdlePoseOverrides(data, nif);

        var pose = Assert.Contains("Weapon", overrides!);
        Assert.True(pose.HasTranslation);
        Assert.Equal(16.985f, pose.Tx, 3);
        Assert.Equal(-12.076f, pose.Ty, 3);
        Assert.Equal(4.451f, pose.Tz, 3);
        Assert.False(pose.HasScale);
    }

    private static void WriteControllerSequence(
        byte[] data,
        int offset,
        int nameIndex,
        int numBlocks,
        int interpolatorRef,
        int nodeNameIndex)
    {
        WriteInt32(data, offset, nameIndex);
        WriteInt32(data, offset + 4, numBlocks);
        WriteInt32(data, offset + 8, 1);

        if (numBlocks > 0)
        {
            WriteInt32(data, offset + 12, interpolatorRef);
            WriteInt32(data, offset + 21, nodeNameIndex);
        }

        WriteInt32(data, offset + 12 + numBlocks * 29 + 28, -1);
    }

    private static void WriteSentinelTransformInterpolator(byte[] data, int offset, int dataRef)
    {
        WriteFloat(data, offset, float.MaxValue);
        WriteFloat(data, offset + 4, float.MaxValue);
        WriteFloat(data, offset + 8, float.MaxValue);
        WriteFloat(data, offset + 12, float.MaxValue);
        WriteFloat(data, offset + 16, float.MaxValue);
        WriteFloat(data, offset + 20, float.MaxValue);
        WriteFloat(data, offset + 24, float.MaxValue);
        WriteFloat(data, offset + 28, float.MaxValue);
        WriteInt32(data, offset + 32, dataRef);
    }

    private static void WriteMixedTransformInterpolator(
        byte[] data,
        int offset,
        int dataRef,
        float tx,
        float ty,
        float tz)
    {
        WriteFloat(data, offset, tx);
        WriteFloat(data, offset + 4, ty);
        WriteFloat(data, offset + 8, tz);
        WriteFloat(data, offset + 12, float.MaxValue);
        WriteFloat(data, offset + 16, float.MaxValue);
        WriteFloat(data, offset + 20, float.MaxValue);
        WriteFloat(data, offset + 24, float.MaxValue);
        WriteFloat(data, offset + 28, float.MaxValue);
        WriteInt32(data, offset + 32, dataRef);
    }

    private static void WriteTransformData(byte[] data, int offset)
    {
        WriteInt32(data, offset, 1);
        WriteInt32(data, offset + 4, 1);
        WriteFloat(data, offset + 8, 0f);
        WriteFloat(data, offset + 12, 1f);
        WriteFloat(data, offset + 16, 0f);
        WriteFloat(data, offset + 20, 0f);
        WriteFloat(data, offset + 24, 0f);

        WriteInt32(data, offset + 28, 1);
        WriteInt32(data, offset + 32, 1);
        WriteFloat(data, offset + 36, 0f);
        WriteFloat(data, offset + 40, 1f);
        WriteFloat(data, offset + 44, 2f);
        WriteFloat(data, offset + 48, 3f);

        WriteInt32(data, offset + 52, 1);
        WriteInt32(data, offset + 56, 1);
        WriteFloat(data, offset + 60, 0f);
        WriteFloat(data, offset + 64, 1.25f);
    }

    private static void WriteRotationOnlyTransformData(byte[] data, int offset)
    {
        WriteInt32(data, offset, 1);
        WriteInt32(data, offset + 4, 1);
        WriteFloat(data, offset + 8, 0f);
        WriteFloat(data, offset + 12, 1f);
        WriteFloat(data, offset + 16, 0f);
        WriteFloat(data, offset + 20, 0f);
        WriteFloat(data, offset + 24, 0f);

        WriteInt32(data, offset + 28, 0);
        WriteInt32(data, offset + 32, 0);
    }

    private static void WriteInt32(byte[] data, int offset, int value)
        => BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset, 4), value);

    private static void WriteFloat(byte[] data, int offset, float value)
        => WriteInt32(data, offset, BitConverter.SingleToInt32Bits(value));
}
