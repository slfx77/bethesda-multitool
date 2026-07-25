using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     <see cref="NifAnimationDetector" /> block-table classification. The signature gates whether the
///     world decode path attempts a (much more expensive) animation collect at all, so the flags must
///     key off the exact block-type combinations each collector can consume.
/// </summary>
public class NifAnimationDetectorTests
{
    [Fact]
    public void SequencePlusTransformInterpolator_FlagsControllerSequenceTracks()
    {
        var (data, nif) = BuildNif(
            ("NiControllerManager", new byte[4]),
            ("NiControllerSequence", new byte[4]),
            ("NiTransformInterpolator", new byte[4]),
            ("NiTransformData", new byte[4]));

        var signature = NifAnimationDetector.Detect(data, nif);

        Assert.True(signature.HasControllerSequenceTracks);
        Assert.False(signature.HasNodeKeyframeTracks);
        Assert.True(signature.IsAnimatedOrSkinned);
    }

    [Fact]
    public void SequenceWithoutTransformInterpolators_DoesNotFlag()
    {
        // A manager whose sequences drive non-transform controllers (flip/vis/alpha) has no
        // transform rig for the collector — the decode path must not pay for a doomed collect.
        var (data, nif) = BuildNif(
            ("NiControllerSequence", new byte[4]),
            ("NiFloatInterpolator", new byte[4]));

        var signature = NifAnimationDetector.Detect(data, nif);

        Assert.False(signature.HasControllerSequenceTracks);
        Assert.False(signature.IsAnimatedOrSkinned);
    }

    [Fact]
    public void KeyframeController_StillFlagsNodeKeyframeTracks()
    {
        var (data, nif) = BuildNif(("NiKeyframeController", new byte[4]));

        var signature = NifAnimationDetector.Detect(data, nif);

        Assert.True(signature.HasNodeKeyframeTracks);
        Assert.False(signature.HasControllerSequenceTracks);
    }

    private static (byte[] data, NifInfo nif) BuildNif(params (string type, byte[] payload)[] blocks)
    {
        var nif = new NifInfo { IsBigEndian = false, BlockCount = blocks.Length, BinaryVersion = 0x14020007 };
        using var ms = new MemoryStream();
        var offsets = new int[blocks.Length];
        for (var i = 0; i < blocks.Length; i++)
        {
            offsets[i] = (int)ms.Length;
            ms.Write(blocks[i].payload);
        }

        var data = ms.ToArray();
        for (var i = 0; i < blocks.Length; i++)
        {
            nif.Blocks.Add(new BlockInfo
            {
                Index = i,
                TypeName = blocks[i].type,
                DataOffset = offsets[i],
                Size = blocks[i].payload.Length
            });
        }

        return (data, nif);
    }
}