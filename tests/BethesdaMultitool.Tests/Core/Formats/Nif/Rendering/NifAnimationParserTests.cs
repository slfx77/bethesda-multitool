using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Bsa.Extraction;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

public sealed class NifAnimationParserTests
{
    [Fact]
    public void ParseIdlePoseOverrides_PrefersIdleSequenceAndFallsBackToFirstKeyframe()
    {
        var data = new byte[256];

        WriteControllerSequence(
            data,
            0,
            0,
            0,
            -1,
            -1);
        WriteControllerSequence(
            data,
            48,
            1,
            1,
            2,
            2);

        WriteSentinelTransformInterpolator(data, 128, 3);
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
            0,
            0,
            1,
            1,
            2);

        WriteMixedTransformInterpolator(
            data,
            96,
            2,
            16.985f,
            -12.076f,
            4.451f);
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

    [Fact]
    public void ParseIdlePoseOverrides_OblivionPaletteSequenceSelectsInlineIdleAndResolvesNodeName()
    {
        var data = new byte[320];
        var walkSequenceSize = WriteOblivionControllerSequence(
            data,
            0,
            "Walk",
            0,
            -1,
            3,
            0,
            "Bip01");
        var sequenceSize = WriteOblivionControllerSequence(
            data,
            64,
            "Idle",
            1,
            2,
            3,
            0,
            "Bip01");
        WriteBaseTransformInterpolator(data, 160, 1f, 2f, 3f);
        var paletteSize = WriteStringPalette(
            data,
            208,
            "Bip01 Head\0NiTransformController\0");

        var nif = new NifInfo
        {
            BinaryVersion = NifVersions.Gamebryo20004,
            UserVersion = 11,
            BsVersion = 11,
            IsBigEndian = false,
            HasInlineStrings = true
        };
        nif.Blocks.AddRange(
        [
            new BlockInfo
            {
                Index = 0,
                TypeName = "NiControllerSequence",
                DataOffset = 0,
                Size = walkSequenceSize
            },
            new BlockInfo
            {
                Index = 1,
                TypeName = "NiControllerSequence",
                DataOffset = 64,
                Size = sequenceSize
            },
            new BlockInfo
            {
                Index = 2,
                TypeName = "NiTransformInterpolator",
                DataOffset = 160,
                Size = 36
            },
            new BlockInfo
            {
                Index = 3,
                TypeName = "NiStringPalette",
                DataOffset = 208,
                Size = paletteSize
            }
        ]);
        nif.BlockNames[0] = "Walk";
        nif.BlockNames[1] = "Idle";

        var overrides = NifAnimationParser.ParseIdlePoseOverrides(data, nif);

        var pose = Assert.Contains("Bip01 Head", overrides!);
        Assert.True(pose.HasTranslation);
        Assert.Equal(1f, pose.Tx, 3);
        Assert.Equal(2f, pose.Ty, 3);
        Assert.Equal(3f, pose.Tz, 3);
        Assert.True(pose.HasScale);
        Assert.Equal(1f, pose.Scale, 3);
    }

    [Fact]
    [Trait("Category", TestCategories.BucketB)]
    public void ParseIdlePoseOverrides_RetailOblivionIdle_DecodesPaletteAndSplineTracks()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var archivePath = RealAssetPaths.SteamGameFile(
            "Oblivion",
            @"Data\Oblivion - Meshes.bsa");
        Assert.SkipUnless(
            archivePath is not null && File.Exists(archivePath),
            RealAssetPaths.SkipMessage("Oblivion - Meshes.bsa"));

        using var extractor = new BsaExtractor(archivePath!);
        var file = extractor.Archive.AllFiles.First(record =>
            string.Equals(
                record.FullPath,
                @"meshes\characters\_male\idle.kf",
                StringComparison.OrdinalIgnoreCase));
        var data = extractor.ExtractFile(file);
        var nif = Assert.IsType<NifInfo>(NifParser.Parse(data));

        var overrides = NifAnimationParser.ParseIdlePoseOverrides(data, nif);

        Assert.NotNull(overrides);
        Assert.Equal(71, overrides.Count);
        var pelvis = Assert.Contains("Bip01 Pelvis", overrides);
        Assert.Equal(0.42f, pelvis.Tx, 2);
        var leftUpperArm = Assert.Contains("Bip01 L UpperArm", overrides);
        Assert.InRange(MathF.Abs(leftUpperArm.Rotation.X), 0.30f, 0.35f);
        Assert.Contains("Bip01 R UpperArm", overrides);
        Assert.Contains("Bip01 L Thigh", overrides);
        Assert.Contains("Bip01 R Thigh", overrides);
        Assert.DoesNotContain("Bip01", overrides);
        Assert.DoesNotContain("Bip01 NonAccum", overrides);
        Assert.DoesNotContain("Bow:0", overrides);
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

    private static int WriteOblivionControllerSequence(
        byte[] data,
        int offset,
        string name,
        int numBlocks,
        int interpolatorRef,
        int paletteRef,
        int nodeNameOffset,
        string accumRoot)
    {
        var pos = offset;
        pos = WriteSizedString(data, pos, name);
        WriteInt32(data, pos, numBlocks);
        WriteInt32(data, pos + 4, 1);
        pos += 8;

        for (var index = 0; index < numBlocks; index++)
        {
            WriteInt32(data, pos, interpolatorRef);
            WriteInt32(data, pos + 4, -1);
            data[pos + 8] = 20;
            WriteInt32(data, pos + 9, paletteRef);
            WriteInt32(data, pos + 13, nodeNameOffset);
            WriteInt32(data, pos + 17, -1);
            WriteInt32(data, pos + 21, "Bip01 Head\0".Length);
            WriteInt32(data, pos + 25, -1);
            WriteInt32(data, pos + 29, -1);
            pos += 33;
        }

        WriteFloat(data, pos, 1f);
        WriteInt32(data, pos + 4, -1);
        WriteInt32(data, pos + 8, 0);
        WriteFloat(data, pos + 12, 1f);
        WriteFloat(data, pos + 16, 0f);
        WriteFloat(data, pos + 20, 1f);
        WriteInt32(data, pos + 24, -1);
        pos += 28;
        pos = WriteSizedString(data, pos, accumRoot);
        WriteInt32(data, pos, paletteRef);
        pos += 4;
        return pos - offset;
    }

    private static void WriteBaseTransformInterpolator(
        byte[] data,
        int offset,
        float tx,
        float ty,
        float tz)
    {
        WriteFloat(data, offset, tx);
        WriteFloat(data, offset + 4, ty);
        WriteFloat(data, offset + 8, tz);
        WriteFloat(data, offset + 12, 1f);
        WriteFloat(data, offset + 16, 0f);
        WriteFloat(data, offset + 20, 0f);
        WriteFloat(data, offset + 24, 0f);
        WriteFloat(data, offset + 28, 1f);
        WriteInt32(data, offset + 32, -1);
    }

    private static int WriteStringPalette(byte[] data, int offset, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        WriteInt32(data, offset, bytes.Length);
        bytes.CopyTo(data, offset + 4);
        WriteInt32(data, offset + 4 + bytes.Length, bytes.Length);
        return bytes.Length + 8;
    }

    private static int WriteSizedString(byte[] data, int offset, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        WriteInt32(data, offset, bytes.Length);
        bytes.CopyTo(data, offset + 4);
        return offset + 4 + bytes.Length;
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
    {
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset, 4), value);
    }

    private static void WriteFloat(byte[] data, int offset, float value)
    {
        WriteInt32(data, offset, BitConverter.SingleToInt32Bits(value));
    }
}
