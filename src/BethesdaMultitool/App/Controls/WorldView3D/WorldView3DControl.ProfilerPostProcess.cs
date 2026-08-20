using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool;

/// <summary>
///     Profiler-only projection of the viewer's live post-process switches. The first four values
///     are the existing private UI gates; the effective values include the renderer's HDR kill
///     switch and the active imagespace's authored bloom flag.
/// </summary>
internal sealed record WorldView3DProfilerPostProcessState(
    bool HdrEnabled,
    bool BloomEnabled,
    bool ImagespaceEnabled,
    bool FogEnabled,
    bool EffectiveHdrEnabled,
    bool EffectiveBloomEnabled,
    string TonemapMode,
    string? BaseImageSpaceEditorId,
    string BaseImageSpaceSource,
    float ResolvedSunlightScale,
    float SceneSunlightScale,
    bool ShadowsEnabled,
    string TonemapGuiMode = "");

/// <summary>Retail placed-reference identity retained alongside a profiler scenario fixture.</summary>
internal sealed record WorldView3DProfilerFixture(
    uint ReferenceFormId,
    uint BaseFormId,
    string? BaseEditorId,
    string ModelPath,
    uint CellFormId,
    uint WorldspaceFormId,
    Vector3 Position,
    Vector3 RotationRadians,
    float Scale);

public sealed partial class WorldView3DControl
{
    /// <summary>Read back both the private gates and the effective tonemap immediately before capture.</summary>
    internal WorldView3DProfilerPostProcessState Profiler_PostProcessState
    {
        get
        {
            var tonemap = ResolveTonemapSettings();
            var tonemapAvailable = Environment.GetEnvironmentVariable("FALLOUT_VIEWER_HDR") != "0";
            var hdrActive = HdrGuiActive && tonemapAvailable;
            var modeTraits = GpuTonemapModeTraits.For(tonemap.Mode, tonemapAvailable);
            var sceneSunlightScale = GpuTonemapSettings.ResolveSceneSunlightScale(
                tonemap,
                _data?.Game ?? BethesdaGame.Unknown,
                hdrActive,
                isInterior: _selectedInterior is not null);
            return new WorldView3DProfilerPostProcessState(
                HdrGuiActive,
                _bloomEnabled,
                _imagespaceMode != ImagespaceSelectionMode.None,
                _showFog,
                tonemapAvailable && modeTraits.IsHdrDisplayOperator,
                modeTraits.AllowsClassicBloom && tonemap.BloomEnabled && tonemap.BrightScale > 0f,
                tonemap.Mode.ToString(),
                _tonemapBaseImageSpaceEditorId,
                _tonemapBaseImageSpaceSource,
                tonemap.SunlightScale,
                sceneSunlightScale,
                _showShadows,
                _tonemapGuiMode.ToString());
        }
    }

    /// <summary>
    ///     Apply a scenario step through the same private gates the settings-panel handlers update.
    ///     The controls are synchronized as well, so an unattended run cannot leave the visible UI
    ///     claiming a state different from the frame renderer's state.
    /// </summary>
    internal void Profiler_SetPostProcessState(
        bool hdrEnabled,
        bool bloomEnabled,
        bool imagespaceEnabled,
        bool fogEnabled,
        bool shadowsEnabled = true)
    {
        // The bool scenario contract predates the retail three-way radio: hdrEnabled maps onto
        // Hdr/Sdr (the funnel reseats the radio control), and bloomEnabled stays the NON-UI
        // diagnostic AND-gate so scenarios can A/B bloom independently under HDR.
        SetTonemapGuiMode(hdrEnabled ? GpuTonemapGuiMode.Hdr : GpuTonemapGuiMode.Sdr);
        _bloomEnabled = bloomEnabled;
        _showFog = fogEnabled;
        _showShadows = shadowsEnabled;

        // The bool scenario contract maps onto the dropdown's Automatic/None entries (explicit IMGS
        // picks are a GUI-only affordance); this also syncs the ComboBox selection.
        SetImagespaceSelection(imagespaceEnabled
            ? ImagespaceSelectionMode.Automatic
            : ImagespaceSelectionMode.None);
        LightingPanel.FogEnabled = fogEnabled;
        LightingPanel.ShadowsEnabled = shadowsEnabled;
    }

    /// <summary>
    ///     Resolve an authored fixture from the loaded semantic ESM rather than trusting a camera
    ///     bookmark alone. Both FormIDs and the model path are required, and the reference must be
    ///     owned by the requested worldspace.
    /// </summary>
    internal WorldView3DProfilerFixture? Profiler_FindPlacedFixture(
        string worldspaceEditorId,
        uint referenceFormId,
        uint baseFormId,
        string modelPath)
    {
        if (_data is null)
        {
            return null;
        }

        var worldspace = _data.Worldspaces.FirstOrDefault(candidate =>
            string.Equals(candidate.EditorId, worldspaceEditorId, StringComparison.OrdinalIgnoreCase));
        if (worldspace is null)
        {
            return null;
        }

        foreach (var cell in worldspace.Cells)
        {
            var placed = cell.PlacedObjects.FirstOrDefault(candidate =>
                candidate.FormId == referenceFormId && candidate.BaseFormId == baseFormId);
            if (placed is null)
            {
                continue;
            }

            var resolvedModelPath = placed.ModelPath;
            if (string.IsNullOrWhiteSpace(resolvedModelPath))
            {
                _data.ModelPathIndex.TryGetValue(placed.BaseFormId, out resolvedModelPath);
            }

            if (string.IsNullOrWhiteSpace(resolvedModelPath) ||
                !PathEquals(resolvedModelPath, modelPath))
            {
                return null;
            }

            return new WorldView3DProfilerFixture(
                placed.FormId,
                placed.BaseFormId,
                placed.BaseEditorId,
                resolvedModelPath,
                cell.FormId,
                worldspace.FormId,
                new Vector3(placed.X, placed.Y, placed.Z),
                new Vector3(placed.RotX, placed.RotY, placed.RotZ),
                placed.Scale);
        }

        return null;

        static bool PathEquals(string left, string right) =>
            string.Equals(
                left.Replace('/', '\\').TrimStart('\\'),
                right.Replace('/', '\\').TrimStart('\\'),
                StringComparison.OrdinalIgnoreCase);
    }
}
