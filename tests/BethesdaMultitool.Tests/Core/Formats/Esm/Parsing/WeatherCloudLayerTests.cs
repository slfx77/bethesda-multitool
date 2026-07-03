using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
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
    [InlineData("00TX", 0)]   // 0x30
    [InlineData("10TX", 1)]   // 0x31
    [InlineData("30TX", 3)]   // 0x33
    [InlineData("<0TX", 12)]  // 0x3C
    [InlineData("?0TX", 15)]  // 0x3F
    [InlineData("@0TX", 16)]  // 0x40
    [InlineData("A0TX", 17)]  // 0x41
    [InlineData("C0TX", 19)]  // 0x43
    [InlineData("L0TX", 28)]  // 0x4C (last layer)
    public void TryCloudLayerIndex_MapsLegacyFalloutAndSkyrimPlusZeroTxLayers(string signature, int expected)
    {
        Assert.True(MiscEnvironmentHandler.TryCloudLayerIndex(signature, out var layer));
        Assert.Equal(expected, layer);
    }

    [Theory]
    [InlineData("NAM0")]   // weather colors — not a cloud layer
    [InlineData("FNAM")]   // fog distances
    [InlineData("DATA")]   // data block
    [InlineData("LNAM")]   // max cloud layers (count, not a texture)
    [InlineData("M0TX")]   // 0x4D = layer 29, past the 0..28 range
    [InlineData("01TX")]   // wrong middle bytes (not "0TX")
    public void TryCloudLayerIndex_RejectsNonCloudSignatures(string signature)
    {
        Assert.False(MiscEnvironmentHandler.TryCloudLayerIndex(signature, out var layer));
        Assert.Equal(-1, layer);
    }
}
