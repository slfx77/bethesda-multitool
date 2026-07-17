using System.Numerics;

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
    string BaseImageSpaceSource);

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
    /// <summary>
    ///     Apply a scenario step through the same private gates the settings-panel handlers update.
    ///     The controls are synchronized as well, so an unattended run cannot leave the visible UI
    ///     claiming a state different from the frame renderer's state.
    /// </summary>
    internal void Profiler_SetPostProcessState(
        bool hdrEnabled,
        bool bloomEnabled,
        bool imagespaceEnabled,
        bool fogEnabled)
    {
        _hdrEnabled = hdrEnabled;
        _bloomEnabled = bloomEnabled;
        _imagespaceModifiersEnabled = imagespaceEnabled;
        _showFog = fogEnabled;

        SettingsPanel.HdrToggle.IsOn = hdrEnabled;
        SettingsPanel.BloomToggle.IsOn = bloomEnabled;
        SettingsPanel.ImagespaceToggle.IsOn = imagespaceEnabled;
        LightingPanel.FogEnabled = fogEnabled;
    }

    /// <summary>Read back both the private gates and the effective tonemap immediately before capture.</summary>
    internal WorldView3DProfilerPostProcessState Profiler_PostProcessState
    {
        get
        {
            var tonemap = ResolveTonemapSettings();
            return new WorldView3DProfilerPostProcessState(
                _hdrEnabled,
                _bloomEnabled,
                _imagespaceModifiersEnabled,
                _showFog,
                !string.Equals(tonemap.Mode.ToString(), "LegacyClamp", StringComparison.Ordinal),
                tonemap.BloomEnabled,
                tonemap.Mode.ToString(),
                _tonemapBaseImageSpaceEditorId,
                _tonemapBaseImageSpaceSource);
        }
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
