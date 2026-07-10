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

    /// <summary>
    ///     The climate driving the sky for an exterior worldspace, via the engine's precedence:
    ///     <list type="number">
    ///         <item>the worldspace's own climate (WRLD <c>CNAM</c>) — present in FO3/FNV/Skyrim/FO4 and the
    ///         Oblivion worldspaces that set one;</item>
    ///         <item>a global default climate when the worldspace carries none — Oblivion's Tamriel has NO
    ///         <c>CNAM</c> (climate is a rare per-cell <c>XCCM</c> override there — only ~55 in the whole
    ///         277&#160;MB Oblivion.esm, so nothing to sample at cell 0,0), and the engine falls back to a
    ///         global default. Oblivion ships one literally named <c>DefaultClimate</c>.</item>
    ///     </list>
    ///     Returns null for an interior / no selection (those have no sky climate). The fallback only fires
    ///     for an actual exterior worldspace whose climate doesn't resolve, so FO3/FNV/Skyrim/FO4 — whose
    ///     exterior worldspaces all carry a CNAM — are unaffected.
    /// </summary>
    private ClimateRecord? ResolveClimate(WorldspaceRecord? ws)
    {
        if (_data is null || ws is null) return null; // interior / nothing selected → no sky climate
        if (ws.ClimateFormId is uint climateFormId &&
            _data.ClimatesByFormId.TryGetValue(climateFormId, out var climate))
        {
            return climate;
        }

        return ResolveDefaultClimate(); // climateless exterior worldspace (Oblivion Tamriel)
    }

    /// <summary>The loaded data's global default climate for a worldspace that names none: prefer the one
    /// EditorID'd exactly <c>DefaultClimate</c> (Oblivion's), then any <c>*Default*</c> climate (e.g.
    /// FNV's NVDefaultClimate), then the first loaded climate so an exterior worldspace still gets a real
    /// sky (sun + per-weather colors) instead of the bare gradient. Null when no climates are loaded.</summary>
    private ClimateRecord? ResolveDefaultClimate()
    {
        if (_data is null || _data.ClimatesByFormId.Count == 0) return null;
        var climates = _data.ClimatesByFormId.Values;
        return climates.FirstOrDefault(c => string.Equals(c.EditorId, "DefaultClimate", StringComparison.OrdinalIgnoreCase))
               ?? climates.FirstOrDefault(c => c.EditorId?.Contains("Default", StringComparison.OrdinalIgnoreCase) == true)
               ?? climates.First();
    }

    /// <summary>The climate's default weather, resolved to a record. The climate's weather list (WLST)
    /// gates conditional variants with a Global FormID — e.g. NVDefaultClimate lists NVWastelandClearNight
    /// gated by the VNight global (night only, cloud = a starfield) AND NVWastelandClear with NO gate. The
    /// UNGATED entry is the base/default weather, so prefer it; otherwise a night-gated variant could become
    /// the daytime default. Falls back to the first entry when every entry is gated. Null when the
    /// worldspace has no climate / weather list.</summary>
    private WeatherRecord? ResolveClimateDefaultWeather(WorldspaceRecord? ws)
    {
        var climate = ResolveClimate(ws);
        if (_data is null || climate is null || climate.WeatherTypes.Count == 0) return null;
        var entry = climate.WeatherTypes.FirstOrDefault(w => w.GlobalFormId == 0) ?? climate.WeatherTypes[0];
        return _data.WeathersByFormId.TryGetValue(entry.WeatherFormId, out var w) ? w : null;
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

        // Per-phase moon textures (Morrowind ships 8 per moon; FO4/FO76 ship 8 in the reused Creation
        // masser slot). Games with no per-phase pattern resolve every entry to NoTexture and
        // RenderSkyBillboards falls back to the full-moon index. Cheap (cached by the resolver);
        // re-resolved with the rest of the sky set.
        var moonForPhases = MoonProfile;
        for (var i = 0; i < MoonSky.PhaseCount; i++)
        {
            _moonPhaseTexIndices[i] = Resolve(moonForPhases.PhaseTexturePath(secondary: false, i));
            _moonSecundaPhaseTexIndices[i] = Resolve(moonForPhases.PhaseTexturePath(secondary: true, i));
        }

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
        if (_skyDiag)
        {
            var dws = CurrentExteriorWorldspace();
            Log.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "[SkyGeo] enter: game={0} skyGeom={1} resolver={2} meshArch={3} climate={4} modl='{5}' | wsEdid={6} wsClimateFid={7} climCount={8} wsComboIdx={9} interior={10}",
                _data?.Game, _skyGeometry is not null, _textureResolver12 is not null, _meshArchives is not null,
                climate?.EditorId ?? "(null)", climate?.ModelPath ?? "(null)",
                dws?.EditorId ?? "(null)",
                dws?.ClimateFormId is uint cf ? $"0x{cf:X8}" : "(none)",
                _data?.ClimatesByFormId.Count ?? -1,
                WorldspaceComboBox?.SelectedIndex ?? -2,
                _selectedInterior is not null));
        }

        if (_skyGeometry is null) return;
        _skyGeometry.Clear();
        if (_textureResolver12 is null || _meshArchives is null)
        {
            return;
        }

        var layers = new List<SkyGeometryLayer>();
        if (_data?.Game == BethesdaMultitool.Core.Games.BethesdaGame.Morrowind)
        {
            // Morrowind's sky set is ENGINE-HARDCODED, not climate-authored — the NIF names sit in
            // Morrowind.exe beside their "Failed to load sky stars/clouds" error strings:
            // sky_night_01/02.nif are the stars domes (02 ships with the expansions — missing files
            // skip cleanly) and sky_clouds_01.nif is the cloud dome, retextured per weather from the
            // INI's Cloud Texture (already carried on the synthetic weather's CloudLayerTextures).
            // sky_atmosphere.nif (the vertex-tinted gradient dome) is deliberately NOT loaded: the
            // renderer's own gradient dome already draws the resolved sky colors.
            AddSkyNifLayers(layers, "sky_night_01.nif", weather);
            AddSkyNifLayers(layers, "sky_night_02.nif", weather);
            AddSkyNifLayers(layers, "sky_clouds_01.nif", weather);
        }
        else if (climate?.ModelPath is string modl && !string.IsNullOrWhiteSpace(modl))
        {
            // The climate MODL is the stars dome (e.g. "Sky\Stars.nif"); the engine draws its sibling
            // clouds dome from the same sky directory. The atmosphere gradient is the renderer's
            // fallback dome (so it works for every game), so only the textured stars + clouds NIFs
            // are loaded here.
            var skyDir = Path.GetDirectoryName(modl.Replace('/', '\\')) ?? string.Empty;
            AddSkyNifLayers(layers, modl, weather);
            AddSkyNifLayers(layers, Path.Combine(skyDir, "Clouds.nif"), weather);
        }
        else
        {
            return;
        }

        if (_skyDiag)
        {
            var cloudTex = weather?.CloudLayerTextures is { Count: > 0 } ct ? string.Join(", ", ct) : "(none)";
            Log.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "[SkyGeo] game={0} climate={1} modl='{2}' weather={3} cloudTex=[{4}] => layers: {5} stars, {6} clouds (of {7} total)",
                _data?.Game, climate?.EditorId, climate?.ModelPath ?? "(game-keyed)",
                weather?.EditorId ?? "(default)", cloudTex,
                layers.Count(l => l.Type == SkyObjectType.Stars),
                layers.Count(l => l.Type == SkyObjectType.Clouds), layers.Count));
        }

        if (layers.Count > 0)
        {
            _skyGeometry.SetLayers(layers);
        }
    }

    // Diagnostic toggle for the sky-geometry pipeline — env FALLOUT_VIEWER_SKY_DIAG=1. Logs once per
    // climate/weather change (RebuildSkyGeometry), so it's quiet during steady-state rendering. Used to
    // ground per-game cloud diagnoses (which NIF, how many sky-typed submeshes, which cloud layers resolve).
    private static readonly bool _skyDiag =
        Environment.GetEnvironmentVariable("FALLOUT_VIEWER_SKY_DIAG") == "1";

    // Classifies a sky-dome shape by NAME, for pre-SkyShaderProperty NIFs (Oblivion's clouds.nif "CloudDome"
    // + stars.nif "Stars" shapes, NIF 20.0.0.4) that carry no Sky Object Type. Returns null for shapes that
    // aren't a cloud/star layer (e.g. an atmosphere/sky-dome base — the renderer's fallback gradient covers
    // that). Only consulted when the NIF gives no SkyType, so shader-typed games are unaffected.
    private static SkyObjectType? InferSkyTypeFromName(string? shapeName)
    {
        if (string.IsNullOrEmpty(shapeName)) return null;
        if (shapeName.Contains("Cloud", StringComparison.OrdinalIgnoreCase)) return SkyObjectType.Clouds;
        if (shapeName.Contains("Star", StringComparison.OrdinalIgnoreCase)) return SkyObjectType.Stars;
        // Morrowind's stars domes are named sky_night_01/02 ("Tri sky_night_01 0") — the engine's
        // own error string calls them "sky stars". Gated (like the rest of this method) to shapes
        // with no SkyShaderProperty type, so shader-typed games are unaffected.
        if (shapeName.Contains("Night", StringComparison.OrdinalIgnoreCase)) return SkyObjectType.Stars;
        return null;
    }


    // Extracts one sky NIF's renderable layers (filtered to sky-shader submeshes) and appends them as
    // cooked render layers. Stars take the sky NIF's baked texture; clouds take the active weather's
    // cloud-layer texture by ordinal (falling back to the NIF's baked one); the gradient needs none.
    private void AddSkyNifLayers(List<SkyGeometryLayer> layers, string modlPath, WeatherRecord? weather)
    {
        var submeshes = TryLoadSkyNif(modlPath);
        if (_skyDiag)
        {
            Log.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "[SkyGeo]   nif '{0}': {1}", modlPath,
                submeshes is null
                    ? "NOT LOADED (missing/unparseable)"
                    : $"{submeshes.Count} submeshes [{string.Join(", ", submeshes.Select(s => $"{(s.SkyType?.ToString() ?? "null")}:{s.Triangles.Length / 3}tri"))}]"));
        }

        if (submeshes is null) return;

        var cloudOrdinal = 0;
        foreach (var sm in submeshes)
        {
            // SkyType comes from the NIF's SkyShaderProperty (FO3/FNV/Skyrim/FO4). Oblivion's
            // pre-SkyShaderProperty sky NIFs (clouds.nif/stars.nif, NIF 20.0.0.4) carry no such block, so
            // their CloudDome/Stars shapes arrive with SkyType=null and were dropped. Classify those by
            // NAME instead (the engine keys them by node name). Gated to null-SkyType, so the shader-typed
            // games keep their authoritative type; this method only processes sky NIFs, so it's safe here.
            var resolved = sm.SkyType ?? InferSkyTypeFromName(sm.ShapeName);
            if (resolved is not SkyObjectType type || sm.Triangles.Length == 0)
            {
                continue;
            }

            var texIndex = uint.MaxValue;
            var scrollSpeed = Vector2.Zero;
            WeatherColor? cloudColor = null;
            WeatherCloudAlpha? cloudAlpha = null;
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
                if (cloudPath is null || IsUnusedCloudLayer(cloudPath))
                {
                    if (_skyDiag) Log.Info($"[SkyGeo]     cloud shape #{layerIndex}: layer='{cloudPath ?? "(no layer)"}' -> SKIP (unused/no-layer)");
                    continue;
                }

                texIndex = ResolveSkyTexture(cloudPath);
                if (texIndex == uint.MaxValue)
                {
                    if (_skyDiag) Log.Info($"[SkyGeo]     cloud shape #{layerIndex}: layer='{cloudPath}' -> SKIP (texture unresolved)");
                    continue; // texture missing -> don't draw it
                }

                if (_skyDiag)
                {
                    var cc = WeatherCloudColor(weather, layerIndex);
                    var a = cc is null ? "n/a" : $"R{cc.Day.R} G{cc.Day.G} B{cc.Day.B} A{cc.Day.A}";
                    Log.Info($"[SkyGeo]     cloud shape #{layerIndex}: layer='{cloudPath}' -> DRAW (tex#{texIndex}) dayColor=[{a}]");
                }

                // This layer's per-draw color (PNAM), per-layer opacity (JNAM) + drift speed (QNAM/RNAM) —
                // the engine drives each cloud layer's color/opacity/scroll independently
                // (SkyShader::SetupGeometryConstants / Clouds::Update). All are indexed by the SAME layer
                // ordinal as the textures above. JNAM (modern weathers) hides/thins layers per weather; FO3/
                // FNV carry none, so cloudAlpha stays null and the renderer keeps its flat per-layer opacity.
                cloudColor = WeatherCloudColor(weather, layerIndex);
                cloudAlpha = WeatherCloudAlphaFor(weather, layerIndex);
                scrollSpeed = WeatherCloudScroll(weather, layerIndex);
            }
            else if (type == SkyObjectType.Stars)
            {
                // Stars take the sky NIF's baked texture (FO3/FNV/Skyrim). Oblivion's stars.nif shapes carry
                // no baked diffuse, so fall back to the conventional star asset the loaded archives ship.
                texIndex = ResolveSkyTexture(sm.DiffuseTexturePath);
                if (texIndex == uint.MaxValue)
                {
                    texIndex = ResolveSkyTexture(ProbeFirstExisting(StarCandidates));
                }

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
                CloudAlpha = cloudAlpha,
            });
        }
    }

    private uint ResolveSkyTexture(string? path) =>
        path is null || _textureResolver12 is null
            ? uint.MaxValue
            : _textureResolver12.ResolveDiffuseBindlessIndex(NormalizeSkyTexturePath(path)) ?? uint.MaxValue;

    // WTHR cloud-texture paths are stored relative to the textures\ root (e.g. "sky\NVCloudlight.dds"),
    // but the texture cache keys off the full "textures\..." path the NIFs use — a bare "sky\..." path
    // misses and resolves to the transparent placeholder, so the cloud sheet renders invisible. Prepend
    // the prefix when absent (idempotent for the NIF/sun/moon paths that already carry it).
    private static string NormalizeSkyTexturePath(string path)
    {
        var p = path.Replace('/', '\\').TrimStart('\\');
        return p.StartsWith(@"textures\", StringComparison.OrdinalIgnoreCase) ? p : @"textures\" + p;
    }

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

    // The active weather's JNAM per-layer cloud OPACITY for a layer ordinal (modern weathers only). The
    // renderer multiplies the host cloud opacity by the time-of-day-sampled value, so a layer authored 0 is
    // hidden and fractional layers thin out. Null when the weather has no JNAM (FO3/FNV) or the ordinal is
    // past the authored layers — the renderer then keeps its flat per-layer opacity.
    private static WeatherCloudAlpha? WeatherCloudAlphaFor(WeatherRecord? weather, int layer)
    {
        var alphas = weather?.CloudLayerAlphas;
        return alphas is not null && layer >= 0 && layer < alphas.Count ? alphas[layer] : null;
    }

    // Per-layer cloud UV drift from the weather's QNAM (X) / RNAM (Y) speeds, pre-normalized to −1‥1 by
    // the parser (signed bytes ÷127 on FNV/FO3/Skyrim, per-layer floats on FO4/FO76), scaled to a slow
    // UV/sec rate. CloudScrollScale is the one visual tunable; the per-layer RELATIVE speeds + axes are
    // the data-grounded part (the exact engine speed constant lives in the binary's data section —
    // Clouds::Update scales the authored value by it).
    // NOTE: FNV weathers in practice OMIT QNAM/RNAM (confirmed: count 0 on every NVWastelandClear*), so the
    // engine drifts clouds at a built-in default speed (Clouds::Update defaults the per-layer byte to 0x33).
    // We reproduce that with DefaultCloudDrift when a layer has no authored speed, so FNV clouds still move.
    private static Vector2 WeatherCloudScroll(WeatherRecord? weather, int layer)
    {
        var hasX = weather is not null && layer < weather.CloudSpeedsX.Count;
        var hasY = weather is not null && layer < weather.CloudSpeedsY.Count;
        if (!hasX && !hasY) return DefaultCloudDrift; // no QNAM/RNAM (typical FNV) -> engine default drift
        var x = hasX ? weather!.CloudSpeedsX[layer] : 0f;
        var y = hasY ? weather!.CloudSpeedsY[layer] : 0f;
        return new Vector2(x, y) * CloudScrollScale;
    }

    private const float CloudScrollScale = 0.010f;                 // UV/sec at full (±127) authored cloud speed
    private static readonly Vector2 DefaultCloudDrift = new(0.0040f, 0.0015f); // engine default when no QNAM/RNAM

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
    // <paramref name="domeCenter"/> is the world-space center of the sky dome / sun-moon billboards. The
    // live frame passes Vector3.Zero together with a translation-free viewProj (camera-relative, jitter-
    // free); the offscreen scene capture passes the absolute camera position with its absolute viewProj.
    private void RenderSky(Matrix4x4 viewProj, Vector3 domeCenter)
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
        _skyGeometry?.Render(viewProj, domeCenter,
            cloudTint, cloudOpacity, starTint, domeStarFade, _gameHour, _currentClimateTiming);

        if (exterior)
        {
            RenderSkyBillboards(viewProj, atmo, domeCenter);
        }
    }

    // Draws the textured sun (disc + glare, day) + moon (night) billboards after the sky gradient. Sun
    // direction/intensity come from the decompile-grounded AtmosphereState (already resolved by the
    // caller with lighting forced on); the moon uses a plausible night arc (the engine's independent
    // orbit isn't tracked here — documented simplification).
    private void RenderSkyBillboards(Matrix4x4 viewProj, AtmosphereState.Resolved atmo, Vector3 domeCenter)
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
        // Billboard origin = the dome center. The live frame passes 0 (camera-relative, with a translation-
        // free viewProj) so the sun/moon don't jitter at far-from-origin camera positions; the offscreen
        // capture passes the absolute camera position. camRight/camUp are pure directions either way.
        var camPos = domeCenter;

        var sunDir = atmo.SunWorldDirection;
        var sunFade = Math.Clamp(sunDir.Z * 6f, 0f, 1f);                    // soft fade through the horizon
        var sunTint = Vector3.Lerp(new Vector3(1.0f, 0.55f, 0.30f), new Vector3(1.0f, 0.97f, 0.92f),
            Math.Clamp(sunDir.Z * 2f, 0f, 1f));                            // warm at the horizon → near-white high

        // Two moons on independent, day-driven orbits (MoonSky, decompile-grounded) — Masser and Secunda
        // no longer share a path (the headline fix). The day slider advances both the orbital position and
        // the phase; time-of-day drives the nightly arc. The renderer skips a moon below the horizon, and
        // skips Secunda entirely for single-moon games (its texture is NoTexture).
        var moonProfile = MoonProfile;
        var moonDir = MoonSky.ComputeMoonDirection(moonProfile.PrimaryOrbit, _gameHour, _gameDay);
        var secundaDir = MoonSky.ComputeMoonDirection(moonProfile.SecondaryOrbit, _gameHour, _gameDay);
        var moonFade = Math.Clamp(1f - (atmo.SunIntensity * 1.4f), 0f, 1f);

        // Phase texture per moon. Morrowind + Oblivion swap among their 8 per-phase textures by the
        // day-driven phase index; games without per-phase art fall through to the full-moon texture in
        // PhaseTextureOrFull. Secunda's phase is offset from Masser's so the moons aren't phase-locked.
        // Phase length prefers the active climate's TNAM value (TESClimate::GetMoonPhaseDays) over the
        // profile fallback — Oblivion's climates author it per-region.
        var phaseLengthDays = _currentClimateTiming.MoonPhaseDays > 0
            ? _currentClimateTiming.MoonPhaseDays
            : moonProfile.PhaseLengthDays;
        var masserPhase = MoonSky.PhaseIndex(_gameDay, phaseLengthDays);
        var secundaPhase = MoonSky.PhaseIndex(_gameDay, phaseLengthDays, moonProfile.SecondaryPhaseOffsetDays);
        var moonTex = PhaseTextureOrFull(_moonPhaseTexIndices, masserPhase, _moonTexIndex);
        var secundaTex = PhaseTextureOrFull(_moonSecundaPhaseTexIndices, secundaPhase, _moonSecundaTexIndex);

        // Per-game moon disc sizes (fraction of the billboard radius → world half-extent). Prefer the
        // engine-exact size read from the loaded ESM's GMSTs (iMasserSize/iSecundaSize ÷ fSunXExtreme —
        // mod-aware), falling back to the per-game SkyMoonProfile default when the GMSTs are absent
        // (Morrowind TES3, DMP/save without a settings table). FO4/FO76's single moon is the SECUNDA
        // slot, so its size comes from iSecundaSize (the secondary GMST fraction).
        var primaryGmstFraction = moonProfile.PrimaryUsesSecundaSize
            ? _data?.MoonSecondaryHalfSizeFraction
            : _data?.MoonPrimaryHalfSizeFraction;
        var primaryFraction = primaryGmstFraction ?? moonProfile.PrimaryHalfSizeFraction;
        var secondaryFraction = _data?.MoonSecondaryHalfSizeFraction ?? moonProfile.SecondaryHalfSizeFraction;
        var moonHalf = SkyBillboardRenderer12.Radius * primaryFraction;
        var moon2Half = SkyBillboardRenderer12.Radius * secondaryFraction;

        _skyBillboards.Render(viewProj, camPos, camRight, camUp,
            sunDir, sunFade, sunTint, _sunDiscTexIndex, _sunGlareTexIndex,
            moonDir, moonFade, moonTex, moonHalf,
            secundaDir, moonFade, secundaTex, moon2Half);
    }

    // The bindless texture index for a moon's current phase, falling back to its full-moon texture when the
    // game ships no per-phase art (that phase slot is NoTexture). Phase is clamped so a bad index can't throw.
    private static uint PhaseTextureOrFull(uint[] phaseIndices, int phase, uint fullMoonIndex)
    {
        var idx = phaseIndices[Math.Clamp(phase, 0, phaseIndices.Length - 1)];
        return idx != SkyBillboardRenderer12.NoTexture ? idx : fullMoonIndex;
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
        // Wind: nothing to do — auto mode reads this weather's wind byte next frame, and an explicit
        // "Override wind" pin deliberately survives weather changes.
    }

    private void LightingPanel_TimeChanged(object? sender, double hour)
    {
        // Continuous input — no _show flag; the next frame's BindAtmosphereConstants reads _gameHour.
        _gameHour = (float)hour;
    }

    private void LightingPanel_DayChanged(object? sender, double day)
    {
        // Drives the moon phase (texture) + orbit position; the next frame's RenderSkyBillboards reads it.
        _gameDay = (float)day;
    }
}
