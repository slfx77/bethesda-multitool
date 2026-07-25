using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

/// <summary>
///     Pins the WTHR cloud-layer signature → layer-index mapping (<c>MiscEnvironmentHandler.TryCloudLayerIndex</c>),
///     grounded in xEdit's <c>wbWeatherCloudTextures</c>: FO3/FNV use DNAM/CNAM/ANAM/BNAM (layers 0–3);
///     Skyrim/FO4/FO76/SF1 use the <c>?0TX</c> scheme — layer N's signature is <c>(char)(0x30 + N) + "0TX"</c>
///     for layers 0–28 (0x30..0x40 then 'A'..'L'). Recognized structurally by signature, so one parser path
///     covers every game.
/// </summary>
public class WeatherCloudLayerTests
{
    [Theory]
    // Legacy FO3/FNV layers 0–3
    [InlineData("DNAM", 0)]
    [InlineData("CNAM", 1)]
    [InlineData("ANAM", 2)]
    [InlineData("BNAM", 3)]
    // Skyrim+ ?0TX scheme
    [InlineData("00TX", 0)] // 0x30
    [InlineData("10TX", 1)] // 0x31
    [InlineData("30TX", 3)] // 0x33
    [InlineData("<0TX", 12)] // 0x3C
    [InlineData("?0TX", 15)] // 0x3F
    [InlineData("@0TX", 16)] // 0x40
    [InlineData("A0TX", 17)] // 0x41
    [InlineData("C0TX", 19)] // 0x43
    [InlineData("L0TX", 28)] // 0x4C (last layer)
    public void TryCloudLayerIndex_MapsLegacyFalloutAndSkyrimPlusZeroTxLayers(string signature, int expected)
    {
        Assert.True(MiscEnvironmentHandler.TryCloudLayerIndex(signature, out var layer));
        Assert.Equal(expected, layer);
    }

    [Theory]
    [InlineData("NAM0")] // weather colors — not a cloud layer
    [InlineData("FNAM")] // fog distances
    [InlineData("DATA")] // data block
    [InlineData("LNAM")] // max cloud layers (count, not a texture)
    [InlineData("M0TX")] // 0x4D = layer 29, past the 0..28 range
    [InlineData("01TX")] // wrong middle bytes (not "0TX")
    public void TryCloudLayerIndex_RejectsNonCloudSignatures(string signature)
    {
        Assert.False(MiscEnvironmentHandler.TryCloudLayerIndex(signature, out var layer));
        Assert.Equal(-1, layer);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadCloudSpeeds_FnvScalarBytesRemainUnsignedFractions(bool isBigEndian)
    {
        // FNV MemDebug Clouds::Update MOVZX-loads ONAM and multiplies it by 1/255 before applying
        // fWeatherCloudSpeedMax and Sky.fWindSpeed. It never subtracts a midpoint.
        byte[] data = [0, 127, 254, 255];

        var speeds = MiscEnvironmentHandler.ReadCloudSpeeds(data, isBigEndian, BethesdaGame.FalloutNewVegas);

        Assert.Equal(0f, speeds[0]);
        Assert.Equal(127f / 255f, speeds[1]);
        Assert.Equal(254f / 255f, speeds[2]);
        Assert.Equal(1f, speeds[3]);
    }

    [Fact]
    public void CloudLayerSourceIndices_PreserveSparseSkyrimArraySlots()
    {
        var weather = new WeatherRecord
        {
            CloudLayerTextures = ["layer0.dds", "layer15.dds", "layer19.dds", "layer20.dds"],
            CloudLayerSourceIndices = [0, 15, 19, 20]
        };

        Assert.Equal(0, weather.GetCloudLayerSourceIndex(0));
        Assert.Equal(15, weather.GetCloudLayerSourceIndex(1));
        Assert.Equal(19, weather.GetCloudLayerSourceIndex(2));
        Assert.Equal(20, weather.GetCloudLayerSourceIndex(3));
        Assert.Equal(4, weather.GetCloudLayerSourceIndex(4)); // legacy/synthetic dense fallback
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadCloudSpeeds_SkyrimClearLayerZero_MatchesEngineFormula(bool isBigEndian)
    {
        var qnam = MiscEnvironmentHandler.ReadCloudSpeeds([0xA8], isBigEndian, BethesdaGame.Skyrim);
        var rnam = MiscEnvironmentHandler.ReadCloudSpeeds([0x72], isBigEndian, BethesdaGame.Skyrim);

        Assert.Equal(41f / 127f, qnam[0], 6);
        Assert.Equal(-13f / 127f, rnam[0], 6);
        // Clouds::Update's two exact .1 factors produce .01 UV/s at normalized speed 1.
        Assert.Equal(0.0032283465f, qnam[0] * 0.01f, 7);
        Assert.Equal(-0.0010236220f, rnam[0] * 0.01f, 7);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadCloudSpeeds_Fallout4RetainsEveryAuthoredByte(bool isBigEndian)
    {
        // xEdit's shared wbWeatherCloudSpeed declares FO4/FO76 as U8 arrays too. A float decode collapses
        // four independent layer values into one and loses sparse source identities.
        byte[] data = [0, 64, 127, 192, 254, 255];

        var speeds = MiscEnvironmentHandler.ReadCloudSpeeds(data, isBigEndian, BethesdaGame.Fallout4);

        Assert.Equal(data.Length, speeds.Length);
        Assert.Equal(-1f, speeds[0]);
        Assert.Equal(0f, speeds[2]);
        Assert.Equal(1f, speeds[4]);
        Assert.Equal(128f / 127f, speeds[5]);
    }

    [Fact]
    public void TryCloudLayerIndex_OblivionKeepsLowerThenUpperIdentity()
    {
        Assert.True(MiscEnvironmentHandler.TryCloudLayerIndex("CNAM", BethesdaGame.Oblivion, out var lower));
        Assert.True(MiscEnvironmentHandler.TryCloudLayerIndex("DNAM", BethesdaGame.Oblivion, out var upper));
        Assert.Equal(0, lower);
        Assert.Equal(1, upper);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void TryWeatherImageSpaceAdapterIndex_MapsBinarySignatures(int authoredIndex)
    {
        var signature = new string([(char)authoredIndex, 'I', 'A', 'D']);
        Assert.True(MiscEnvironmentHandler.TryWeatherImageSpaceAdapterIndex(signature, out var index));
        Assert.Equal(authoredIndex, index);
    }
}