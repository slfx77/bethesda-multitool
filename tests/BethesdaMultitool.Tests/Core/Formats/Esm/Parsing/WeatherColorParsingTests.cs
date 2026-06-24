using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

/// <summary>
///     Pins the WTHR NAM0/PNAM weather-color layout across the FO3↔FNV format gap. FNV stores TEN color
///     categories of SIX RGBA bands each (24 bytes — Sunrise/Day/Sunset/Night/HighNoon/Midnight); FO3 (the
///     format the earliest crash dumps carry) stores only FOUR bands (16 bytes), since FNV added High Noon
///     + Midnight. The band count is detected structurally from NAM0's length (10 categories) rather than
///     assumed, so FO3-era records don't drift 8 bytes per category (horizon black at noon / white at
///     night). These tests guard both the stride and the structural detection.
/// </summary>
public class WeatherColorParsingTests
{
    // A subrecord = 4-byte signature + 2-byte LE length + payload, matching EsmSubrecordUtils.
    private static byte[] BuildSubrecord(string sig, int payloadLength)
    {
        var buffer = new byte[6 + payloadLength];
        Encoding.ASCII.GetBytes(sig).CopyTo(buffer, 0);
        buffer[4] = (byte)(payloadLength & 0xFF);
        buffer[5] = (byte)((payloadLength >> 8) & 0xFF);
        return buffer;
    }

    // Each band is 4 bytes laid out [R,G,B,A] (little-endian read). N full categories at the given stride.
    private static byte[] BuildCategoryBuffer(int categories, int bandsPerEntry)
    {
        var data = new byte[categories * bandsPerEntry * 4];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i + 1); // byte value encodes its offset so bands are trivially identifiable.
        }

        return data;
    }

    [Fact]
    public void ReadWeatherColors_SixBands_Uses24ByteStride()
    {
        var colors = MiscEnvironmentHandler.ReadWeatherColors(BuildCategoryBuffer(2, 6), false, 6);

        Assert.Equal(2, colors.Count);
        Assert.Equal(new WeatherRgba(1, 2, 3, 4), colors[0].Sunrise);
        Assert.Equal(new WeatherRgba(17, 18, 19, 20), colors[0].HighNoon);
        Assert.Equal(new WeatherRgba(21, 22, 23, 24), colors[0].Midnight);

        // Category 1 must START at byte 24 (24-byte stride). A regressed 16-byte stride would instead
        // read category 0's HighNoon bytes (17,18,19,20) here — this assertion is the stride guard.
        Assert.Equal(new WeatherRgba(25, 26, 27, 28), colors[1].Sunrise);
        Assert.NotEqual(colors[0].HighNoon, colors[1].Sunrise);
    }

    [Fact]
    public void ReadWeatherColors_FourBands_Uses16ByteStride_AndBackfillsNoonMidnight()
    {
        // FO3: 4 bands, 16-byte stride. HighNoon/Midnight don't exist → fall back to Day/Night.
        var colors = MiscEnvironmentHandler.ReadWeatherColors(BuildCategoryBuffer(2, 4), false, 4);

        Assert.Equal(2, colors.Count);
        Assert.Equal(new WeatherRgba(1, 2, 3, 4), colors[0].Sunrise);
        Assert.Equal(new WeatherRgba(5, 6, 7, 8), colors[0].Day);
        Assert.Equal(new WeatherRgba(13, 14, 15, 16), colors[0].Night);
        Assert.Equal(colors[0].Day, colors[0].HighNoon);
        Assert.Equal(colors[0].Night, colors[0].Midnight);

        // Category 1 starts at byte 16 (16-byte stride), not 24.
        Assert.Equal(new WeatherRgba(17, 18, 19, 20), colors[1].Sunrise);
    }

    [Fact]
    public void ReadWeatherColors_FullFnvNam0_DecodesTenCategories()
    {
        var colors = MiscEnvironmentHandler.ReadWeatherColors(new byte[240], false, 6);
        Assert.Equal(10, colors.Count);
    }

    [Fact]
    public void ReadWeatherColors_FullFo3Nam0_DecodesTenCategories()
    {
        // FO3 NAM0 is 160 bytes = 10 categories × 16 bytes (4 bands).
        var colors = MiscEnvironmentHandler.ReadWeatherColors(new byte[160], false, 4);
        Assert.Equal(10, colors.Count);
    }

    [Theory]
    [InlineData(240, 6)] // FNV NAM0: 10 × 24
    [InlineData(160, 4)] // FO3 NAM0: 10 × 16
    public void DetectWeatherBands_ReadsBandCountFromNam0Length(int nam0Payload, int expectedBands)
    {
        var record = BuildSubrecord("NAM0", nam0Payload);
        Assert.Equal(expectedBands, MiscEnvironmentHandler.DetectWeatherBands(record, record.Length, false));
    }

    [Fact]
    public void DetectWeatherBands_NoNam0_DefaultsToFnvSixBands()
    {
        var record = BuildSubrecord("FNAM", 24);
        Assert.Equal(6, MiscEnvironmentHandler.DetectWeatherBands(record, record.Length, false));
    }

    // --- Modern (Skyrim/FO4/FO76/SF1) NAM0 layout -------------------------------------------------------
    // These games use a form-versioned wbWeatherTimeOfDay per category: 4 RGBA bands (16B) base, widening
    // to 8 (32B) for FO4/FO76/SF1 at form version 111. Category COUNT is form-version dependent (10→19),
    // so the stride is taken as given rather than derived from NAM0's length.

    [Theory]
    [InlineData(BethesdaGame.Fallout4, 131, 32)]   // FO4 retail: 8 bands
    [InlineData(BethesdaGame.Fallout4, 110, 16)]   // FO4 pre-111: 4 bands
    [InlineData(BethesdaGame.Fallout76, 120, 32)]
    [InlineData(BethesdaGame.Starfield, 150, 32)]
    [InlineData(BethesdaGame.Skyrim, 43, 16)]      // Skyrim never widens — 4 bands regardless of version
    [InlineData(BethesdaGame.Skyrim, 200, 16)]
    public void ModernWeatherStride_WidensOnlyForFo4PlusAtVersion111(BethesdaGame game, int formVersion, int expectedStride)
    {
        Assert.Equal(expectedStride, MiscEnvironmentHandler.ModernWeatherStride(game, formVersion));
    }

    [Fact]
    public void ReadWeatherColorsModern_EightBandStride_ReadsFourBaseBands_BackfillsNoonMidnight()
    {
        // FO4 v111+: 32-byte stride (8 bands), but only Sunrise/Day/Sunset/Night map to our model; the
        // extra Early/Late Sunrise/Sunset bands are skipped and HighNoon/Midnight fall back to Day/Night.
        var colors = MiscEnvironmentHandler.ReadWeatherColorsModern(BuildCategoryBuffer(2, 8), false, 32);

        Assert.Equal(2, colors.Count);
        Assert.Equal(new WeatherRgba(1, 2, 3, 4), colors[0].Sunrise);
        Assert.Equal(new WeatherRgba(5, 6, 7, 8), colors[0].Day);
        Assert.Equal(new WeatherRgba(9, 10, 11, 12), colors[0].Sunset);
        Assert.Equal(new WeatherRgba(13, 14, 15, 16), colors[0].Night);
        Assert.Equal(colors[0].Day, colors[0].HighNoon);
        Assert.Equal(colors[0].Night, colors[0].Midnight);

        // Category 1 must START at byte 32 (32-byte stride), not 16 — this is the stride guard that a
        // regressed 16-byte read (the FO4 "red sky" bug) would fail by landing on category 0's Early
        // Sunrise band (17,18,19,20).
        Assert.Equal(new WeatherRgba(33, 34, 35, 36), colors[1].Sunrise);
        Assert.NotEqual(new WeatherRgba(17, 18, 19, 20), colors[1].Sunrise);
    }

    [Fact]
    public void ReadWeatherColorsModern_FourBandStride_Uses16ByteStride()
    {
        // FO4 pre-111 / Skyrim: 16-byte stride (4 bands).
        var colors = MiscEnvironmentHandler.ReadWeatherColorsModern(BuildCategoryBuffer(2, 4), false, 16);

        Assert.Equal(2, colors.Count);
        Assert.Equal(new WeatherRgba(1, 2, 3, 4), colors[0].Sunrise);
        Assert.Equal(new WeatherRgba(13, 14, 15, 16), colors[0].Night);
        Assert.Equal(colors[0].Day, colors[0].HighNoon);
        Assert.Equal(new WeatherRgba(17, 18, 19, 20), colors[1].Sunrise);
    }

    [Fact]
    public void ReadWeatherColorsModern_Fo4RetailNam0_Decodes19Categories()
    {
        // FO4 retail NAM0 is 608 bytes = 19 categories × 32 bytes (verified against Fallout4.esm
        // 0x0024A3C1). Only categories 0–9 are consumed by the atmosphere renderer; the rest are harmless.
        var colors = MiscEnvironmentHandler.ReadWeatherColorsModern(new byte[608], false, 32);
        Assert.Equal(19, colors.Count);
    }
}
