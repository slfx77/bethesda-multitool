using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Games;
using Xunit;
using static BethesdaMultitool.Tests.Helpers.EsmTestRecordBuilder;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

/// <summary>
///     Pins the TES4 cloud-color routing: Oblivion stores its per-layer cloud colors INSIDE NAM0 —
///     categories 2 "Clouds-Lower" and 9 "Clouds-Upper" (xEdit <c>wbWeatherColors</c> IsTES4 arms) —
///     while FO3+ repurpose those ordinals as Unused and author the per-layer PNAM instead. The parser
///     must surface NAM0[2]/NAM0[9] as cloud layers 0 (CNAM lower) / 1 (DNAM upper) so the sky
///     renderer samples authored time-of-day bands; without the routing every Oblivion cloud layer
///     fell back to the white tint and glowed at night.
/// </summary>
public sealed class OblivionCloudColorRoutingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OblivionNam0_RoutesCloudCategoriesIntoLayers(bool bigEndian)
    {
        // 10 categories × 4 bands (TES4 has no HighNoon/Midnight) × 4 bytes = 160-byte NAM0.
        // Byte value encodes its LE offset so each band is trivially identifiable.
        var nam0 = new byte[160];
        for (var i = 0; i < nam0.Length; i++)
        {
            nam0[i] = (byte)(i + 1);
        }

        if (bigEndian)
        {
            for (var offset = 0; offset < nam0.Length; offset += 4)
            {
                Array.Reverse(nam0, offset, 4);
            }
        }

        var weather = ParseWeather(BethesdaGame.Oblivion, bigEndian,
            ("CNAM", Encoding.ASCII.GetBytes("clouds\\lower.dds\0")),
            ("DNAM", Encoding.ASCII.GetBytes("clouds\\upper.dds\0")),
            ("NAM0", nam0));

        Assert.Equal(10, weather.Colors.Count);
        Assert.Equal(2, weather.CloudLayers.Count);

        // Layer 0 = CNAM lower = NAM0 category 2; layer 1 = DNAM upper = NAM0 category 9 — the
        // exact same parsed instances, not re-decoded copies.
        Assert.Same(weather.Colors[2], weather.CloudLayers[0].Color);
        Assert.Same(weather.Colors[9], weather.CloudLayers[1].Color);

        // Category 2 starts at byte 32 → its sunrise band decodes R=33 G=34 B=35 A=36.
        var lowerSunrise = weather.CloudLayers[0].Color!.Bands.Sunrise;
        Assert.Equal(33, lowerSunrise.R);
        Assert.Equal(34, lowerSunrise.G);
        Assert.Equal(35, lowerSunrise.B);
        // Category 9 starts at byte 144 → its night band (offset +12) decodes R=157.
        Assert.Equal(157, weather.CloudLayers[1].Color!.Bands.Night.R);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FnvNam0_WithoutPnam_LeavesCloudLayerColorsNull(bool bigEndian)
    {
        // FNV's NAM0 ordinals 2/9 are Unused — no PNAM means no cloud colors, exactly as before.
        var nam0 = new byte[240]; // 10 categories × 6 bands × 4 bytes
        for (var i = 0; i < nam0.Length; i++)
        {
            nam0[i] = (byte)(i + 1);
        }

        var weather = ParseWeather(BethesdaGame.FalloutNewVegas, bigEndian,
            ("DNAM", Encoding.ASCII.GetBytes("clouds\\fnvlayer0.dds\0")),
            ("NAM0", nam0));

        Assert.Equal(10, weather.Colors.Count);
        var layer = Assert.Single(weather.CloudLayers);
        Assert.Null(layer.Color);
    }

    private static WeatherRecord ParseWeather(
        BethesdaGame game, bool bigEndian, params (string Signature, byte[] Payload)[] subrecords)
    {
        const uint weatherFormId = 0x0100_2000;
        var bytes = BuildRecordBytes(weatherFormId, "WTHR", bigEndian, subrecords);
        var record = new DetectedMainRecord(
            "WTHR", (uint)(bytes.Length - 24), 0, weatherFormId, 0, bigEndian);
        var context = new RecordParserContext(
            new EsmRecordScanResult { Game = game, MainRecords = [record] },
            null,
            new ByteArrayMemoryAccessor(bytes),
            bytes.Length,
            null);

        return Assert.Single(new MiscEnvironmentHandler(context).ParseWeather());
    }
}