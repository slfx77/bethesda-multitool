using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Parsing.Handlers;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Parsing;

/// <summary>
///     Pins the FNV WTHR NAM0 layout: TEN color categories, each a 24-byte "Time of Day Colors" struct of
///     SIX RGBA bands (Sunrise/Day/Sunset/Night/HighNoon/Midnight) per the fopdoc FalloutNV WTHR
///     definition. The original parser used a 16-byte (4-band) stride, which drifted 8 bytes per category
///     and inverted every sky/sun color after category 0 (horizon black at noon / white at night). These
///     tests are the regression guard for that stride.
/// </summary>
public class WeatherColorParsingTests
{
    // Each band is 4 bytes laid out [R,G,B,A] (little-endian read). Two full 24-byte categories.
    private static byte[] BuildTwoCategoryBuffer()
    {
        var data = new byte[48];
        for (var i = 0; i < 48; i++)
        {
            // byte value encodes (band offset) so each band is trivially identifiable.
            data[i] = (byte)(i + 1);
        }

        return data;
    }

    [Fact]
    public void ReadWeatherColors_Uses24ByteStride_SixBandsPerCategory()
    {
        var colors = MiscEnvironmentHandler.ReadWeatherColors(BuildTwoCategoryBuffer(), isBigEndian: false);

        Assert.Equal(2, colors.Count);

        // Category 0: bands at byte offsets 0,4,8,12,16,20.
        Assert.Equal(new WeatherRgba(1, 2, 3, 4), colors[0].Sunrise);
        Assert.Equal(new WeatherRgba(5, 6, 7, 8), colors[0].Day);
        Assert.Equal(new WeatherRgba(9, 10, 11, 12), colors[0].Sunset);
        Assert.Equal(new WeatherRgba(13, 14, 15, 16), colors[0].Night);
        Assert.Equal(new WeatherRgba(17, 18, 19, 20), colors[0].HighNoon);
        Assert.Equal(new WeatherRgba(21, 22, 23, 24), colors[0].Midnight);

        // Category 1 must START at byte 24 (24-byte stride). A regressed 16-byte stride would instead
        // read category 0's HighNoon bytes (17,18,19,20) here — this assertion is the stride guard.
        Assert.Equal(new WeatherRgba(25, 26, 27, 28), colors[1].Sunrise);
        Assert.NotEqual(colors[0].HighNoon, colors[1].Sunrise);
    }

    [Fact]
    public void ReadWeatherColors_FullFnvNam0_DecodesTenCategories()
    {
        // FNV NAM0 is 240 bytes = 10 categories × 24 bytes (NOT 15 × 16).
        var colors = MiscEnvironmentHandler.ReadWeatherColors(new byte[240], isBigEndian: false);
        Assert.Equal(10, colors.Count);
    }

    [Fact]
    public void ReadWeatherColors_TrailingPartialCategory_Ignored()
    {
        // 24 full bytes + 10 trailing bytes → only the one complete category is produced.
        var colors = MiscEnvironmentHandler.ReadWeatherColors(new byte[34], isBigEndian: false);
        Assert.Single(colors);
    }
}
