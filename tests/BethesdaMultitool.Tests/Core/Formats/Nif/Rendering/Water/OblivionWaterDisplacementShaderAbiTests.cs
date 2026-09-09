using System.Numerics;
using System.Runtime.InteropServices;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Water;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Water;

public sealed class OblivionWaterDisplacementShaderAbiTests
{
    [Fact]
    public void SixRootRegistersHaveExactByteSizeOrderAndIndependentChannels()
    {
        var value = new OblivionWaterDisplacementConstants(new Vector4(1, 2, 3, 4), new Vector4(5, 6, 7, 8),
            new Vector4(9, 10, 11, 12), new Vector4(13, 14, 15, 16), new Vector4(17, 18, 19, 20),
            new Vector4(21, 22, 23, 24));
        Assert.Equal(96, Marshal.SizeOf<OblivionWaterDisplacementConstants>());
        Assert.Equal(24u, OblivionWaterDisplacementConstants.DwordCount);
        OblivionWaterDisplacementConstants[] values = [value];
        var channels = MemoryMarshal.Cast<OblivionWaterDisplacementConstants, float>(values.AsSpan()).ToArray();
        Assert.Equal(Enumerable.Range(1, 24).Select(index => (float)index), channels);
    }

    [Fact]
    public void RainStampUsesAuthoredScaleAndRecordedClipOffsetAsTexRatio()
    {
        var pass = Pass(OblivionWaterSimulationStage.RainStamp) with { StampScale = 0f, Offset = new Vector2(-1, 1) };
        var constants =
            OblivionWaterDisplacementShaderAbi.Constants(pass,
                new OblivionWaterRecordedRaster(new Vector4(7, 8, 9, 10), 0f));
        Assert.Equal(new Vector4(0, 0, -1, 1), constants.TexRatio0);
        Assert.Equal(Vector4.Zero, constants.Translation0);
    }

    [Fact]
    public void WadingKeepsBothRecordedRowsWithoutMovingThirdComponentTranslationIntoW()
    {
        var stamp = new OblivionWaterRecordedWadingStamp(new Vector4(1, 2, 3, 77), new Vector4(4, 5, 6, 88));
        var constants = OblivionWaterDisplacementShaderAbi.Constants(
            Pass(OblivionWaterSimulationStage.WadingStamp) with { WadingStamp = stamp },
            new OblivionWaterRecordedRaster(new Vector4(7, 8, 9, 10), 0f));
        Assert.Equal(stamp.Row0, constants.Translation0);
        Assert.Equal(stamp.Row1, constants.Translation1);
        Assert.Equal(new Vector4(7, 8, 9, 10), constants.TexRatio0);
    }

    [Theory]
    [InlineData(false, 0f)]
    [InlineData(true, 1f)]
    public void NativeSamplerFlagPreservesWrapVersusClamp(bool clamp, float expected)
    {
        var pass = Pass(OblivionWaterSimulationStage.Recenter) with
        {
            Address = clamp ? OblivionWaterSimulationAddress.Clamp : OblivionWaterSimulationAddress.Wrap,
            Offset = new Vector2(.125f, -.25f)
        };
        var constants =
            OblivionWaterDisplacementShaderAbi.Constants(pass,
                new OblivionWaterRecordedRaster(new Vector4(2, 3, 4, 5), 0f));
        Assert.Equal(new Vector4(expected, 0, 0, 0), constants.Sampling);
        Assert.Equal(new Vector4(0, .125f, -.25f, 0), constants.Surface);
        Assert.Equal(new Vector4(2, 3, 4, 5), constants.TexRatio0);
    }

    [Fact]
    public void ZeroAuthoredDampenerRejectsUndefinedHeightMixWithoutSubstitution()
    {
        var pass = Pass(OblivionWaterSimulationStage.MixedHeight);
        Assert.Throws<ArgumentException>(() => OblivionWaterDisplacementShaderAbi.Constants(pass, default));
        var negativeZero = BitConverter.UInt32BitsToSingle(0x80000000);
        Assert.Throws<ArgumentException>(() =>
            OblivionWaterDisplacementShaderAbi.Constants(pass with { Dampener = negativeZero }, default));
    }

    [Fact]
    public void ZeroDampenerIsValidForNormalAndDoesNotAcquireAMixFallback()
    {
        var constants =
            OblivionWaterDisplacementShaderAbi.Constants(Pass(OblivionWaterSimulationStage.Normal), default);
        Assert.Equal(Vector4.Zero, constants.Simulation);
    }

    [Theory]
    [InlineData(.0078125f)]
    [InlineData(.00390625f)]
    public void HmapNormalKeepsRecordedSampleStepDistinctFromDisplaceLiteral(float recordedStep)
    {
        var constants = OblivionWaterDisplacementShaderAbi.Constants(Pass(OblivionWaterSimulationStage.FftNormal),
            new OblivionWaterRecordedRaster(Vector4.One, recordedStep));
        Assert.Equal(recordedStep, constants.Surface.W);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    public void MissingOrInvalidHmapStepIsNotGuessedFromNormalDimensions(float step)
    {
        Assert.Throws<ArgumentException>(() => OblivionWaterDisplacementShaderAbi.Constants(
            Pass(OblivionWaterSimulationStage.FftNormal), new OblivionWaterRecordedRaster(Vector4.One, step)));
    }

    [Theory]
    [InlineData(0, "vsWadingStamp", "psWadingStamp")]
    [InlineData(1, "vsRainStamp", "psRainStamp")]
    [InlineData(2, "vsQuad", "psWadingEvolution")]
    [InlineData(3, "vsQuad", "psRainEvolution")]
    [InlineData(4, "vsQuad", "psNormal")]
    [InlineData(5, "vsQuad", "psMixedHeight")]
    [InlineData(6, "vsQuad", "psRecenter")]
    [InlineData(7, "vsQuad", "psFftNormal")]
    [InlineData(8, "vsQuad", "psFftAbsoluteHeight")]
    public void EveryScheduledStageHasItsExactProgramFamily(int stage, string vertex, string pixel)
    {
        Assert.Equal((vertex, pixel), OblivionWaterDisplacementShaderAbi.Entries((OblivionWaterSimulationStage)stage));
    }

    [Fact]
    public void EveryRuntimeEntryIsPresentOnceInTheShippedShaderCatalog()
    {
        var expected = Enum.GetValues<OblivionWaterSimulationStage>()
            .Select(OblivionWaterDisplacementShaderAbi.Entries)
            .SelectMany(entries => new[] { (entries.Vertex, "vs_5_1"), (entries.Pixel, "ps_5_1") })
            .Distinct().OrderBy(entry => entry.Item1).ToArray();
        var actual = ShaderPermutations.All.Where(entry => entry.File == OblivionWaterDisplacementShaderAbi.FileName)
            .Select(entry => (entry.EntryPoint, entry.Profile)).OrderBy(entry => entry.Item1).ToArray();
        Assert.Equal(12, actual.Length);
        Assert.Equal(expected, actual);
    }

    private static OblivionWaterSimulationPass Pass(OblivionWaterSimulationStage stage)
    {
        return new OblivionWaterSimulationPass(stage, null, null, new OblivionWaterSimulationResource(1),
            OblivionWaterSimulationAddress.Wrap);
    }
}