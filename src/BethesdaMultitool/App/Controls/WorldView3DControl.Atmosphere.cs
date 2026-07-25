using System.IO;
using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif;
using BethesdaMultitool.Core.Formats.Nif.Conversion;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool;

public sealed partial class WorldView3DControl
{
    private string _weatherImageSpaceTelemetry = "inactive";
    private string? _weatherImageSpaceTelemetryLogKey;
    private WeatherImageSpaceEvaluation? _weatherImageSpaceEvaluation;
    private uint? _tonemapBaseImageSpaceFormId;
    private string? _tonemapBaseImageSpaceEditorId;
    private string _tonemapBaseImageSpaceSource = "unavailable";
    private string? _tonemapBaseImageSpaceUnavailableReason;
    private ResolvedImageSpaceSelection _tonemapBaseImageSpaceSelection;

    /// <summary>Last classic WTHR→IMAD selection and sampled tonemap values, for captures/profiling.</summary>
    internal string WeatherImageSpaceTelemetry => _weatherImageSpaceTelemetry;

    // Sky-element texture paths DERIVED from the loaded game's data (not a hardcoded per-game table):
    // sun/glare ← CLMT FNAM/GNAM, stars ← the CLMT MODL sky NIF, clouds ← the active weather's cloud
    // layers, moons ← whichever conventional moon asset the loaded archives actually ship. Any field
    // may be null → that sky element is skipped.
    private sealed record SkyTexturePaths(
        string? Sun, string? SunGlare, string? Cloud, string? Star, string? Moon, string? Secunda);

    private SkyTexturePaths? _resolvedSkyTexturePaths;

    /// <summary>The worldspace selected by the exterior picker, independent of interior mode.</summary>
    private WorldspaceRecord? CurrentSelectedExteriorWorldspace()
    {
        if (_data is null) return null;
        var index = WorldspaceComboBox.SelectedIndex;
        return index >= 0 && index < _data.Worldspaces.Count ? _data.Worldspaces[index] : null;
    }

    /// <summary>
    ///     Canonical sky context for the loaded cell/worldspace. A DATA-bit-7 interior resolves its
    ///     retained parent WRLD and renders the exterior sky; ordinary interiors resolve neither.
    /// </summary>
    private ResolvedSkySceneContext CurrentSkySceneContext()
    {
        var parent = _selectedInterior?.WorldspaceFormId is { } parentId
            ? FindWorldspace(parentId)
            : null;
        return SkySceneContextResolver.Resolve(
            _selectedInterior,
            CurrentSelectedExteriorWorldspace(),
            parent);
    }

    private WorldspaceRecord? CurrentExteriorWorldspace() => CurrentSkySceneContext().Worldspace;

    /// <summary>
    ///     The climate driving the sky for an exterior worldspace or DATA-bit-7 interior, via the
    ///     engine's precedence:
    ///     <list type="number">
    ///         <item>the selected CELL's classic <c>XCCM</c> climate override;</item>
    ///         <item>the worldspace's own climate (WRLD <c>CNAM</c>) — present in FO3/FNV/Skyrim/FO4 and the
    ///         Oblivion worldspaces that set one;</item>
    ///         <item>a global default climate when the worldspace carries none — Oblivion's Tamriel has NO
    ///         <c>CNAM</c> (climate is a rare per-cell <c>XCCM</c> override there — only ~55 in the whole
    ///         277&#160;MB Oblivion.esm, so nothing to sample at cell 0,0), and the engine falls back to a
    ///         global default. Oblivion ships one literally named <c>DefaultClimate</c>.</item>
    ///     </list>
    ///     Returns null for an ordinary interior or unlinked exterior selection. The fallback only fires
    ///     for a sky-bearing CELL/WRLD context whose authored climate doesn't resolve.
    /// </summary>
    private ClimateRecord? ResolveClimate(ResolvedSkySceneContext skyContext)
    {
        if (_data is null || !skyContext.RendersExteriorSky) return null;
        if (skyContext.PreferredClimateFormId is uint climateFormId &&
            _data.ClimatesByFormId.TryGetValue(climateFormId, out var climate))
        {
            return climate;
        }

        if (skyContext.Worldspace is null && skyContext.CellClimateFormId is null)
        {
            return null;
        }

        // A real sky-bearing context with no resolvable override/CNAM uses the game default. This
        // covers climateless exterior worldspaces and flagged interiors whose partial capture omitted
        // the referenced CLMT record, without enabling a sky for ordinary interiors.
        return ResolveDefaultClimate();
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
    private WeatherRecord? ResolveClimateDefaultWeather(ResolvedSkySceneContext skyContext)
    {
        var climate = ResolveClimate(skyContext);
        if (_data is null || climate is null || climate.WeatherTypes.Count == 0) return null;
        var entry = climate.WeatherTypes.FirstOrDefault(w => w.GlobalFormId == 0) ?? climate.WeatherTypes[0];
        return _data.WeathersByFormId.TryGetValue(entry.WeatherFormId, out var w) ? w : null;
    }

    /// <summary>
    ///     The scene atmosphere for the current view. Interiors resolve from their AUTHORED cell
    ///     lighting (XCLL, with LGTM lighting-template fallback) — time-of-day independent, so the
    ///     time slider no longer drives interiors to black at night. Exteriors use the weather/climate
    ///     sun model as before.
    /// </summary>
    private ResolvedWeatherTransition ResolveSelectedWeatherTransition()
    {
        ResolvedRegionWeatherSelection? regionSelection = null;
        if (_weatherSelectionIsClimateDefault &&
            _selectedInterior is null &&
            _data is { Game: BethesdaGame.FalloutNewVegas, RuntimeWeatherTransition: null } data)
        {
            // XCLR is a cell-level candidate list, not proof that the camera is inside a REGN.
            // Resolve at the same camera/projection-focus point used by CELL XCIM selection and
            // require retained RPLI/RPLD containment before a deterministic RDWT may win.
            var point = ProjectionActive ? _projectionFocus : _camera.Position;
            var cellContext = CurrentImageSpaceCellContext();
            regionSelection = RegionWeatherSelectionResolver.Resolve(
                cellContext.Cell,
                point.X,
                point.Y,
                CurrentExteriorWorldspace()?.FormId,
                data.RegionsByFormId,
                data.WeathersByFormId);
        }

        var transition = WeatherTransitionSelectionResolver.Resolve(
            _selectedWeather,
            _weatherSelectionIsClimateDefault,
            _climateDefaultWeather,
            _data?.RuntimeWeatherTransition,
            _data?.WeathersByFormId,
            deterministicRegionWeather: regionSelection?.Weather);
        return regionSelection is { } selection
            ? transition with { Telemetry = $"{transition.Telemetry} {selection.Telemetry}" }
            : transition;
    }

    private AtmosphereState.Resolved ResolveSceneAtmosphere(float gameHour, bool lightingEnabled)
    {
        // Cell DATA flag 0x80 = "behave like exterior" (xEdit): those interiors keep the exterior
        // sun/weather atmosphere by engine design.
        var skyContext = CurrentSkySceneContext();
        if (_selectedInterior is { } cell && _data is not null && !skyContext.BehavesLikeExterior)
        {
            var template = cell.LightingTemplateFormId is { } lgtmId
                           && _data.LightingTemplatesByFormId.TryGetValue(lgtmId, out var lgtm)
                ? lgtm.LightingData
                : null;
            return AtmosphereState.ResolveInterior(
                cell.LightingData, template, cell.LightingTemplateInheritanceFlags ?? 0u, lightingEnabled);
        }

        var weather = ResolveSelectedWeatherTransition();
        return AtmosphereState.ResolveWeatherTransition(
            gameHour, weather.CurrentWeather, weather.OutgoingWeather, weather.CurrentWeatherWeight,
            _currentClimateTiming, lightingEnabled, _data?.Game ?? default,
            GmstFloat("fSunXExtreme"), GmstFloat("fSunYExtreme"), GmstFloat("fSunAlphaTransTime"));
    }

    /// <summary>Refreshes the climate timing + default weather for whatever worldspace is now showing,
    /// then re-applies the dropdown selection (so "(Climate default)" picks up the new default).</summary>
    private void RefreshAtmosphereForCurrentWorldspace()
    {
        var skyContext = CurrentSkySceneContext();
        _currentClimateTiming = AtmosphereState.ClimateTiming.FromClimateData(ResolveClimate(skyContext)?.Timing);
        _climateDefaultWeather = ResolveClimateDefaultWeather(skyContext);
        _skyTexKey = null; // force the sky textures to re-resolve for the new climate/weather next frame
        ApplyWeatherSelection();
    }

    /// <summary>
    ///     Active CELL for image-space selection. Perspective mode follows the camera position;
    ///     orthographic inspection modes follow their visible projection focus because the perspective
    ///     camera is intentionally parked/stale there.
    /// </summary>
    private ImageSpaceCellContext CurrentImageSpaceCellContext()
    {
        if (_selectedInterior is { } selectedInterior)
        {
            return ImageSpaceCellContext.ForSelectedInterior(selectedInterior);
        }

        var focus = ProjectionActive ? _projectionFocus : _camera.Position;
        return ImageSpaceSelectionResolver.ResolveExteriorCell(
            _cellGridLookup,
            focus.X,
            focus.Y,
            _cellSize,
            ProjectionActive
                ? ImageSpaceCellContextSource.ProjectionFocusGrid
                : ImageSpaceCellContextSource.CameraGrid);
    }

    /// <summary>
    ///     Tonemap operator + parameters for the current view. FO3/FNV (and Oblivion as a labeled
    ///     stand-in) reproduce the engine's imagespace HDR stage: the active IMGS resolves like the
    ///     engine's <c>GetUsableImageSpace</c> — active camera/interior CELL XCIM → worldspace INAM → the shipped
    ///     DefaultImageSpaceInterior/Exterior values (0x160/0x161). Other games get their family
    ///     default operator (see <see cref="Core.Formats.Nif.Rendering.Gpu.D3D12.GpuTonemapSettings" />).
    /// </summary>
    private Core.Formats.Nif.Rendering.Gpu.D3D12.GpuTonemapSettings ResolveTonemapSettings()
    {
        _weatherImageSpaceTelemetry = "inactive";
        _weatherImageSpaceEvaluation = null;
        _tonemapBaseImageSpaceFormId = null;
        _tonemapBaseImageSpaceEditorId = null;
        _tonemapBaseImageSpaceSource = "unavailable";
        _tonemapBaseImageSpaceUnavailableReason = null;
        _tonemapBaseImageSpaceSelection = default;
        var skyContext = CurrentSkySceneContext();
        var behavesLikeExterior = skyContext.BehavesLikeExterior;
        var interior = skyContext.IsInterior && !behavesLikeExterior;
        var game = _data?.Game ?? default;
        var weatherTransition = ResolveSelectedWeatherTransition();
        _weatherImageSpaceTelemetry = weatherTransition.Telemetry;
        var activeWeather = weatherTransition.CurrentWeather;
        // TES4 has no interior/exterior HDR split — the weather HNAM (with per-field engine-default
        // substitution) governs everywhere the sky is active.
        var settings = game == Core.Games.BethesdaGame.Oblivion
            ? Core.Formats.Nif.Rendering.Gpu.D3D12.GpuTonemapSettings.ApplyOverrides(
                Core.Formats.Nif.Rendering.Gpu.D3D12.GpuTonemapSettings.ForOblivionWeather(
                    activeWeather?.Hdr))
            : Core.Formats.Nif.Rendering.Gpu.D3D12.GpuTonemapSettings.ForGame(game, interior);
        var engineImagespaceFamily = game is Core.Games.BethesdaGame.Oblivion
            or Core.Games.BethesdaGame.Fallout3
            or Core.Games.BethesdaGame.FalloutNewVegas;
        // Semantic Creation-family IMGS resolution is independent of the selected display operator.
        // Skyrim retains its family while GammaAces is active so the non-imagespace scene scales can
        // run without enabling the still-incomplete CreationModern cinematic/exposure shader.
        var modernImagespaceFamily = settings.ModernFamily is not null;
        var imageSpaceWorld = skyContext.Worldspace;
        var cellContext = CurrentImageSpaceCellContext();
        var imageSpaceSelection = ImageSpaceSelectionResolver.Resolve(
            cellContext,
            imageSpaceWorld,
            _data?.Worldspaces,
            interior,
            useClassicDefault: engineImagespaceFamily);
        _tonemapBaseImageSpaceSelection = imageSpaceSelection;
        var formId = imageSpaceSelection.ImageSpaceFormId;
        // Explicit dropdown pick replaces the resolved cell/worldspace IMGS with the chosen record;
        // the resolver still runs so the Inspection panel can show what Automatic WOULD have used.
        var explicitImagespace = _imagespaceMode == ImagespaceSelectionMode.Explicit;
        if (explicitImagespace)
        {
            formId = _imagespaceExplicitFormId;
        }
        _tonemapBaseImageSpaceFormId = formId;
        _tonemapBaseImageSpaceSource = explicitImagespace
            ? "explicit UI selection"
            : imageSpaceSelection.SourceTelemetry;
        if (_data is null)
        {
            _tonemapBaseImageSpaceUnavailableReason = "world data is unavailable";
        }
        else if (formId is { } sourceFormId)
        {
            if (_data.ImageSpacesByFormId.TryGetValue(sourceFormId, out var sourceImageSpace))
            {
                _tonemapBaseImageSpaceEditorId = sourceImageSpace.EditorId;
            }
            else
            {
                _tonemapBaseImageSpaceUnavailableReason = imageSpaceSelection.Source is
                    ImageSpaceSelectionSource.DefaultInterior or ImageSpaceSelectionSource.DefaultExterior
                    ? "default IMGS record is not retained; the built-in shipped fallback is active"
                    : "selected IMGS FormID is not present in the retained image-space index";
            }
        }
        else
        {
            _tonemapBaseImageSpaceUnavailableReason =
                "no CELL XCIM or inherited WRLD INAM resolved; game-family defaults are active";
        }
        // Retail FNV keeps one continuous adapted-light history across ordinary scene and authored
        // imagespace transitions. Only a real clear epoch enters the semantic key; the GPU pass owns
        // render-target/device/adaptive-mode lifecycle invalidation.
        var historyKey = TonemapHistoryKeyBuilder.Build(game, _tonemapHistoryClearGeneration);

        // GUI post-processing toggles (settings panel, read per frame — no rebuild). HDR off =
        // the LegacyClamp passthrough (visually the pre-HDR look; the float scene target itself is
        // only revertible via the static FALLOUT_VIEWER_HDR=0 kill-switch).
        var hdrEnabled = _hdrEnabled
            && Environment.GetEnvironmentVariable("FALLOUT_VIEWER_HDR") != "0";
        if (!hdrEnabled)
        {
            return settings with
            {
                Mode = Core.Formats.Nif.Rendering.Gpu.D3D12.GpuTonemapMode.LegacyClamp,
                BloomEnabled = false,
                HistoryKey = historyKey,
                EmissiveMult = Core.Formats.Nif.Rendering.Gpu.D3D12.GpuTonemapSettings
                    .ResolveEmissiveMult(settings.EmissiveMult, null, hdrEnabled: false,
                        imagespaceModifiersEnabled: _imagespaceMode != ImagespaceSelectionMode.None),
            };
        }

        // Tonemap operator overrides are display-pass A/Bs; they must not suppress the raw FO3/FNV
        // imagespace values consumed by the scene pass (notably EmissiveMult).
        if ((!engineImagespaceFamily && !modernImagespaceFamily) || _data is null)
        {
            return settings with
            {
                BloomEnabled = settings.BloomEnabled && _bloomEnabled,
                HistoryKey = historyKey,
            };
        }

        // Imagespace "(None)" = skip the authored IMGS overlay (cinematic grade + HDR params)
        // and render with a neutral scene grade; the eye-adapt exposure stays.
        if (_imagespaceMode == ImagespaceSelectionMode.None)
        {
            settings = settings with
            {
                EmissiveMult = Core.Formats.Nif.Rendering.Gpu.D3D12.GpuTonemapSettings
                    .ResolveEmissiveMult(settings.EmissiveMult, null, hdrEnabled: true,
                        imagespaceModifiersEnabled: false),
                Saturation = 1f,
                ContrastAvgLum = 0.5f,
                Contrast = 1f,
                Brightness = 1f,
                TintR = 1f,
                TintG = 1f,
                TintB = 1f,
                TintAmount = 0f,
                CinematicFlags = ImageSpaceCinematicFlags.All,
                SunlightScale = 1f,
                SkyScale = 1f,
            };
        }

        if (_imagespaceMode != ImagespaceSelectionMode.None
            && formId is { } id && _data.ImageSpacesByFormId.TryGetValue(id, out var imgs))
        {
            if (modernImagespaceFamily)
            {
                settings = Core.Formats.Nif.Rendering.Gpu.D3D12.GpuTonemapSettings
                    .ApplyModernImageSpace(settings, imgs);
            }
            else if (imgs.Hdr is { } hdr)
            {
                settings = settings with
                {
                    // TESImageSpace::Load copies this complete block. Zero is authored data for every
                    // field, not only EmissiveMult/BrightClamp; do not silently substitute FNV defaults.
                    TargetLum = hdr.TargetLum,
                    UpperLumClamp = hdr.UpperLumClamp,
                    EyeAdaptSpeed = hdr.EyeAdaptSpeed,
                    // Raw engine copy: zero is authored data, not an "unset" sentinel.
                    EmissiveMult = Core.Formats.Nif.Rendering.Gpu.D3D12.GpuTonemapSettings
                        .ResolveEmissiveMult(settings.EmissiveMult, hdr.EmissiveMult,
                            hdrEnabled: true, imagespaceModifiersEnabled: true),
                    BlurRadius = hdr.BlurRadius,
                    BlurPasses = hdr.BlurPasses,
                    BrightScale = hdr.BrightScale,
                    BrightClamp = hdr.BrightClamp,
                    // ImageSpaceManager::ApplyCurrentParameterData copies classic hdrData[11]
                    // into the global consumed by directional-light setup. Keep it in scene state;
                    // it is not part of the display composite.
                    SunlightScale = hdr.SunlightDimmer,
                };
            }

            if (imgs.Cinematic is { } cin)
            {
                settings = settings with
                {
                    Saturation = cin.Saturation,
                    ContrastAvgLum = cin.ContrastAvgLum,
                    Contrast = cin.Contrast,
                    Brightness = cin.Brightness,
                    // Retain the parsed source mask for capture telemetry. All classic DNAM sizes
                    // author it; Creation-era split/packed cinematic blocks do not. The shipped
                    // FO3/FNV composite shader itself does not consume this metadata.
                    CinematicFlags = Core.Formats.Nif.Rendering.Gpu.D3D12.GpuTonemapSettings
                        .ResolveCinematicFlags(settings.CinematicFlags, cin),
                };
            }

            if (imgs.Tint is { } tint)
            {
                settings = settings with
                {
                    TintR = tint.Red,
                    TintG = tint.Green,
                    TintB = tint.Blue,
                    TintAmount = tint.Amount,
                };
            }
        }

        // Creation-era WTHR IMSP entries reference IMGS records directly. FO4's recovered
        // Sky::UpdateHDRValues WeightedAdd path selects two semantic time bands and, during a
        // weather transition, two weathers (up to four base-data/LUT contributions).
        // Weather IMAD bands only ride the Automatic resolution — an explicit dropdown pick is an
        // atomic "show me this IMGS" preview and (None) is neutral by definition.
        if (_imagespaceMode == ImagespaceSelectionMode.Automatic && modernImagespaceFamily && !interior
            && (activeWeather?.ImageSpaceModifiers is not null
                || weatherTransition.OutgoingWeather?.ImageSpaceModifiers is not null))
        {
            var weatherEvaluation = Core.Formats.Nif.Rendering.Gpu.D3D12.WeatherImageSpaceEvaluator
                .EvaluateModern(settings, _gameHour, _currentClimateTiming, activeWeather,
                    weatherTransition.OutgoingWeather, weatherTransition.CurrentWeatherWeight,
                    _data.ImageSpacesByFormId);
            settings = weatherEvaluation.Settings;
            _weatherImageSpaceEvaluation = weatherEvaluation;
            _weatherImageSpaceTelemetry = $"{weatherTransition.Telemetry} {weatherEvaluation.Telemetry}";
            if (_skyDiag && !string.Equals(_weatherImageSpaceTelemetryLogKey, _weatherImageSpaceTelemetry,
                    StringComparison.Ordinal))
            {
                _weatherImageSpaceTelemetryLogKey = _weatherImageSpaceTelemetry;
                Log.Info($"[WeatherIMAD] {_weatherImageSpaceTelemetry}");
            }
        }

        // FNV/FO3 apply the active WTHR's two selected time-band IMADs after the base IMGS. Runtime
        // climate-default views retain Sky's outgoing/current pair; explicit previews stay atomic.
        // The IMAD elapsed clock is not in the recovered Sky layout, so animatable timelines are
        // explicitly gated as unknown instead of silently sampling a fabricated t=0.
        if (_imagespaceMode == ImagespaceSelectionMode.Automatic && !interior
            && game is BethesdaGame.Fallout3 or BethesdaGame.FalloutNewVegas
            && (activeWeather?.ImageSpaceModifiers is not null
                || weatherTransition.OutgoingWeather?.ImageSpaceModifiers is not null))
        {
            var weatherEvaluation = Core.Formats.Nif.Rendering.Gpu.D3D12.WeatherImageSpaceEvaluator.Evaluate(
                settings, _gameHour, _currentClimateTiming, activeWeather,
                weatherTransition.OutgoingWeather, weatherTransition.CurrentWeatherWeight,
                _data.ImageSpaceModifiersByFormId,
                modifierElapsedSeconds: weatherTransition.UsesRuntimeTransition
                    ? weatherTransition.ModifierElapsedSeconds
                    : 0f);
            settings = weatherEvaluation.Settings;
            _weatherImageSpaceEvaluation = weatherEvaluation;
            _weatherImageSpaceTelemetry = $"{weatherTransition.Telemetry} {weatherEvaluation.Telemetry}";
            if (_skyDiag && !string.Equals(_weatherImageSpaceTelemetryLogKey, _weatherImageSpaceTelemetry,
                    StringComparison.Ordinal))
            {
                _weatherImageSpaceTelemetryLogKey = _weatherImageSpaceTelemetry;
                Log.Info($"[WeatherIMAD] {_weatherImageSpaceTelemetry}");
            }
        }

        settings = Core.Formats.Nif.Rendering.Gpu.D3D12.GpuTonemapSettings.ApplyOverrides(settings);
        // The GUI Bloom toggle wins over the FALLOUT_VIEWER_BLOOM=1 force-on (debug knob).
        return settings with
        {
            BloomEnabled = settings.BloomEnabled && _bloomEnabled,
            HistoryKey = historyKey,
        };
    }

    private WorldspaceRecord? FindWorldspace(uint formId) =>
        _data?.Worldspaces.FirstOrDefault(w => w.FormId == formId);

    // Resolves the sky-element bindless texture indices for the current climate + active weather,
    // re-running only when either changes. Runs inside the render frame (a command list is open for the
    // texture cache's first upload). Everything is DERIVED from the loaded data — sun/glare from the CLMT
    // (FNAM/GNAM), stars from the climate's MODL sky NIF, clouds from the active weather's cloud layers —
    // with the genuinely engine-side moon resolved by probing whichever moon asset the game actually ships.
    private void EnsureSkyTexturesResolved()
    {
        if (_textureResolver12 is null) return; // retry once a worldspace (and its resolver) has loaded
        var resolver = _textureResolver12;
        var skyContext = CurrentSkySceneContext();
        var climate = ResolveClimate(skyContext);
        var transition = ResolveSelectedWeatherTransition();
        var weather = transition.CurrentWeather;
        var outgoingWeather = transition.OutgoingWeather;
        var key = (
            climate?.FormId ?? 0u,
            weather?.FormId ?? 0u,
            outgoingWeather?.FormId ?? 0u);
        if (_skyTexKey == key)
        {
            // A runtime weather percentage changes continuously. It updates the retained cloud state,
            // not the sky topology: reloading and recooking Clouds.nif for every new float bit-pattern
            // turns one weather transition into per-frame archive I/O and geometry allocation.
            _skyGeometry?.UpdateCloudWeatherTransition(
                weather, outgoingWeather, transition.CurrentWeatherWeight,
                _data?.Game ?? BethesdaGame.Unknown);
            return;
        }
        _skyTexKey = key;

        const uint none = BethesdaMultitool.Core.Formats.Nif.Rendering.Camera.D3D12.SkyBillboardRenderer12.NoTexture;
        var paths = ResolveSkyTexturePaths(climate, weather);
        _resolvedSkyTexturePaths = paths;

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
        RebuildSkyGeometry(climate, weather, outgoingWeather, transition.CurrentWeatherWeight);
    }

    // Loads the climate's real sky-dome NIFs (the engine's own sky set — atmosphere, stars, clouds, in
    // render order) and hands their geometry + per-layer textures to the sky-geometry renderer. The cloud
    // layers' textures come from the ACTIVE WEATHER (version-driven), so the sky is drawn from the loaded
    // data, not a procedural approximation. Cleared for interiors / no-climate worldspaces.
    private void RebuildSkyGeometry(
        ClimateRecord? climate,
        WeatherRecord? weather,
        WeatherRecord? outgoingWeather,
        float currentWeatherWeight)
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
        var hasOutgoing = outgoingWeather is not null;
        var currentCloudWeight = hasOutgoing ? Math.Clamp(currentWeatherWeight, 0f, 1f) : 1f;
        var outgoingCloudWeight = hasOutgoing ? 1f - currentCloudWeight : 0f;
        if (_data?.Game == BethesdaMultitool.Core.Games.BethesdaGame.Morrowind)
        {
            // Morrowind's sky set is ENGINE-HARDCODED, not climate-authored — the NIF names sit in
            // Morrowind.exe beside their "Failed to load sky stars/clouds" error strings:
            // sky_night_01/02.nif are the stars domes and sky_clouds_01.nif is the cloud dome,
            // retextured per weather from the INI's Cloud Texture (already carried on the synthetic
            // weather's CloudLayerTextures). sky_atmosphere.nif (the vertex-tinted gradient dome) is
            // deliberately NOT loaded: the renderer's own gradient dome already draws the resolved
            // sky colors.
            //
            // The night domes are ALTERNATES, not layers: both carry the same seven constellation/
            // nebula shapes (Mage/Warrior/Thief + star field + 3 nebulae) with slightly different
            // geometry — 02 is Bloodmoon's replacement night sky (it ships in Bloodmoon.bsa with the
            // _02 nebula textures). Drawing both stacks two near-identical star fields a few degrees
            // apart and every star doubles, so load 02 and fall back to 01 only when it's absent.
            var beforeNight = layers.Count;
            AddSkyNifLayers(layers, "sky_night_02.nif", weather, currentCloudWeight);
            if (layers.Count == beforeNight)
            {
                AddSkyNifLayers(layers, "sky_night_01.nif", weather, currentCloudWeight);
            }

            AddSkyNifLayers(layers, "sky_clouds_01.nif", weather, currentCloudWeight);
            if (outgoingWeather is not null)
            {
                AddSkyNifLayers(layers, "sky_clouds_01.nif", outgoingWeather,
                    outgoingCloudWeight, cloudsOnly: true, isOutgoingCloudPass: true);
            }

            // Morrowind fogs its sky domes with linear distance fog, and the cloud cap's shape
            // encodes its own horizon fade: rim vertices sit ~3.4× farther than the zenith (|p|
            // ≈ 1004 vs 292), so in-engine the rim dissolves into the horizon haze while overhead
            // stays clear. The mesh carries NO baked fade (vertex alpha is 255 everywhere, unlike
            // FNV's cloud caps), so without fog the dome ends in a hard horizontal edge against
            // the gradient. Synthesize the fog fade into the vertex alpha the shader already
            // multiplies. Stars keep their authored look — the night domes are near-spherical, so
            // engine fog barely grades them.
            foreach (var layer in layers)
            {
                if (layer.Type == SkyObjectType.Clouds)
                {
                    SynthesizeCloudHorizonFade(layer);
                }
            }
        }
        else if (climate?.ModelPath is string modl && !string.IsNullOrWhiteSpace(modl))
        {
            // The climate MODL is the stars dome (e.g. "Sky\Stars.nif"); the exact stars/cloud layers
            // remain active on both paths. Atmosphere.nif is an architectural replacement and stays
            // opt-in until its cross-game capture matrix passes; otherwise the procedural dome remains
            // the compatibility fallback.
            var skyDir = Path.GetDirectoryName(modl.Replace('/', '\\')) ?? string.Empty;
            if (AuthoredSkyArchitecture.ShouldLoadAtmosphereNif(AuthoredSkyArchitecture.Enabled))
            {
                AddSkyNifLayers(layers, Path.Combine(skyDir, "Atmosphere.nif"), weather,
                    currentCloudWeight);
            }
            AddSkyNifLayers(layers, modl, weather, currentCloudWeight);
            var cloudsPath = Path.Combine(skyDir, "Clouds.nif");
            AddSkyNifLayers(layers, cloudsPath, weather, currentCloudWeight);
            if (outgoingWeather is not null)
            {
                // Retain both texture candidates for the lifetime of the weather pair, even at a zero-
                // weight endpoint. The cache key deliberately excludes the continuously changing weight;
                // draw-time suppression handles zero while preserving topology for a later lower weight.
                AddSkyNifLayers(layers, cloudsPath, outgoingWeather,
                    outgoingCloudWeight, cloudsOnly: true, isOutgoingCloudPass: true);
            }
        }
        else
        {
            return;
        }

        if (outgoingWeather is not null)
        {
            ApplyCloudWeatherTransition(
                layers, weather, outgoingWeather, currentCloudWeight,
                _data?.Game ?? BethesdaGame.Unknown);
        }

        if (_skyDiag)
        {
            var cloudTex = weather?.CloudLayerTextures is { Count: > 0 } ct ? string.Join(", ", ct) : "(none)";
            Log.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "[SkyGeo] game={0} climate={1} modl='{2}' weather={3} outgoing={4}@{5:0.###} cloudTex=[{6}] => layers: {7} stars, {8} clouds (of {9} total)",
                _data?.Game, climate?.EditorId, climate?.ModelPath ?? "(game-keyed)",
                weather?.EditorId ?? "(default)", outgoingWeather?.EditorId ?? "(none)",
                outgoingCloudWeight, cloudTex,
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

    // Fraction of the cloud dome's authored distance range where the horizon fog fade begins
    // (fade runs over the outer 40% — full alpha above ~12° elevation, dissolved at the ~5° rim).
    // TO-CONFIRM (stand-in): the engine's fog start/end for the sky pass hasn't been decompiled;
    // this matches the vanilla look of clouds melting into the horizon haze. Refine against an
    // OpenMW hour sweep if the band reads too wide/narrow.
    private const float CloudHorizonFogStartFraction = 0.6f;

    // Bakes Morrowind's sky fog into a cloud layer's vertex alpha: linear fade by authored vertex
    // distance (the engine fogs the sky domes; the shallow cloud cap's rim is ~3.4× farther than
    // its zenith, so distance IS the horizon fade profile). Mutates the layer's color array in
    // place (freshly extracted per load, so nothing else holds it).
    private static void SynthesizeCloudHorizonFade(SkyGeometryLayer layer)
    {
        var pos = layer.Positions;
        var vertexCount = pos.Length / 3;
        if (vertexCount == 0) return;

        var minDist = float.MaxValue;
        var maxDist = float.MinValue;
        var dists = new float[vertexCount];
        for (var v = 0; v < vertexCount; v++)
        {
            var x = pos[v * 3];
            var y = pos[(v * 3) + 1];
            var z = pos[(v * 3) + 2];
            var d = MathF.Sqrt((x * x) + (y * y) + (z * z));
            dists[v] = d;
            minDist = MathF.Min(minDist, d);
            maxDist = MathF.Max(maxDist, d);
        }

        var range = maxDist - minDist;
        if (range <= 1e-3f) return; // spherical layer — fog wouldn't grade it

        var fogStart = minDist + (range * CloudHorizonFogStartFraction);
        var fogSpan = MathF.Max(maxDist - fogStart, 1e-3f);

        var colors = layer.VertexColors;
        if (colors is null)
        {
            colors = new byte[vertexCount * 4];
            Array.Fill(colors, (byte)255);
            layer.VertexColors = colors;
        }

        for (var v = 0; v < vertexCount; v++)
        {
            var fade = Math.Clamp((maxDist - dists[v]) / fogSpan, 0f, 1f);
            var a = (v * 4) + 3;
            if (a < colors.Length)
            {
                colors[a] = (byte)(colors[a] * fade);
            }
        }
    }

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
    // cloud-layer texture by authored source-shape index; the atmosphere gradient needs none.
    private void AddSkyNifLayers(
        List<SkyGeometryLayer> layers,
        string modlPath,
        WeatherRecord? weather,
        float cloudWeatherWeight = 1f,
        bool cloudsOnly = false,
        bool isOutgoingCloudPass = false)
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
            if (resolved is not SkyObjectType type ||
                type is not (SkyObjectType.Sky or SkyObjectType.Stars or SkyObjectType.Clouds) ||
                sm.Triangles.Length == 0)
            {
                continue;
            }

            if (cloudsOnly && type != SkyObjectType.Clouds)
            {
                continue;
            }

            var texIndex = uint.MaxValue;
            var scrollSpeed = Vector2.Zero;
            var cloudSourceIndex = -1;
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
                // Skyrim's Clouds.nif root owns 29 shapes directly and in source order. Retail weathers
                // commonly author only slots 15/19/20 (?0TX/C0TX/D0TX). Bind those textures to root
                // children 15/19/20, not to the first three dense extracted shapes.
                var authoredLayer = weather?.FindCloudLayerBySourceIndex(layerIndex);
                var cloudPath = authoredLayer?.Texture;
                var sourceLayerIndex = authoredLayer?.SourceIndex ?? layerIndex;
                cloudSourceIndex = sourceLayerIndex;
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
                    var cc = WeatherCloudColor(weather, sourceLayerIndex);
                    var a = cc is null ? "n/a" : $"R{cc.Day.R} G{cc.Day.G} B{cc.Day.B} A{cc.Day.A}";
                    Log.Info($"[SkyGeo]     cloud shape #{layerIndex}: sourceLayer={sourceLayerIndex} layer='{cloudPath}' -> DRAW (tex#{texIndex}) dayColor=[{a}]");
                }

                // This layer's per-draw color (PNAM), per-layer opacity (JNAM) + drift speed (QNAM/RNAM) —
                // the engine drives each cloud layer's color/opacity/scroll independently
                // (SkyShader::SetupGeometryConstants / Clouds::Update). Skyrim's texture signatures can be
                // sparse (?0TX/C0TX/D0TX = 15/19/20), so every parallel array lookup uses the retained
                // SOURCE index rather than the dense shape/texture ordinal. JNAM (modern weathers)
                // hides/thins layers per weather; FO3/FNV carry none, so cloudAlpha stays null.
                cloudColor = authoredLayer?.Color ?? WeatherCloudColor(weather, sourceLayerIndex);
                cloudAlpha = authoredLayer?.Opacity ?? WeatherCloudAlphaFor(weather, sourceLayerIndex);
                scrollSpeed = WeatherCloudMotion.Resolve(
                    weather, authoredLayer, sourceLayerIndex,
                    _data?.Game ?? BethesdaGame.Unknown);
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
                CloudSourceIndex = cloudSourceIndex,
                IsOutgoingCloudPass = type == SkyObjectType.Clouds && isOutgoingCloudPass,
                CloudWeatherWeight = type == SkyObjectType.Clouds ? cloudWeatherWeight : 1f,
            });
        }
    }

    /// <summary>
    ///     Applies the recovered one-offset weather-transition contract after the current/outgoing
    ///     texture candidates have been resolved. Equal textures collapse to one draw. Differing
    ///     textures temporarily keep weighted draws, but share the exact blended velocity, PNAM, and
    ///     JNAM state until the dual-texture pixel equation is recovered under SKY-15.
    /// </summary>
    private static void ApplyCloudWeatherTransition(
        List<SkyGeometryLayer> layers,
        WeatherRecord? currentWeather,
        WeatherRecord outgoingWeather,
        float currentWeatherWeight,
        BethesdaGame game)
    {
        var sourceIndices = layers
            .Where(static layer => layer.Type == SkyObjectType.Clouds && layer.CloudSourceIndex >= 0)
            .Select(static layer => layer.CloudSourceIndex)
            .Distinct()
            .ToArray();

        foreach (var sourceIndex in sourceIndices)
        {
            var transition = WeatherCloudTransitionResolver.Resolve(
                currentWeather, outgoingWeather, sourceIndex, currentWeatherWeight, game);
            var currentColor = transition.CurrentLayer?.Color
                               ?? WeatherCloudColor(currentWeather, sourceIndex);
            var outgoingColor = transition.OutgoingLayer?.Color
                                ?? WeatherCloudColor(outgoingWeather, sourceIndex);
            var currentAlpha = transition.CurrentLayer?.Opacity
                               ?? WeatherCloudAlphaFor(currentWeather, sourceIndex);
            var outgoingAlpha = transition.OutgoingLayer?.Opacity
                                ?? WeatherCloudAlphaFor(outgoingWeather, sourceIndex);

            foreach (var layer in layers.Where(layer =>
                         layer.Type == SkyObjectType.Clouds &&
                         layer.CloudSourceIndex == sourceIndex))
            {
                layer.ScrollSpeed = transition.ScrollVelocity;
                layer.CloudColor = currentColor;
                layer.OutgoingCloudColor = outgoingColor;
                layer.CloudAlpha = currentAlpha;
                layer.OutgoingCloudAlpha = outgoingAlpha;
                layer.CloudCurrentWeatherWeight = transition.CurrentWeatherWeight;
                layer.CloudWeatherWeight = layer.IsOutgoingCloudPass
                    ? transition.OutgoingTextureWeight
                    : transition.CurrentTextureWeight;
            }

            if (transition.UsesSameTexture)
            {
                layers.RemoveAll(layer =>
                    layer.Type == SkyObjectType.Clouds &&
                    layer.CloudSourceIndex == sourceIndex &&
                    layer.IsOutgoingCloudPass);
            }
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
    // <paramref name="billboardBasis"/> overrides the sun/moon billboard camera basis — the ortho
    // projection modes pass their own camera axes because the perspective camera is parked/stale there.
    private void RenderSky(Matrix4x4 viewProj, Vector3 domeCenter,
        (Vector3 Right, Vector3 Up)? billboardBasis = null,
        float? animationTimeSeconds = null,
        float skyColorScale = 1f)
    {
        EnsureSkyTexturesResolved();
        var atmo = AtmosphereState.ApplySkyColorScale(
            ResolveSceneAtmosphere(_gameHour, lightingEnabled: true), skyColorScale);
        var exterior = CurrentSkySceneContext().RendersExteriorSky;

        // The weather already authors per-layer PNAM tint and JNAM opacity. Keep the host gates neutral:
        // the old blue night tint, 0.55 opacity ceiling, and night multiplier all double-graded those values.
        var cloudTint = Vector3.One;
        var cloudOpacity = exterior ? 1f : 0f;
        var starTint = new Vector3(atmo.StarsColor.X, atmo.StarsColor.Y, atmo.StarsColor.Z);
        // WTHR celestial rows are RGBX. Keep the raw X byte in telemetry/data, but visibility is
        // supplied by the sky state rather than that commonly-zero padding byte.
        var starFade = atmo.StarsDrawAlpha;
        var domeStarFade = exterior ? starFade : 0f;
        // Real sky-dome NIF geometry centered on the camera: atmosphere gradient + stars + cloud layers,
        // each on its authored UVs. Per-layer textures were resolved in EnsureSkyTexturesResolved.
        _skyGeometry?.Render(viewProj, domeCenter,
            atmo.SkyTopColor, atmo.SkyLowerColor, atmo.AuthoredHorizonColor, atmo.SkyHorizonColor,
            cloudTint, cloudOpacity, starTint, domeStarFade, _gameHour, _currentClimateTiming,
            _data?.Game ?? BethesdaGame.Unknown, animationTimeSeconds);

        if (exterior)
        {
            RenderSkyBillboards(viewProj, atmo, domeCenter, billboardBasis);
        }
    }

    // Draws the textured sun (disc + glare, day) + moon (night) billboards after the sky gradient. Sun
    // direction/intensity come from the decompile-grounded AtmosphereState (already resolved by the
    // caller with lighting forced on); the moon uses the game-family profile's recovered path where an
    // oracle exists (FO3/FNV rotated arm, Skyrim per-moon rotated arms, FO4-family triangle wave).
    private void RenderSkyBillboards(Matrix4x4 viewProj, AtmosphereState.Resolved atmo, Vector3 domeCenter,
        (Vector3 Right, Vector3 Up)? basisOverride = null)
    {
        if (_skyBillboards is null)
        {
            return;
        }

        Vector3 camRight, camUp;
        if (basisOverride is { } basis)
        {
            // Ortho projection modes: the perspective camera is parked wherever the user left it,
            // so the billboards face the projection camera's axes instead.
            (camRight, camUp) = basis;
        }
        else
        {
            // Camera world basis from the inverse view matrix (System.Numerics row-vector: invView's
            // rows are the camera's world-space right/up).
            if (!Matrix4x4.Invert(_camera.GetViewMatrix(), out var invView))
            {
                return;
            }

            camRight = Vector3.Normalize(new Vector3(invView.M11, invView.M12, invView.M13));
            camUp = Vector3.Normalize(new Vector3(invView.M21, invView.M22, invView.M23));
        }
        // Billboard origin = the dome center. The live frame passes 0 (camera-relative, with a translation-
        // free viewProj) so the sun/moon don't jitter at far-from-origin camera positions; the offscreen
        // capture passes the absolute camera position. camRight/camUp are pure directions either way.
        var camPos = domeCenter;

        // FO4 applies fSunShadowMinAngle only to the directional-light copy. Its base and glare nodes
        // receive the unfloored SunPos, exposed separately here so the billboard stays on its authored
        // triangle path near dawn/dusk.
        var sunDir = atmo.SunBillboardDirection;
        var sunTint = new Vector3(atmo.SunDiscColor.X, atmo.SunDiscColor.Y, atmo.SunDiscColor.Z);
        var sunFade = atmo.SunDiscDrawAlpha;
        var sunGlareTint = new Vector3(atmo.SunGlareColor.X, atmo.SunGlareColor.Y, atmo.SunGlareColor.Z);
        var sunGlareFade = atmo.SunGlareDrawAlpha;
        var sunSizes = SkySunProfile.ForGame(_data?.Game ?? BethesdaGame.Unknown)
            .ResolveBillboardHalfSizes(
                SkyBillboardRenderer12.Radius,
                _gameHour,
                _currentClimateTiming,
                GmstFloat("fSunXExtreme"),
                GmstFloat("fSunYExtreme"),
                alphaTransitionHours: GmstFloat("fSunAlphaTransTime"));

        // Game-family moon paths (MoonSky, decompile-grounded). Absolute day participates in recovered
        // arm state, but a 24-hour Masser returns to the same nightly route while Skyrim's 20-hour Secunda
        // drifts by 72 degrees/day; day always advances phase. The renderer skips below-horizon moons and
        // omits Secunda entirely for single-moon games.
        var moonProfile = MoonProfile;
        var masserSpeed = GmstFloat("fMasserSpeed");
        var masserInclination = GmstFloat("fMasserZOffset");
        var secundaSpeed = GmstFloat("fSecundaSpeed");
        var secundaInclination = GmstFloat("fSecundaZOffset");
        var moonDir = moonProfile.Direction(
            secondary: false, _gameHour, _gameDay, _currentClimateTiming,
            masserSpeed, masserInclination);
        var secundaDir = moonProfile.HasSecondMoon
            ? moonProfile.Direction(
                secondary: true, _gameHour, _gameDay, _currentClimateTiming,
                secundaSpeed, secundaInclination)
            : Vector3.UnitZ;
        var moonTint = new Vector3(atmo.MoonGlareColor.X, atmo.MoonGlareColor.Y, atmo.MoonGlareColor.Z);
        var fallbackMoonFade = Math.Clamp(1f - (atmo.SunIntensity * 1.4f), 0f, 1f);
        var recoveredAngularFade = moonProfile.PathFamily is
            MoonPathFamily.FalloutRotatedArm or MoonPathFamily.SkyrimRotatedArm;
        var moonFade = atmo.MoonDiscDrawAlpha(recoveredAngularFade
            ? moonProfile.RotatedArmDiscFade(
                secondary: false, _gameHour, _gameDay,
                masserSpeed,
                GmstFloat("fMasserAngleFadeStart"),
                GmstFloat("fMasserAngleFadeEnd"))
            : fallbackMoonFade);
        var secundaFade = atmo.MoonDiscDrawAlpha(recoveredAngularFade
            ? moonProfile.RotatedArmDiscFade(
                secondary: true, _gameHour, _gameDay,
                secundaSpeed,
                GmstFloat("fSecundaAngleFadeStart"),
                GmstFloat("fSecundaAngleFadeEnd"))
            : fallbackMoonFade);

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

        // Deliberately-black new-moon stubs (FO3/FNV masser_new, and the same Oblivion family) are not
        // drawn — the shipped asset is an all-black disc that would render as an opaque quad over the
        // stars. Skipping the draw IS the new-moon look.
        if (moonProfile.HiddenPhaseIndex is { } hidden)
        {
            if (masserPhase == hidden) moonTex = SkyBillboardRenderer12.NoTexture;
            if (secundaPhase == hidden) secundaTex = SkyBillboardRenderer12.NoTexture;
        }

        // Per-game moon disc sizes (fraction of the billboard radius → world half-extent). Prefer the
        // recovered size read from the loaded ESM's GMSTs (fixed 512-unit arm for FNV/Skyrim; authored
        // path radius for FO4), falling back to the per-game SkyMoonProfile default when inputs are absent
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
            sunDir, sunFade, sunTint, sunGlareFade, sunGlareTint,
            sunSizes.Disc, sunSizes.Glare, _sunDiscTexIndex, _sunGlareTexIndex,
            moonDir, moonFade, moonTint, moonTex, moonHalf,
            secundaDir, secundaFade, moonTint, secundaTex, moon2Half);
    }

    private float? GmstFloat(string editorId)
    {
        return _data?.GameSettingsByEditorId.TryGetValue(editorId, out var setting) == true
            ? setting.FloatValue
            : null;
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
        _weatherSelectionIsClimateDefault = sel.IsClimateDefault;
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
        _weatherSelectionIsClimateDefault = sel.IsClimateDefault;
        _selectedWeather = sel.IsClimateDefault ? _climateDefaultWeather : sel.Weather;
        _skyTexKey = null;
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
