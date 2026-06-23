using System.IO;
using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif;
using BethesdaMultitool.Core.Formats.Nif.Conversion;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera.D3D12;

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

        // Rebuild the real sky-dome geometry (atmosphere/stars/clouds NIFs) for this climate + weather.
        // The dome layers carry their own per-layer textures, so the single cloud/star indices the old
        // procedural dome used are gone — stars come from the sky NIF, clouds from the active weather.
        RebuildSkyGeometry(climate, weather);
    }

    // Loads the climate's real sky-dome NIFs (the engine's own sky set — atmosphere, stars, clouds, in
    // render order) and hands their geometry + per-layer textures to the sky-geometry renderer. The cloud
    // layers' textures come from the ACTIVE WEATHER (version-driven), so the sky is drawn from the loaded
    // data, not a procedural approximation. Cleared for interiors / no-climate worldspaces.
    private void RebuildSkyGeometry(ClimateRecord? climate, WeatherRecord? weather)
    {
        if (_skyGeometry is null) return;
        _skyGeometry.Clear();
        if (_textureResolver12 is null || _meshArchives is null ||
            climate?.ModelPath is not string modl || string.IsNullOrWhiteSpace(modl))
        {
            return;
        }

        // The climate MODL is the stars dome (e.g. "Sky\Stars.nif"); the engine draws its sibling clouds
        // dome from the same sky directory. The atmosphere gradient is the renderer's fallback dome (so it
        // works for every game), so only the textured stars + clouds NIFs are loaded here.
        var skyDir = Path.GetDirectoryName(modl.Replace('/', '\\')) ?? string.Empty;
        var layers = new List<SkyGeometryLayer>();
        AddSkyNifLayers(layers, modl, weather);
        AddSkyNifLayers(layers, Path.Combine(skyDir, "Clouds.nif"), weather);
        if (layers.Count > 0)
        {
            _skyGeometry.SetLayers(layers);
        }
    }


    // Extracts one sky NIF's renderable layers (filtered to sky-shader submeshes) and appends them as
    // cooked render layers. Stars take the sky NIF's baked texture; clouds take the active weather's
    // cloud-layer texture by ordinal (falling back to the NIF's baked one); the gradient needs none.
    private void AddSkyNifLayers(List<SkyGeometryLayer> layers, string modlPath, WeatherRecord? weather)
    {
        var submeshes = TryLoadSkyNif(modlPath);
        if (submeshes is null) return;

        var cloudOrdinal = 0;
        foreach (var sm in submeshes)
        {
            if (sm.SkyType is not SkyObjectType type || sm.Triangles.Length == 0)
            {
                continue;
            }

            var texIndex = uint.MaxValue;
            var scrollSpeed = Vector2.Zero;
            WeatherColor? cloudColor = null;
            if (type == SkyObjectType.Clouds)
            {
                // The engine textures clouds.nif's (up to 4) cloud-layer shapes from the ACTIVE weather's
                // cloud textures in order (Clouds::Initialize attaches the shapes; Clouds::Update indexes
                // them 1:1 with the weather's per-layer textures — MemDebug XEX). A weather marks an UNUSED
                // layer by binding it to sky\alpha.dds, a fully transparent texture: FNV clear weather is
                // [alpha, alpha, alpha, NVCloudlight] — i.e. ONE real cloud sheet, three empty placeholders.
                // So we draw ONLY the weather's real (non-alpha) layers, with no baked-NIF fallback. Drawing
                // every baked cap (or the alpha placeholders) was the "5 separate domes" bug — it rendered
                // all four authored cloud shapes at once instead of the single sheet the engine shows.
                var layerIndex = cloudOrdinal;
                cloudOrdinal++;
                var cloudPath = WeatherCloudTexture(weather, layerIndex);
                if (cloudPath is null || IsUnusedCloudLayer(cloudPath)) continue;
                texIndex = ResolveSkyTexture(cloudPath);
                if (texIndex == uint.MaxValue) continue; // texture missing -> don't draw it

                // This layer's per-draw color (PNAM) + drift speed (QNAM/RNAM) — the engine drives each
                // cloud layer's color/opacity and scroll independently (SkyShader::SetupGeometryConstants /
                // Clouds::Update). Both are indexed by the SAME layer ordinal as the textures above.
                cloudColor = WeatherCloudColor(weather, layerIndex);
                scrollSpeed = WeatherCloudScroll(weather, layerIndex);
            }
            else if (type == SkyObjectType.Stars)
            {
                texIndex = ResolveSkyTexture(sm.DiffuseTexturePath);
                if (texIndex == uint.MaxValue) continue;
            }

            layers.Add(new SkyGeometryLayer
            {
                Positions = sm.Positions,
                Uvs = sm.UVs,
                // The cloud/star caps carry the engine's horizon fade in their per-vertex ALPHA
                // (cloudcloudy ~2 at the rim → 255 overhead) — hand it to the renderer so the sky fades
                // exactly as the mesh authors baked it, instead of a guessed shader curve.
                VertexColors = sm.VertexColors,
                Indices = sm.Triangles,
                Type = type,
                TextureIndex = texIndex,
                ScrollSpeed = scrollSpeed,
                CloudColor = cloudColor,
            });
        }
    }

    private uint ResolveSkyTexture(string? path) =>
        path is null || _textureResolver12 is null
            ? uint.MaxValue
            : _textureResolver12.ResolveDiffuseBindlessIndex(path) ?? uint.MaxValue;

    // Loads + extracts a sky NIF from the mesh archives (big-endian Xbox NIFs converted first). Returns the
    // renderable submeshes (each carrying its SkyType + baked texture) or null when absent/unparseable.
    private List<RenderableSubmesh>? TryLoadSkyNif(string modlPath)
    {
        if (_meshArchives is null || string.IsNullOrWhiteSpace(modlPath)) return null;

        var meshPath = modlPath.Replace('/', '\\').TrimStart('\\');
        if (!meshPath.StartsWith(@"meshes\", StringComparison.OrdinalIgnoreCase))
        {
            meshPath = @"meshes\" + meshPath;
        }

        if (!_meshArchives.TryExtractFile(meshPath, out var bytes, out _)) return null;

        var nif = NifParser.Parse(bytes);
        if (nif is null) return null;
        if (nif.IsBigEndian)
        {
            var converted = NifConverter.Convert(bytes);
            if (!converted.Success || converted.OutputData is null) return null;
            bytes = converted.OutputData;
            nif = NifParser.Parse(bytes);
            if (nif is null) return null;
        }

        try
        {
            return NifGeometryExtractor.Extract(bytes, nif)?.Submeshes;
        }
        catch
        {
            return null;
        }
    }

    // The active weather's cloud-layer texture for a cloud dome shape's ordinal. WeatherRecord.CloudLayerTextures
    // is already unified across games by the parser (FO3/FNV DNAM/CNAM/ANAM/BNAM + Skyrim+ ?0TX, in layer
    // order, per xEdit wbWeatherCloudTextures), so this is game-agnostic. NOTE: Skyrim's clouds.nif has more
    // shapes than weather layers; shapes past the last layer get no texture and are skipped — the exact
    // shape<->layer correspondence is a NIF follow-up (clouds.nif node names / Sky::ReloadAllTextures).
    private static string? WeatherCloudTexture(WeatherRecord? weather, int ordinal)
    {
        if (weather is null) return null;
        var clouds = weather.CloudLayerTextures;
        return ordinal >= 0 && ordinal < clouds.Count ? clouds[ordinal] : null;
    }

    // A weather binds sky\alpha.dds to a cloud layer to mean "unused this weather" — a fully transparent
    // texture. The engine renders nothing for such a layer; we likewise skip it so we draw only the
    // weather's real cloud sheet(s) instead of empty placeholder dome caps.
    private static bool IsUnusedCloudLayer(string path) =>
        Path.GetFileNameWithoutExtension(path).Equals("alpha", StringComparison.OrdinalIgnoreCase);

    // The active weather's PNAM cloud color for a layer ordinal (RGB tint + A opacity, per time-of-day).
    // The renderer blends its time bands by the game hour — the engine's per-draw cloud color uniform.
    private static WeatherColor? WeatherCloudColor(WeatherRecord? weather, int layer)
    {
        var colors = weather?.CloudColors;
        return colors is not null && layer >= 0 && layer < colors.Count ? colors[layer] : null;
    }

    // Per-layer cloud UV drift from the weather's QNAM (X) / RNAM (Y) speed bytes. Read as SIGNED (sbyte)
    // so 0 = still and the sign gives the drift direction, scaled to a slow UV/sec rate. CloudScrollScale
    // is the one visual tunable; the per-layer RELATIVE speeds + axes are the data-grounded part (the exact
    // engine speed constant lives in the binary's data section — Clouds::Update scales the byte by it).
    private static Vector2 WeatherCloudScroll(WeatherRecord? weather, int layer)
    {
        if (weather is null) return Vector2.Zero;
        var x = layer < weather.CloudSpeedsX.Count ? (sbyte)weather.CloudSpeedsX[layer] : (sbyte)0;
        var y = layer < weather.CloudSpeedsY.Count ? (sbyte)weather.CloudSpeedsY[layer] : (sbyte)0;
        return new Vector2(x / 127f, y / 127f) * CloudScrollScale;
    }

    private const float CloudScrollScale = 0.010f; // UV/sec at full (±127) cloud speed — the visual tunable

    // Conventional sky-element asset names, tried against the LOADED archives (first existing wins). This
    // is NOT a per-game path table: an asset is used only if the loaded game actually ships it, so the
    // wrong game's texture can never be applied. These cover the genuinely engine-side moon (no record /
    // NIF carries it) and act as a safety net for stars when a sky NIF can't be harvested.
    private static readonly string[] StarCandidates =
        { @"textures\sky\skystars.dds", @"textures\sky\stars.dds" };

    // The per-game moon configuration: how many moons this engine draws, from which assets, at what
    // apparent size. Each Bethesda engine differs (Morrowind/Oblivion/Skyrim draw two moons, FO3/FNV/4/76
    // one, Starfield/Unknown none), so the moon is resolved from the loaded game rather than a shared
    // constant — and each game probes only ITS own moon assets, so the wrong game's texture can never be
    // drawn as a billboard moon. Cheap (returns a cached per-game singleton); read per use.
    private SkyMoonProfile MoonProfile =>
        SkyMoonProfile.ForGame(_data?.Game ?? BethesdaMultitool.Core.Games.BethesdaGame.Unknown);

    // Builds the sky texture set from the loaded climate + weather. Stars come from the climate's own
    // MODL sky-dome NIF (the sky-shader block tagged STARS); clouds from the active weather's last
    // meaningful cloud layer; moons by probing the loaded archives (engine-procedural, not in the data).
    private SkyTexturePaths ResolveSkyTexturePaths(ClimateRecord? climate, WeatherRecord? weather)
    {
        // The moon/secunda/star archive PROBE is only a billboard-sky feature; drive it from the per-game
        // moon profile so a game with no billboard moon can't surface a probe match, and each game probes
        // only ITS own moon assets at ITS own count.
        var moon = MoonProfile;

        // Skyrim's stars.nif tags FOUR blocks STARS (base stars + two constellation layers + galaxy); the
        // single-layer skybox wants the dense base field, so prefer the one whose name reads like "stars"
        // (FNV has just one — SkyStars.dds — which also matches). Falls back to the first STARS block, then
        // an archive probe when no sky NIF is harvestable. The HARVEST stays ungated (it reads the loaded
        // game's own climate NIF); only the generic probe fallback is gated to billboard-moon games.
        var star = HarvestSkyNifTexture(climate?.ModelPath, SkyObjectType.Stars, preferNameContains: "star")
                   ?? (moon.HasMoon ? ProbeFirstExisting(StarCandidates) : null);

        var cloud = PickCloudTexture(weather)
                    ?? HarvestSkyNifTexture(climate?.ModelPath, SkyObjectType.Clouds, preferNameContains: null);

        return new SkyTexturePaths(
            Sun: climate?.SunTexture,
            SunGlare: climate?.SunGlareTexture,
            Cloud: cloud,
            Star: star,
            Moon: moon.HasMoon ? ProbeFirstExisting(moon.PrimaryTextureCandidates) : null,
            Secunda: moon.HasSecondMoon ? ProbeFirstExisting(moon.SecondaryTextureCandidates) : null);
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
    private string? ProbeFirstExisting(IReadOnlyList<string> candidates)
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
        // Real sky-dome NIF geometry centered on the camera: atmosphere gradient + stars + cloud layers,
        // each on its authored UVs. Per-layer textures were resolved in EnsureSkyTexturesResolved.
        _skyGeometry?.Render(viewProj, _camera.Position,
            cloudTint, cloudOpacity, starTint, domeStarFade, _gameHour, _currentClimateTiming);

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

        // Per-game moon disc sizes (fraction of the billboard radius → world half-extent). Prefer the
        // engine-exact size read from the loaded ESM's GMSTs (iMasserSize/iSecundaSize ÷ fSunXExtreme —
        // mod-aware), falling back to the per-game SkyMoonProfile default when the GMSTs are absent
        // (Morrowind TES3, DMP/save without a settings table).
        var moonProfile = MoonProfile;
        var primaryFraction = _data?.MoonPrimaryHalfSizeFraction ?? moonProfile.PrimaryHalfSizeFraction;
        var secondaryFraction = _data?.MoonSecondaryHalfSizeFraction ?? moonProfile.SecondaryHalfSizeFraction;
        var moonHalf = SkyBillboardRenderer12.Radius * primaryFraction;
        var moon2Half = SkyBillboardRenderer12.Radius * secondaryFraction;

        _skyBillboards.Render(viewProj, camPos, camRight, camUp,
            sunDir, sunFade, sunTint, _sunDiscTexIndex, _sunGlareTexIndex,
            moonDir, moonFade, _moonTexIndex, moonHalf,
            secundaDir, moonFade, _moonSecundaTexIndex, moon2Half);
    }

    /// <summary>Maps the lighting panel's current weather selection to <see cref="_selectedWeather" />
    /// (the "(Climate default)" item resolves to the current worldspace default).</summary>
    private void ApplyWeatherSelection()
    {
        var sel = LightingPanel.CurrentWeatherSelection;
        _selectedWeather = sel.IsClimateDefault ? _climateDefaultWeather : sel.Weather;
    }

    /// <summary>Builds the weather dropdown once per load via the shared lighting panel: a
    /// "(Climate default)" entry plus every weather in the file. Defaults to "(Climate default)".</summary>
    private void PopulateWeatherDropdown()
    {
        if (_data is null) return;
        LightingPanel.SetWeathers(_data.AllWeathers);
    }

    private void LightingPanel_WeatherChanged(object? sender, WeatherSelection sel)
    {
        // Null-guard _data — the handler can early-fire during XAML load before LoadData runs.
        if (_data is null) return;
        _selectedWeather = sel.IsClimateDefault ? _climateDefaultWeather : sel.Weather;
    }

    private void LightingPanel_TimeChanged(object? sender, double hour)
    {
        // Continuous input — no _show flag; the next frame's BindAtmosphereConstants reads _gameHour.
        _gameHour = (float)hour;
    }
}
