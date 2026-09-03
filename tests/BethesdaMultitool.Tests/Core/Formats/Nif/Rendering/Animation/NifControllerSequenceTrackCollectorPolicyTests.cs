using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Animation;

public sealed class NifControllerSequenceTrackCollectorPolicyTests
{
    [Fact]
    public void ReadBsxFlags_OblivionReadsValueAfterInlineSizedName()
    {
        var data = new byte[11];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 3);
        "BSX"u8.CopyTo(data.AsSpan(4));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(7), 1);
        var nif = new NifInfo
        {
            BinaryVersion = NifVersions.Gamebryo20004,
            UserVersion = 11,
            BsVersion = 11,
            HasInlineStrings = true,
            IsBigEndian = false
        };
        nif.Blocks.Add(new BlockInfo
        {
            Index = 0,
            TypeName = "BSXFlags",
            DataOffset = 0,
            Size = data.Length
        });

        Assert.Equal(1u, NifControllerSequenceTrackCollector.ReadBsxFlags(data, nif, false));

        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(7), 0);
        Assert.Equal(0u, NifControllerSequenceTrackCollector.ReadBsxFlags(data, nif, false));
    }

    [Fact]
    public void ReadBsxFlags_ModernReadsValueAfterStringTableIndex()
    {
        var data = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(data, 7);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 1);
        var nif = new NifInfo { HasInlineStrings = false, IsBigEndian = false };
        nif.Blocks.Add(new BlockInfo
        {
            Index = 0,
            TypeName = "BSXFlags",
            DataOffset = 0,
            Size = data.Length
        });

        Assert.Equal(1u, NifControllerSequenceTrackCollector.ReadBsxFlags(data, nif, false));
    }

    [Fact]
    public void ReadBsxFlags_MalformedPresentBlockFailsClosed_AbsentBlockRemainsOptional()
    {
        var malformed = new NifInfo { HasInlineStrings = true };
        malformed.Blocks.Add(new BlockInfo
        {
            Index = 0,
            TypeName = "BSXFlags",
            DataOffset = 0,
            Size = 4
        });

        Assert.Equal(
            0u,
            NifControllerSequenceTrackCollector.ReadBsxFlags(new byte[4], malformed, false));
        Assert.Null(NifControllerSequenceTrackCollector.ReadBsxFlags([], new NifInfo(), false));
    }

    [Fact]
    public void SelectIdleNameTargetedSequence_DoesNotAutoplayActivationOnlyManager()
    {
        var open = Clip("Open");
        var specialIdle = Clip("SpecialIdle");

        Assert.Same(
            specialIdle,
            NifControllerSequenceTrackCollector.SelectIdleNameTargetedSequence(
                [open, specialIdle]));
        Assert.Null(
            NifControllerSequenceTrackCollector.SelectIdleNameTargetedSequence(
                [open, Clip("Close")]));
    }

    private static NifNameTargetedAnimationClip Clip(string name)
    {
        return new NifNameTargetedAnimationClip(
            name,
            1f,
            0f,
            1f,
            NifCycleType.Loop,
            null,
            [],
            [],
            0);
    }
}
