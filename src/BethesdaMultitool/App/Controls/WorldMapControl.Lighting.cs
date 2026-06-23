using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering;

namespace BethesdaMultitool;

/// <summary>
///     Drives the hillshade light direction from the shared <see cref="LightingControlsPanel" />. When
///     lighting is OFF the hillshade (Slope layer + textured hill-shade modulation) uses the fixed NW
///     default; when ON the time-of-day slider projects the analytic sun direction onto the map plane.
/// </summary>
public sealed partial class WorldMapControl
{
    // OFF by default → fixed NW hillshade (the proven look). The user opts in to time-of-day lighting.
    private bool _hillshadeLightingEnabled;
    private float _gameHour = 12f;
    private AtmosphereState.ClimateTiming _currentClimateTiming = AtmosphereState.ClimateTiming.Default;

    private void LightingPanel_LightingToggled(object? sender, bool isOn)
    {
        if (_initializing) return;
        _hillshadeLightingEnabled = isOn;
        RebuildForHillshadeChange();
    }

    private void LightingPanel_TimeChanged(object? sender, double hour)
    {
        if (_initializing) return;
        _gameHour = (float)hour;
        if (_hillshadeLightingEnabled) RebuildForHillshadeChange();
    }

    /// <summary>
    ///     The hillshade light direction in image space (x = east, y = south, z = elevation), or null to
    ///     use <see cref="WorldMapHillshadeRenderer.DefaultLightDir" /> (lighting off). Projects the
    ///     analytic sun's world direction onto the map plane, flipping Y (image rows grow southward) and
    ///     clamping the elevation floor so a low/below-horizon sun grazes rather than flattening to black.
    /// </summary>
    private Vector3? CurrentHillshadeLightDir()
    {
        if (!_hillshadeLightingEnabled) return null;
        var resolved = AtmosphereState.Resolve(
            _gameHour, weather: null, climate: _currentClimateTiming, lightingEnabled: true);
        var sun = resolved.SunWorldDirection; // unit TO-sun, +X east / +Y north / +Z up
        const float minElevation = 0.25f;
        return Vector3.Normalize(new Vector3(sun.X, -sun.Y, MathF.Max(minElevation, sun.Z)));
    }

    /// <summary>Re-bakes the affected layer after a hillshade light-direction change. Only the Slope
    /// layer and the textured hill-shade modulation depend on the light direction; everything else is a
    /// no-op (avoids a needless rebuild when hillshade isn't on screen).</summary>
    private void RebuildForHillshadeChange()
    {
        if (_currentLayer == WorldMapLayer.Slope)
        {
            InvalidateWorldBitmap(keepCurrentBitmap: true);
        }
        else if (_currentLayer == WorldMapLayer.TerrainTextures && _terrainShading == TerrainShadingMode.HillShade)
        {
            // Hillshade is baked into the terrain cell bitmaps + aggregate → drop them and re-stream.
            InvalidateWorldBitmap(keepCurrentBitmap: false);
            EnsureViewportTimerRunning();
        }
        else
        {
            return; // hillshade not in use for the current layer/shading
        }

        if (_cellHeightmapBitmap is not null && _state.SelectedCell is not null)
        {
            RebuildCellDetailBitmaps(_state.SelectedCell);
        }
        MapCanvas?.Invalidate();
    }
}
