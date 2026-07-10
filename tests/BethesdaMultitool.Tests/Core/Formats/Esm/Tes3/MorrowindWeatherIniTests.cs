using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Tes3;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Tes3;

/// <summary>
///     Locks the Morrowind.ini → synthetic WTHR/CLMT translation (Morrowind authors its whole
///     weather model as INI data): keyframe → NAM0 band mapping, the fog-depth → near/far
///     translation, the climate schedule bytes, and the vanilla-Clear fallback.
/// </summary>
public sealed class MorrowindWeatherIniTests
{
    private const string SampleIni = """
        [Weather]
        Sunrise Time=6
        Sunset Time=18
        Sunrise Duration=2
        Sunset Duration=2
        [Weather Clear]
        Sky Sunrise Color=117,141,164
        Sky Day Color=095,135,203
        Sky Sunset Color=056,089,129
        Sky Night Color=009,010,011
        Fog Sunrise Color=255,189,157
        Fog Day Color=206,227,255
        Fog Sunset Color=255,189,157
        Fog Night Color=009,010,011
        Ambient Sunrise Color=047,066,096
        Ambient Day Color=137,140,160
        Ambient Sunset Color=068,075,096
        Ambient Night Color=032,035,042
        Sun Sunrise Color=242,159,119
        Sun Day Color=255,252,238
        Sun Sunset Color=255,114,079
        Sun Night Color=059,097,176
        Sun Disc Sunset Color=255,189,157
        Land Fog Day Depth=.69
        Land Fog Night Depth=.69
        Wind Speed=.1
        Glare View=1
        Cloud Texture=Tx_Sky_Clear.tga
        [Weather Ashstorm]
        Sky Day Color=124,073,058
        Fog Day Color=124,073,058
        Ambient Day Color=075,049,041
        Sun Day Color=228,139,114
        Land Fog Day Depth=1.1
        Land Fog Night Depth=1.2
        Wind Speed=.8
        Glare View=0
        Cloud Texture=Tx_Sky_Ashstorm.tga
        """;

    [Fact]
    public void SynthesizesWeathers_WithKeyframesMappedToNam0Bands()
    {
        var (weathers, _) = MorrowindWeatherIni.SynthesizeFromIniText(SampleIni);

        Assert.Equal(2, weathers.Count);
        var clear = weathers.Single(w => w.EditorId == "Clear");
        Assert.Equal(MorrowindWeatherIni.WeatherFormIdBase, clear.FormId);

        var sky = clear.Colors[(int)WeatherColorType.SkyUpper];
        Assert.Equal(new WeatherRgba(95, 135, 203, 255), sky.Day);
        Assert.Equal(new WeatherRgba(9, 10, 11, 255), sky.Night);
        // HighNoon/Midnight authored = Day/Night (zero peaks would read as unauthored FNV semantics).
        Assert.Equal(sky.Day, sky.HighNoon);
        Assert.Equal(sky.Night, sky.Midnight);

        // Morrowind has one sky color: dome upper == lower; the warm glow rides the Horizon ← Fog
        // mapping (Fog Sunrise (255,189,157) IS the sunrise horizon).
        Assert.Equal(sky, clear.Colors[(int)WeatherColorType.SkyLower]);
        Assert.Equal(new WeatherRgba(255, 189, 157, 255), clear.Colors[(int)WeatherColorType.Horizon].Sunrise);

        Assert.Equal(new WeatherRgba(137, 140, 160, 255), clear.Colors[(int)WeatherColorType.Ambient].Day);
        Assert.Equal(new WeatherRgba(255, 252, 238, 255), clear.Colors[(int)WeatherColorType.Sunlight].Day);
        // Sun DISC overrides only its sunset band.
        Assert.Equal(new WeatherRgba(255, 189, 157, 255), clear.Colors[(int)WeatherColorType.Sun].Sunset);
        Assert.Equal(new WeatherRgba(255, 252, 238, 255), clear.Colors[(int)WeatherColorType.Sun].Day);

        Assert.Equal(@"textures\Tx_Sky_Clear.tga", clear.CloudLayerTextures.Single());
        Assert.Equal((byte)25, clear.Data!.WindSpeed); // 0.1 × 255
        Assert.Equal((byte)255, clear.Data!.SunGlare);
    }

    [Fact]
    public void FogDepths_TranslateToNearFarDistances()
    {
        var (weathers, _) = MorrowindWeatherIni.SynthesizeFromIniText(SampleIni);

        // Clear: depth .69 → near = 31% of the fog horizon; far = the horizon (the vanilla
        // 7168-unit draw distance scaled to the viewer's scene scale — density ratio preserved).
        var clear = weathers.Single(w => w.EditorId == "Clear").FogDistances;
        Assert.Equal(6, clear.Count);
        Assert.Equal((1f - 0.69f) * 98304f, clear[0], 1f);
        Assert.Equal(98304f, clear[1]);

        // Ashstorm: depth 1.1/1.2 (denser than full) → near clamps to 0 (fog from the camera).
        var ash = weathers.Single(w => w.EditorId == "Ashstorm").FogDistances;
        Assert.Equal(0f, ash[0]);
        Assert.Equal(0f, ash[2]);
    }

    [Fact]
    public void Climate_CarriesScheduleAndUngatedClearDefault()
    {
        var (weathers, climate) = MorrowindWeatherIni.SynthesizeFromIniText(SampleIni);

        // TNAM bytes = hours × 6: sunrise 6→8h, sunset 18→20h.
        Assert.Equal((byte)36, climate.Timing!.SunriseBegin);
        Assert.Equal((byte)48, climate.Timing!.SunriseEnd);
        Assert.Equal((byte)108, climate.Timing!.SunsetBegin);
        Assert.Equal((byte)120, climate.Timing!.SunsetEnd);

        // Clear is first + ungated → ResolveClimateDefaultWeather picks it as the default.
        Assert.Equal(weathers[0].FormId, climate.WeatherTypes[0].WeatherFormId);
        Assert.Equal("Clear", weathers[0].EditorId);
        Assert.All(climate.WeatherTypes, e => Assert.Equal(0u, e.GlobalFormId));
    }

    [Fact]
    public void MissingIni_FallsBackToVanillaClear()
    {
        var (weathers, climate) = MorrowindWeatherIni.SynthesizeFromInstall(
            @"Z:\definitely\not\a\real\install\Data Files\Morrowind.esm");

        var clear = Assert.Single(weathers);
        Assert.Equal("Clear", clear.EditorId);
        Assert.Equal(new WeatherRgba(95, 135, 203, 255), clear.Colors[(int)WeatherColorType.SkyUpper].Day);
        Assert.Equal((byte)36, climate.Timing!.SunriseBegin);
    }

    [Fact]
    public void DegenerateIniText_FallsBackToVanillaClear()
    {
        var (weathers, _) = MorrowindWeatherIni.SynthesizeFromIniText("[General]\nnothing=here");
        Assert.Equal("Clear", Assert.Single(weathers).EditorId);
    }
}
