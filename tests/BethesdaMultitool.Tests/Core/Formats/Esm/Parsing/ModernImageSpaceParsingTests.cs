using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

public sealed class ModernImageSpaceParsingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SplitHnam_DecodesSkyrimSemanticsWithoutClassicOrdinalAliases(bool bigEndian)
    {
        var bytes = Floats(bigEndian, 1, 2, 3, 4, 5, 6, 7, 8, 9);
        var hdr = MiscEnvironmentHandler.ReadModernImageSpaceHdr(bytes, bigEndian, BethesdaGame.Skyrim);

        Assert.Equal(ImageSpaceModernFamily.Skyrim, hdr.Family);
        Assert.Equal(2f, hdr.BloomBlurRadius);
        Assert.Equal(3f, hdr.BloomThreshold);
        Assert.Equal(5f, hdr.ReceiveBloomThreshold);
        Assert.Equal(6f, hdr.White);
        Assert.Equal(9f, hdr.EyeAdaptStrength);
        Assert.Null(hdr.TonemapE);
        Assert.Null(hdr.MiddleGray);
    }

    [Theory]
    [InlineData(BethesdaGame.Fallout4, false)]
    [InlineData(BethesdaGame.Fallout4, true)]
    [InlineData(BethesdaGame.Fallout76, false)]
    [InlineData(BethesdaGame.Fallout76, true)]
    public void SplitHnam_DecodesFo4FamilySemantics(BethesdaGame game, bool bigEndian)
    {
        var bytes = Floats(bigEndian, 1, 2, 3, 4, 5, 6, 7, 8, 9);
        var hdr = MiscEnvironmentHandler.ReadModernImageSpaceHdr(bytes, bigEndian, game);

        Assert.Equal(ImageSpaceModernFamily.Fallout4, hdr.Family);
        Assert.Equal(2f, hdr.TonemapE);
        Assert.Equal(5f, hdr.AutoExposureMax);
        Assert.Equal(6f, hdr.AutoExposureMin);
        Assert.Equal(9f, hdr.MiddleGray);
        Assert.Null(hdr.BloomBlurRadius);
        Assert.Null(hdr.White);
    }

    [Theory]
    [InlineData(BethesdaGame.Skyrim, false)]
    [InlineData(BethesdaGame.Skyrim, true)]
    [InlineData(BethesdaGame.Fallout4, false)]
    [InlineData(BethesdaGame.Fallout4, true)]
    public void PackedEnam_PreservesHdrCinematicAndTint(BethesdaGame game, bool bigEndian)
    {
        var packed = MiscEnvironmentHandler.ReadModernImageSpacePackedData(
            Floats(bigEndian, 1, 2, 3, 4, 5, 6, 7, 0.8f, 0.9f, 1.1f, 0.4f, 0.2f, 0.3f, 0.5f),
            bigEndian, game);

        Assert.True(packed.Hdr.IsLegacyPackedEnam);
        Assert.Equal(6f, packed.Hdr.SunlightScale);
        Assert.Equal(7f, packed.Hdr.SkyScale);
        Assert.Equal(0.8f, packed.Cinematic.Saturation);
        Assert.Equal(1.1f, packed.Cinematic.Contrast);
        Assert.Equal(0.4f, packed.Tint.Amount);
        Assert.Equal(0.5f, packed.Tint.Blue);
        if (game == BethesdaGame.Fallout4)
        {
            Assert.Equal(5f, packed.Hdr.AutoExposureMax);
            Assert.Equal(5f, packed.Hdr.AutoExposureMin);
            Assert.Null(packed.Hdr.MiddleGray);
        }
        else
        {
            Assert.Equal(5f, packed.Hdr.ReceiveBloomThreshold);
            Assert.Null(packed.Hdr.White);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Dnam_PreservesMixedFloatByteU16LayoutAndVignette(bool bigEndian)
    {
        var bytes = new byte[24];
        WriteSingle(bytes.AsSpan(0, 4), 1.25f, bigEndian);
        WriteSingle(bytes.AsSpan(4, 4), 2.5f, bigEndian);
        WriteSingle(bytes.AsSpan(8, 4), 3.75f, bigEndian);
        bytes[12] = 0xAA;
        bytes[13] = 0x55;
        if (bigEndian) BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(14, 2), 0x1234);
        else BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14, 2), 0x1234);
        WriteSingle(bytes.AsSpan(16, 4), 4.5f, bigEndian);
        WriteSingle(bytes.AsSpan(20, 4), 5.5f, bigEndian);

        var dof = MiscEnvironmentHandler.ReadModernImageSpaceDepthOfField(bytes, bigEndian);

        Assert.Equal(1.25f, dof.Strength);
        Assert.Equal(0xAA, dof.Unused0);
        Assert.Equal(0x55, dof.Unused1);
        Assert.Equal(0x1234, dof.SkyBlurRadius);
        Assert.Equal(4.5f, dof.VignetteRadius);
        Assert.Equal(5.5f, dof.VignetteStrength);
        Assert.Equal(bytes, dof.RawData);
    }

    private static byte[] Floats(bool bigEndian, params float[] values)
    {
        var result = new byte[values.Length * 4];
        for (var i = 0; i < values.Length; i++)
            WriteSingle(result.AsSpan(i * 4, 4), values[i], bigEndian);
        return result;
    }

    private static void WriteSingle(Span<byte> target, float value, bool bigEndian)
    {
        if (bigEndian) BinaryPrimitives.WriteSingleBigEndian(target, value);
        else BinaryPrimitives.WriteSingleLittleEndian(target, value);
    }
}