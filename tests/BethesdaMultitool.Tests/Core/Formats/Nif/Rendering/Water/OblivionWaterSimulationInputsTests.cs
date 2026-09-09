using System.Security.Cryptography;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Water;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Water;

public sealed class OblivionWaterSimulationInputsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FullAuthoredDataPreservesAllTenDistinctFieldsAndIdentity(bool bigEndian)
    {
        var data = OblivionWaterSimulationTestData.Data(bigEndian);
        var source = Assert.IsType<OblivionWaterSimulationInputs>(
            OblivionWaterSimulationInputs.Read(BethesdaGame.Oblivion, 0x18, data, bigEndian));
        Assert.Equal(new OblivionWaterSimulationCoefficients(1, 2, 3, 4, 5), source.Rain);
        Assert.Equal(new OblivionWaterSimulationCoefficients(6, 7, 8, 9, 10), source.Wading);
        Assert.Equal(0x18u, source.WaterFormId);
        Assert.Equal(bigEndian, source.BigEndian);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(data)), source.DataSha256);
        data.AsSpan().Clear();
        Assert.Equal(5f, source.Rain.StartingSize);
        Assert.Equal(10f, source.Wading.StartingSize);
    }

    [Fact]
    public void AuthoredZeroIncludingSignedZeroIsNotMissingOrAConstructorFallback()
    {
        var data = new byte[102];
        OblivionWaterSimulationTestData.Write(data, 0, BitConverter.UInt32BitsToSingle(0x80000000));
        var source = Assert.IsType<OblivionWaterSimulationInputs>(
            OblivionWaterSimulationInputs.Read(BethesdaGame.Oblivion, 0x18, data, false));
        Assert.Equal(0x80000000u, BitConverter.SingleToUInt32Bits(source.Rain.Force));
        Assert.Equal(0f, source.Rain.Dampener);
        Assert.Equal(default, source.Wading);
    }

    [Fact]
    public void NegativeFiniteAuthoredControlsAreRetainedWithoutTuning()
    {
        var data = OblivionWaterSimulationTestData.Data();
        OblivionWaterSimulationTestData.Write(data, 2, -0.5f);
        var source = Assert.IsType<OblivionWaterSimulationInputs>(
            OblivionWaterSimulationInputs.Read(BethesdaGame.Oblivion, 0x18, data, false));
        Assert.Equal(-0.5f, source.Rain.Falloff);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(42)]
    [InlineData(62)]
    [InlineData(86)]
    [InlineData(100)]
    [InlineData(101)]
    [InlineData(103)]
    public void OlderOrMalformedSizeIsNotAFullSimulationCohort(int bytes)
    {
        Assert.Null(OblivionWaterSimulationInputs.Read(BethesdaGame.Oblivion, 0x18, new byte[bytes], false));
    }

    [Theory]
    [InlineData(BethesdaGame.Skyrim)]
    [InlineData(BethesdaGame.FalloutNewVegas)]
    public void SameBytesFromAnotherGameDoNotBecomeTes4Inputs(BethesdaGame game)
    {
        Assert.Null(OblivionWaterSimulationInputs.Read(game, 0x18, OblivionWaterSimulationTestData.Data(), false));
    }

    [Fact]
    public void MissingRecordIdentityIsRejected()
    {
        Assert.Null(OblivionWaterSimulationInputs.Read(BethesdaGame.Oblivion, 0, OblivionWaterSimulationTestData.Data(),
            false));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void EveryNonfiniteAuthoredLaneRejectsTheCohort(int lane)
    {
        var data = OblivionWaterSimulationTestData.Data();
        OblivionWaterSimulationTestData.Write(data, lane, float.NaN);
        Assert.Null(OblivionWaterSimulationInputs.Read(BethesdaGame.Oblivion, 0x18, data, false));
        OblivionWaterSimulationTestData.Write(data, lane, float.PositiveInfinity);
        Assert.Null(OblivionWaterSimulationInputs.Read(BethesdaGame.Oblivion, 0x18, data, false));
    }
}