using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Nif;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;

namespace BethesdaMultitool;

public sealed partial class WorldView3DControl
{
    // Sky-element texture paths DERIVED from the loaded game's data (not a hardcoded per-game table):
    // sun/glare ← CLMT FNAM/GNAM, stars ← the CLMT MODL sky NIF, clouds ← the active weather's cloud
    // layers, moons ← whichever conventional moon asset the loaded archives actually ship. Any field
    // may be null → that sky element is skipped.
    private sealed record SkyTexturePaths(
        string? Sun, string? SunGlare, string? Cloud, string? Star, string? Moon, string? Secunda);

    /// <summary>The WorldspaceRecord currently shown in the 3D scene, or null for an interior /
    /// unlinked-exterior / nothing selected (those have no climate → placeholder atmosphere).</summary>
    private WorldspaceRecord? CurrentExteriorWorldspace()
    {
        if (_data is null || _selectedInterior is not null) return null;
        var index = WorldspaceComboBox.SelectedIndex;
        return index >= 0 && index < _data.Worldspaces.Count ? _data.Worldspaces[index] : null;
    }

    private ClimateRecord? ResolveClimate(WorldspaceRecord? ws)
    {
        if (_data is null || ws?.ClimateFormId is not uint climateFormId) return null;
        return _data.ClimatesByFormId.TryGetValue(climateFormId, out var climate) ? climate : null;
    }

    /// <summary>The climate's default weather = its first WLST entry (highest-priority candidate),
    /// resolved to a record. Null when the worldspace has no climate / weather list.</summary>
    private WeatherRecord? ResolveClimateDefaultWeather(WorldspaceRecord? ws)
    {
        var climate = ResolveClimate(ws);
        if (_data is null || climate is null || climate.WeatherTypes.Count == 0) return null;
        return _data.WeathersByFormId.TryGetValue(climate.WeatherTypes[0].WeatherFormId, out var w) ? w : null;
    }

    /// <summary>Refreshes the climate timing + default weather for whatever worldspace is now showing,
    /// then re-applies the dropdown selection (so "(Climate default)" picks up the new default).</summary>
    private void RefreshAtmosphereForCurrentWorldspace()
    {
        var ws = CurrentExteriorWorldspace();
        _currentClimateTiming = AtmosphereState.ClimateTiming.FromClimateData(ResolveClimate(ws)?.Timing);
        _climateDefaultWeather = ResolveClimateDefaultWeather(ws);
        _skyTexKey = null; // force the sky textures to re-resolve for the new climate/weather next frame
        ApplyWeatherSelection();
    }

    // Resolves the sky-element bindless texture indices for the current climate + active weather,
    // re-running only when either changes. Runs inside the render frame (a command list is open for the
    // texture cache's first upload). Everything is DERIVED from the loaded data — sun/glare from the CLMT
    // (FNAM/GNAM), stars from the climate's MODL sky NIF, clouds from the active weather's cloud layers —
    // with the genuinely engine-side moon resolved by probing whichever moon asset the game actually ships.
    private void EnsureSkyTexturesResolved()
    {
        if (_textureResolver12 is null) return; // retry once a worldspace (and its resolver) has loaded
        var resolver = _textureResolver12;
        var climate = ResolveClimate(CurrentExteriorWorldspace());
        var weather = _selectedWeather ?? _climateDefaultWeather; // the active weather drives the clouds
        var key = (climate?.FormId ?? 0u, weather?.FormId ?? 0u);
        if (_skyTexKey == key) return;
        _skyTexKey = key;

        const uint none = BethesdaMultitool.Core.Formats.Nif.Rendering.Camera.D3D12.SkyBillboardRenderer12.NoTexture;
        var paths = ResolveSkyTexturePaths(climate, weather);

        uint Resolve(string? path) =>
            path is null ? none : resolver.ResolveDiffuseBindlessIndex(path) ?? none;

        _sunDiscTexIndex = Resolve(paths.Sun);
        _sunGlareTexIndex = Resolve(paths.SunGlare);
        _moonTexIndex = Resolve(paths.Moon);
        _moonSecundaTexIndex = Resolve(paths.Secunda);
        _cloudTexIndex = Resolve(paths.Cloud);
        _starTexIndex = Resolve(paths.Star);
    }

    // Conventional sky-element asset names, tried against the LOADED archives (first existing wins). This
    // is NOT a per-game path table: an asset is used only if the loaded game actually ships it, so the
    // wrong game's texture can never be applied. These cover the genuinely engine-side moon (no record /
    // NIF carries it) and act as a safety net for stars when a sky NIF can't be harvested.
    private static readonly string[] StarCandidates =
        { @"textures\sky\skystars.dds", @"textures\sky\stars.dds" };
    private static readonly string[] MoonCandidates =
        { @"textures\sky\skymoonfull.dds", @"textures\sky\masser_full.dds" };
    private static readonly string[] SecundaCandidates =
        { @"textures\sky\secunda_full.dds" };

    // Builds the sky texture set from the loaded climate + weather. Stars come from the climate's own
    // MODL sky-dome NIF (the sky-shader block tagged STARS); clouds from the active weather's last
    // meaningful cloud layer; moons by probing the loaded archives (engine-procedural, not in the data).
    private SkyTexturePaths ResolveSkyTexturePaths(ClimateRecord? climate, WeatherRecord? weather)
    {
        // Skyrim's stars.nif tags FOUR blocks STARS (base stars + two constellation layers + galaxy); the
        // single-layer skybox wants the dense base field, so prefer the one whose name reads like "stars"
        // (FNV has just one — SkyStars.dds — which also matches). Falls back to the first STARS block, then
        // an archive probe when no sky NIF is harvestable.
        var star = HarvestSkyNifTexture(climate?.ModelPath, SkyObjectType.Stars, preferNameContains: "star")
                   ?? ProbeFirstExisting(StarCandidates);

        var cloud = PickCloudTexture(weather)
                    ?? HarvestSkyNifTexture(climate?.ModelPath, SkyObjectType.Clouds, preferNameContains: null);

        return new SkyTexturePaths(
            Sun: climate?.SunTexture,
            SunGlare: climate?.SunGlareTexture,
            Cloud: cloud,
            Star: star,
            Moon: ProbeFirstExisting(MoonCandidates),
            Secunda: ProbeFirstExisting(SecundaCandidates));
    }

    // The active weather's clouds = its last non-placeholder cloud layer (DNAM/CNAM/ANAM/BNAM, layer
    // order). sky\alpha.dds is the transparent "unused layer" placeholder; the front-most real layer is
    // the one the single-layer skybox shows. Null when the weather has no real cloud layer (clear sky).
    private static string? PickCloudTexture(WeatherRecord? weather)
    {
        if (weather is null) return null;
        string? pick = null;
        foreach (var layer in weather.CloudLayerTextures)
        {
            if (string.IsNullOrWhiteSpace(layer)) continue;
            var slash = layer.LastIndexOf('\\');
            var name = slash >= 0 ? layer.AsSpan(slash + 1) : layer.AsSpan();
            if (name.Equals("alpha.dds", StringComparison.OrdinalIgnoreCase)) continue;
            pick = layer;
        }

        return pick;
    }

    // Returns the FileName of the climate sky NIF's sky-shader block for the given object type (e.g. the
    // stars texture). When several blocks share the type and <paramref name="preferNameContains"/> is set,
    // the first whose file name contains that token wins (else the first of the type). Null when there's no
    // MODL, the NIF isn't in the mesh archives, or it carries no such block (→ caller probes the archives).
    private string? HarvestSkyNifTexture(string? modlPath, SkyObjectType type, string? preferNameContains)
    {
        var harvested = HarvestSkyNif(modlPath);
        if (harvested is null || harvested.Count == 0) return null;

        string? firstOfType = null;
        foreach (var tex in harvested)
        {
            if (tex.Type != type) continue;
            firstOfType ??= tex.FileName;
            if (preferNameContains is null ||
                tex.FileName.Contains(preferNameContains, StringComparison.OrdinalIgnoreCase))
            {
                return tex.FileName;
            }
        }

        return firstOfType;
    }

    // Loads + harvests the climate's MODL sky-dome NIF once per MODL path (cached). The MODL is authored
    // relative to the Data folder (e.g. "Sky\Stars.nif"); the mesh archives key on "meshes\...".
    private IReadOnlyList<SkyNifTexture>? HarvestSkyNif(string? modlPath)
    {
        if (string.IsNullOrWhiteSpace(modlPath) || _meshArchives is null) return null;

        var meshPath = modlPath.Replace('/', '\\').TrimStart('\\');
        if (!meshPath.StartsWith(@"meshes\", StringComparison.OrdinalIgnoreCase))
        {
            meshPath = @"meshes\" + meshPath;
        }

        if (string.Equals(_skyNifModlKey, meshPath, StringComparison.OrdinalIgnoreCase))
        {
            return _skyNifTextures;
        }

        _skyNifModlKey = meshPath;
        _skyNifTextures = _meshArchives.TryExtractFile(meshPath, out var bytes, out _)
            ? SkyNifTextureHarvester.Harvest(bytes)
            : null;
        return _skyNifTextures;
    }

    // First candidate path that exists in the loaded texture archives / loose files, or null if none do.
    private string? ProbeFirstExisting(string[] candidates)
    {
        if (_textureResolver12 is null) return null;
        foreach (var candidate in candidates)
        {
            if (_textureResolver12.TextureExists(candidate)) return candidate;
        }

        return null;
    }

    // Draws the whole sky for the frame: the gradient + cloud/star textures (skybox), then the sun/moon
    // billboards over them. The atmosphere is resolved ONCE here with lighting forced on (the sky shows
    // regardless of the lighting toggle) and shared by both. Clouds/stars are exterior-only (suppressed,
    // not the gradient, in interiors); the sun/moon billboards are exterior-only too.
    private void RenderSky(Matrix4x4 viewProj)
    {
        EnsureSkyTexturesResolved();
        var atmo = AtmosphereState.Resolve(_gameHour, _selectedWeather, _currentClimateTiming, lightingEnabled: true);
        var daylight = atmo.SunIntensity; // 0 night → 1 day
        var exterior = _selectedInterior is null;

        // Clouds: grey/blue at night → near-white by day. Stars: cool white, fading in as the sun sets.
        var cloudTint = Vector3.Lerp(new Vector3(0.12f, 0.13f, 0.18f), new Vector3(1.0f, 0.98f, 0.95f), daylight);
        // Night-brightness fix: stars are additive, so a near-white peak blew the night sky out. Cap the
        // star tint well below white, and dim the cloud layer at night (its opacity scaled by daylight)
        // so a night sky reads as dark with faint stars rather than a bright grey sheet.
        var starTint = new Vector3(0.45f, 0.50f, 0.62f);
        var starFade = Math.Clamp(1f - (daylight * 1.5f), 0f, 1f);
        var nightDim = MathF.Min(1f, 0.25f + (0.75f * daylight)); // clouds 0.25× at night → 1× by day
        var cloudOpacity = exterior ? 0.55f * nightDim : 0f;
        var domeStarFade = exterior ? starFade : 0f;
        // Sky dome centered on the camera at the sky-billboard radius: gradient + stars on the dome,
        // clouds on a flat overhead plane.
        _skyDome?.Render(viewProj, _camera.Position, 30000f,
            cloudTint, cloudOpacity, _cloudTexIndex, starTint, domeStarFade, _starTexIndex);

        if (exterior)
        {
            RenderSkyBillboards(viewProj, atmo);
        }
    }

    // Draws the textured sun (disc + glare, day) + moon (night) billboards after the sky gradient. Sun
    // direction/intensity come from the decompile-grounded AtmosphereState (already resolved by the
    // caller with lighting forced on); the moon uses a plausible night arc (the engine's independent
    // orbit isn't tracked here — documented simplification).
    private void RenderSkyBillboards(Matrix4x4 viewProj, AtmosphereState.Resolved atmo)
    {
        if (_skyBillboards is null)
        {
            return;
        }

        // Camera world basis from the inverse view matrix (System.Numerics row-vector: invView's rows are
        // the camera's world-space right/up).
        if (!Matrix4x4.Invert(_camera.GetViewMatrix(), out var invView))
        {
            return;
        }

        var camRight = Vector3.Normalize(new Vector3(invView.M11, invView.M12, invView.M13));
        var camUp = Vector3.Normalize(new Vector3(invView.M21, invView.M22, invView.M23));
        var camPos = _camera.Position;

        var sunDir = atmo.SunWorldDirection;
        var sunFade = Math.Clamp(sunDir.Z * 6f, 0f, 1f);                    // soft fade through the horizon
        var sunTint = Vector3.Lerp(new Vector3(1.0f, 0.55f, 0.30f), new Vector3(1.0f, 0.97f, 0.92f),
            Math.Clamp(sunDir.Z * 2f, 0f, 1f));                            // warm at the horizon → near-white high

        // Moon arc: peaks at midnight, below the horizon by day; fades in as the sun sets.
        var nightAng = (_gameHour / 24f) * MathF.Tau;                       // 0 at midnight
        var moonElev = MathF.Cos(nightAng) * (MathF.PI / 2f * 0.7f);        // peak ≈ 63° up at midnight
        var moonAz = (MathF.PI * 0.5f) + (nightAng * 0.5f);
        var cosE = MathF.Cos(moonElev);
        var moonDir = Vector3.Normalize(new Vector3(MathF.Cos(moonAz) * cosE, MathF.Sin(moonAz) * cosE, MathF.Sin(moonElev)));
        var moonFade = Math.Clamp(1f - (atmo.SunIntensity * 1.4f), 0f, 1f);

        // Secunda (Skyrim's second moon): offset azimuth + a slightly lower arc so the two moons sit
        // apart. Always computed; the renderer skips it when _moonSecundaTexIndex is NoTexture (every
        // single-moon game), so this is a no-op outside Skyrim.
        var secAz = moonAz + 0.55f;
        var secElev = moonElev * 0.82f;
        var secCosE = MathF.Cos(secElev);
        var secundaDir = Vector3.Normalize(new Vector3(MathF.Cos(secAz) * secCosE, MathF.Sin(secAz) * secCosE, MathF.Sin(secElev)));

        _skyBillboards.Render(viewProj, camPos, camRight, camUp,
            sunDir, sunFade, sunTint, _sunDiscTexIndex, _sunGlareTexIndex,
            moonDir, moonFade, _moonTexIndex,
            secundaDir, moonFade, _moonSecundaTexIndex);
    }

    /// <summary>Maps the weather dropdown's current selection to <see cref="_selectedWeather" /> (the
    /// "(Climate default)" item resolves to the current worldspace default, or null = placeholder).</summary>
    private void ApplyWeatherSelection()
    {
        if (WeatherComboBox?.SelectedItem is WeatherDropdownItem item)
        {
            _selectedWeather = item.IsClimateDefault ? _climateDefaultWeather : item.Weather;
        }
        else
        {
            _selectedWeather = null;
        }
    }

    /// <summary>Builds the weather dropdown once per load: a "(Climate default)" entry plus every
    /// weather in the file (by EditorId). Defaults the selection to "(Climate default)".</summary>
    private void PopulateWeatherDropdown()
    {
        if (WeatherComboBox is null || _data is null) return;

        var items = new List<WeatherDropdownItem> { new("(Climate default)", null, true) };
        foreach (var w in _data.AllWeathers)
        {
            items.Add(new WeatherDropdownItem(WeatherLabel(w), w, false));
        }

        _suppressWeatherSelectionEvent = true;
        WeatherComboBox.ItemsSource = items;
        WeatherComboBox.SelectedIndex = 0;
        _suppressWeatherSelectionEvent = false;
    }

    private void WeatherComboBox_SelectionChanged(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
    {
        // Null-guard _data — the handler can early-fire during XAML load before LoadData runs.
        if (_suppressWeatherSelectionEvent || _data is null) return;
        ApplyWeatherSelection();
    }

    private void TimeSlider_ValueChanged(
        object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        // Continuous input — no _show flag; the next frame's BindAtmosphereConstants reads _gameHour.
        _gameHour = (float)e.NewValue;
    }

    private static string WeatherLabel(WeatherRecord w) =>
        string.IsNullOrEmpty(w.EditorId) ? $"0x{w.FormId:X8}" : $"{w.EditorId} (0x{w.FormId:X8})";

    /// <summary>One weather dropdown entry. The first item is the climate-default sentinel
    /// (<see cref="IsClimateDefault" /> true, <see cref="Weather" /> resolved per worldspace at
    /// selection time); the rest carry a concrete weather. <see cref="ToString" /> drives the display.</summary>
    private sealed record WeatherDropdownItem(string Label, WeatherRecord? Weather, bool IsClimateDefault)
    {
        public override string ToString() => Label;
    }
}
