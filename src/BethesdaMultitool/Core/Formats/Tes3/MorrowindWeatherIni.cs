using System.Globalization;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

namespace BethesdaMultitool.Core.Formats.Tes3;

/// <summary>
///     Synthesizes WTHR/CLMT-equivalent records for Morrowind from <c>Morrowind.ini</c> — unlike every
///     TES4-era game, Morrowind authors its ENTIRE weather/sky model as INI data (per-weather
///     Sky/Fog/Ambient/Sun colors × Sunrise/Day/Sunset/Night keyframes, fog depths, cloud texture,
///     wind/glare), not as records. Mapping those keyframes onto the NAM0 band model lets the whole
///     existing atmosphere stack — <c>AtmosphereState</c>'s windowed band blend, the weather picker,
///     scene captures, the 2D map lighting — work for Morrowind unchanged. Derivation + TO-CONFIRM
///     items: <c>docs/research/morrowind_atmosphere_water_model.md</c>.
///     <para>
///         KNOWN SIMPLIFICATION: Morrowind gives each channel its OWN pre/post transition window
///         around the sunrise/sunset anchors (<c>Sky Pre-Sunrise Time=.5</c> vs
///         <c>Ambient Post-Sunrise Time=2</c>, …); the NAM0 model blends every channel over the one
///         shared climate window. The shared window is built from <c>Sunrise/Sunset Time + Duration</c>;
///         the per-channel offsets (±0.5–2h) are second-order and flagged for the OpenMW-oracle pass.
///     </para>
/// </summary>
internal static class MorrowindWeatherIni
{
    /// <summary>Synthetic FormID base for the INI weathers, in the cross-plugin SHARED range
    /// (<see cref="Tes3FormIdScheme.SharedNamespaceByte" /> high byte — the INI is install-level data,
    /// not per-plugin). Disjoint from the shared exterior worldspace (0xFF000000).</summary>
    public const uint WeatherFormIdBase = 0xFF00D000u;

    /// <summary>Synthetic FormID of the one INI-derived climate.</summary>
    public const uint ClimateFormId = 0xFF00DFFFu;

    /// <summary>Fog horizon (world units) used to translate the INI's per-weather
    /// <c>Land Fog … Depth</c> densities into the viewer's linear near/far fog: near =
    /// (1 − depth) × far, far = this. The densities are authored against Morrowind's vanilla
    /// 7168-unit draw distance; using that literally white-washes a viewer that renders 16+ cells,
    /// so the horizon is scaled to the viewer's scene scale (= its default fog far) while each
    /// weather's DENSITY RATIO — the authored look — is preserved (Clear fogs the far 69% of the
    /// horizon; Ashstorm ≥1 fogs from the camera). The near = (1 − depth) × far reading of the
    /// depth semantics is TO-CONFIRM against an OpenMW render oracle (see the derivation doc).</summary>
    private const float FogViewDistance = 98304f;

    /// <summary>The ten vanilla weather section names, in a fixed order so synthetic FormIDs are
    /// stable across runs (Snow/Blizzard are Bloodmoon's and may be absent from a base install).</summary>
    private static readonly string[] WeatherNames =
    [
        "Clear", "Cloudy", "Foggy", "Overcast", "Rain",
        "Thunderstorm", "Ashstorm", "Blight", "Snow", "Blizzard",
    ];

    /// <summary>
    ///     Synthesizes the weather + climate records for the install that
    ///     <paramref name="pluginPath" /> belongs to: <c>Morrowind.ini</c> sits beside the install's
    ///     <c>Data Files</c> directory. Falls back to embedded vanilla-Clear constants when the INI
    ///     is missing/unreadable (the values are identical in every unmodded install).
    /// </summary>
    public static (List<WeatherRecord> Weathers, ClimateRecord Climate) SynthesizeFromInstall(string? pluginPath)
    {
        try
        {
            var dataDir = Path.GetDirectoryName(pluginPath);
            if (dataDir is not null)
            {
                var iniPath = Path.GetFullPath(Path.Combine(dataDir, "..", "Morrowind.ini"));
                if (File.Exists(iniPath))
                {
                    return SynthesizeFromIniText(File.ReadAllText(iniPath));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Unreadable INI — fall through to the vanilla constants.
        }

        return SynthesizeFromIniText(VanillaClearFallback);
    }

    /// <summary>Synthesizes from raw INI text (separated from the file probe for testability).</summary>
    public static (List<WeatherRecord> Weathers, ClimateRecord Climate) SynthesizeFromIniText(string iniText)
    {
        var sections = ParseSections(iniText);
        var schedule = sections.TryGetValue("Weather", out var sched) ? sched : [];

        var weathers = new List<WeatherRecord>();
        for (var i = 0; i < WeatherNames.Length; i++)
        {
            if (sections.TryGetValue($"Weather {WeatherNames[i]}", out var section))
            {
                weathers.Add(BuildWeather(WeatherNames[i], WeatherFormIdBase + (uint)i, section));
            }
        }

        // A degenerate INI (no weather sections at all) still yields the vanilla Clear so the
        // atmosphere always has an authored palette.
        if (weathers.Count == 0 && !ReferenceEquals(iniText, VanillaClearFallback))
        {
            return SynthesizeFromIniText(VanillaClearFallback);
        }

        return (weathers, BuildClimate(schedule, weathers));
    }

    private static WeatherRecord BuildWeather(string name, uint formId, Dictionary<string, string> s)
    {
        var sky = ReadKeyframes(s, "Sky");
        var fog = ReadKeyframes(s, "Fog");
        var ambient = ReadKeyframes(s, "Ambient");
        var sun = ReadKeyframes(s, "Sun");

        // Sun disc: only a Sunset color is authored (`Sun Disc Sunset Color`); other bands reuse the
        // Sun channel (the engine tints the disc by the sun color outside the sunset window).
        var sunDisc = sun;
        if (ReadColor(s, "Sun Disc Sunset Color") is { } discSunset)
        {
            sunDisc = new WeatherColor(sun.Bands with { Sunset = discSunset });
        }

        // NAM0 category mapping (see WeatherColorType): Morrowind authors ONE sky color (no
        // upper/lower split) → SkyUpper = SkyLower = Sky; the dome's horizon warmth comes from the
        // Horizon band ← Fog (Clear's Fog Sunrise = (255,189,157) IS the warm sunrise glow, exactly
        // what AtmosphereState folds into the dome horizon when the sun is low).
        var colors = new WeatherColor[10];
        colors[(int)WeatherColorType.SkyUpper] = sky;
        colors[(int)WeatherColorType.Fog] = fog;
        colors[(int)WeatherColorType.Unused2] = Solid(default);
        colors[(int)WeatherColorType.Ambient] = ambient;
        colors[(int)WeatherColorType.Sunlight] = sun;
        colors[(int)WeatherColorType.Sun] = sunDisc;
        colors[(int)WeatherColorType.Stars] = Solid(new WeatherRgba(255, 255, 255, 255));
        colors[(int)WeatherColorType.SkyLower] = sky;
        colors[(int)WeatherColorType.Horizon] = fog;
        colors[(int)WeatherColorType.Unused9] = Solid(default);

        var dayDepth = ReadFloat(s, "Land Fog Day Depth") ?? 0.69f;
        var nightDepth = ReadFloat(s, "Land Fog Night Depth") ?? dayDepth;

        var cloudTexture = s.TryGetValue("Cloud Texture", out var cloud) && !string.IsNullOrWhiteSpace(cloud)
            ? Path.Combine("textures", cloud.Trim())
            : null;

        return new WeatherRecord
        {
            FormId = formId,
            EditorId = name,
            Colors = colors,
            // FNAM shape: Day Near/Far, Night Near/Far, Day/Night power. The INI authors a fog DEPTH
            // (density); near = (1 − depth) × viewDistance reproduces "deeper fog starts closer"
            // (Ashstorm 1.1 → clamps to 0 = fog from the camera). Linear power.
            FogDistances =
            [
                Math.Max(0f, (1f - dayDepth) * FogViewDistance), FogViewDistance,
                Math.Max(0f, (1f - nightDepth) * FogViewDistance), FogViewDistance,
                1f, 1f,
            ],
            CloudLayerTextures = cloudTexture is null ? [] : [cloudTexture],
            Data = new WeatherData
            {
                // Engine wind scale: weather wind-speed byte / 255 (the SpeedTree wind profile's
                // input). Glare View gates the sun-glare fader per weather.
                WindSpeed = (byte)Math.Clamp((ReadFloat(s, "Wind Speed") ?? 0f) * 255f, 0f, 255f),
                SunGlare = (byte)Math.Clamp((ReadFloat(s, "Glare View") ?? 0f) * 255f, 0f, 255f),
            },
        };
    }

    private static ClimateRecord BuildClimate(Dictionary<string, string> schedule, List<WeatherRecord> weathers)
    {
        var sunriseTime = ReadFloat(schedule, "Sunrise Time") ?? 6f;
        var sunsetTime = ReadFloat(schedule, "Sunset Time") ?? 18f;
        var sunriseDuration = ReadFloat(schedule, "Sunrise Duration") ?? 2f;
        var sunsetDuration = ReadFloat(schedule, "Sunset Duration") ?? 2f;

        // CLMT TNAM stores hours × 6 (10-minute units — the decompiled Sky::GetSunriseBegin scale).
        static byte Hours(float h) => (byte)Math.Clamp(MathF.Round(h * 6f), 0f, 255f);

        return new ClimateRecord
        {
            FormId = ClimateFormId,
            EditorId = "MorrowindIniClimate",
            // Engine-hardcoded sun assets (strings in Morrowind.exe beside their "Sun texture not
            // found" errors: Textures\tx_sun_05.tga + tx_sun_flash_grey_05.tga); the BSA ships .dds.
            SunTexture = @"textures\tx_sun_05.dds",
            SunGlareTexture = @"textures\tx_sun_flash_grey_05.dds",
            Timing = new ClimateTimingData(
                Hours(sunriseTime), Hours(sunriseTime + sunriseDuration),
                Hours(sunsetTime), Hours(sunsetTime + sunsetDuration),
                Volatility: 0,
                MoonPhaseLength: 0), // 0 → the per-game moon profile drives phase length
            // Clear first + ungated so ResolveClimateDefaultWeather picks it as the default; the
            // rest populate the weather picker.
            WeatherTypes = weathers
                .Select(w => new ClimateWeatherEntry(w.FormId, Chance: 100 / Math.Max(weathers.Count, 1), GlobalFormId: 0))
                .ToList(),
        };
    }

    /// <summary>Reads a channel's four keyframes (<c>{channel} Sunrise/Day/Sunset/Night Color</c>).
    /// HighNoon/Midnight are authored = Day/Night so the 6-band blend stays on authored values
    /// (a zero peak would read as unauthored, which is FNV-specific semantics).</summary>
    private static WeatherColor ReadKeyframes(Dictionary<string, string> s, string channel)
    {
        var sunrise = ReadColor(s, $"{channel} Sunrise Color") ?? default;
        var day = ReadColor(s, $"{channel} Day Color") ?? default;
        var sunset = ReadColor(s, $"{channel} Sunset Color") ?? default;
        var night = ReadColor(s, $"{channel} Night Color") ?? default;
        return new WeatherColor(sunrise, day, sunset, night, day, night);
    }

    private static WeatherColor Solid(WeatherRgba c) => new(c, c, c, c, c, c);

    private static WeatherRgba? ReadColor(Dictionary<string, string> s, string key)
    {
        if (!s.TryGetValue(key, out var value)) return null;
        var parts = value.Split(',');
        if (parts.Length < 3) return null;
        return byte.TryParse(parts[0].Trim(), out var r) &&
               byte.TryParse(parts[1].Trim(), out var g) &&
               byte.TryParse(parts[2].Trim(), out var b)
            ? new WeatherRgba(r, g, b, 255)
            : null;
    }

    private static float? ReadFloat(Dictionary<string, string> s, string key) =>
        s.TryGetValue(key, out var value) &&
        float.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var f)
            ? f
            : null;

    /// <summary>Case-insensitive section → (key → value) parse; `;` comments stripped.</summary>
    private static Dictionary<string, Dictionary<string, string>> ParseSections(string iniText)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? current = null;
        foreach (var raw in iniText.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == ';') continue;
            if (line[0] == '[' && line[^1] == ']')
            {
                current = [];
                sections[line[1..^1]] = current;
                continue;
            }
            var eq = line.IndexOf('=');
            if (current is null || eq <= 0) continue;
            current[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return sections;
    }

    /// <summary>Vanilla <c>[Weather]</c> schedule + <c>[Weather Clear]</c> (identical in every
    /// unmodded install; dumped from the Steam Morrowind.ini) — the fallback when no INI is found.</summary>
    private const string VanillaClearFallback = """
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
        Cloud Speed=1.25
        Glare View=1
        Cloud Texture=Tx_Sky_Clear.tga
        """;
}
