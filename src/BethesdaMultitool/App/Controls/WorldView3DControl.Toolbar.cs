using BethesdaMultitool.Core.Formats.Esm.Models;
using Microsoft.UI.Xaml;

namespace BethesdaMultitool;

public sealed partial class WorldView3DControl
{
    // Toolbar checkboxes mirror the 2D viewer. Handlers fire once during XAML load with
    // IsChecked="True" (Water only) before sibling fields are assigned, so each must be safe to
    // call before LoadData — they touch only their own control (assigned) and null-guarded state.
    private void CellsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _showWireframe = CellsCheckBox.IsChecked == true;
    }

    private void TerrainToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _showTerrain = TerrainToggle.IsChecked == true;
    }

    private void WaterCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _showWater = WaterCheckBox.IsChecked == true;
    }

    private void TerrainTexturesToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _showTerrainTextures = TerrainTexturesToggle.IsChecked == true;
        _terrain?.SetDebugModes(_showTerrainTextures, _showVertexColors);
    }

    private void VertexColorsToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _showVertexColors = VertexColorsToggle.IsChecked == true;
        _terrain?.SetDebugModes(_showTerrainTextures, _showVertexColors);
    }

    private void RefsToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _showReferences = RefsToggle.IsChecked == true;
    }

    private void LightingPanel_LightingToggled(object? sender, bool isOn)
    {
        if (_initializing) return;
        _showLighting = isOn;
    }

    private void LightingPanel_SkyboxToggled(object? sender, bool isOn)
    {
        if (_initializing) return;
        _showSky = isOn;
    }

    private void LightingPanel_WindOverrideChanged(object? sender, bool isOn)
    {
        if (_initializing) return;
        // On: pin the slider's current value as the wind strength; off: follow the active weather
        // (the render loop reads the weather wind byte each frame — see Frame.cs).
        _windStrength = isOn ? (float)Math.Clamp(LightingPanel.WindSpeed, 0, 1) : null;
    }

    private void LightingPanel_WindSpeedChanged(object? sender, double windSpeed)
    {
        if (_initializing) return;
        _windStrength = (float)Math.Clamp(windSpeed, 0, 1);
    }

    private void LightingPanel_FogToggled(object? sender, bool isOn)
    {
        if (_initializing) return;
        _showFog = isOn;
    }

    private void LightingPanel_ShadowsToggled(object? sender, bool isOn)
    {
        if (_initializing) return;
        _showShadows = isOn;
    }

    private void NavMeshCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        SetShowNavMesh(NavMeshCheckBox.IsChecked == true);
    }

    private void CollisionCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        SetShowCollision(CollisionCheckBox.IsChecked == true);
    }

    private void DisabledCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        SetShowDisabled(DisabledCheckBox.IsChecked == true);
    }

    private void EditorMarkersCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        SetShowMarkers(EditorMarkersCheckBox.IsChecked == true);
    }

    private void ActivatorsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        SetShowActivators(ActivatorsCheckBox.IsChecked == true);
    }

    private void SkyMeshesCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        SetShowSkyMeshes(SkyMeshesCheckBox.IsChecked == true);
    }

    private void TreesCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        SetShowTrees(TreesCheckBox.IsChecked == true);
    }

    private void EffectsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        SetShowEffects(EffectsCheckBox.IsChecked == true);
    }

    private void SetRenderDistance(float distance)
    {
        _renderDistance = Math.Clamp(distance, MinRenderDistanceCells * _cellSize, MaxRenderDistance);
        _camera.FarPlane = _renderDistance;
    }

    /// <summary>Single point for the navmesh-layer toggle (keyboard key 6 + the toolbar
    /// checkbox both route here so field, checkbox, and render state stay in sync).</summary>
    private void SetShowNavMesh(bool on)
    {
        _showNavMesh = on;
        if (NavMeshCheckBox is not null && NavMeshCheckBox.IsChecked != on)
        {
            NavMeshCheckBox.IsChecked = on;
        }
    }

    /// <summary>Single point for the Havok collision-cage overlay toggle (checkbox routes here).
    /// Off by default; renders each visible ref's walk-mode collision mesh as a wireframe.</summary>
    private void SetShowCollision(bool on)
    {
        _showCollision = on;
        if (CollisionCheckBox is not null && CollisionCheckBox.IsChecked != on)
        {
            CollisionCheckBox.IsChecked = on;
        }
    }

    /// <summary>Single point for the initially-disabled-objects toggle. Default off = disabled
    /// REFRs hidden, matching the 2D viewer. Applies straight to the live reference renderer
    /// (render-time filter — no cache rebuild).</summary>
    private void SetShowDisabled(bool on)
    {
        _showDisabled = on;
        if (_references is not null)
        {
            _references.ShowInitiallyDisabled = on;
        }
        if (_collisionDebug is not null)
        {
            _collisionDebug.ShowDisabled = on; // keep the collision overlay in sync with the scene
        }
        if (DisabledCheckBox is not null && DisabledCheckBox.IsChecked != on)
        {
            DisabledCheckBox.IsChecked = on;
        }
    }

    /// <summary>Editor/engine-marker visibility toggle. Default off = markers hidden (matches the
    /// game). Applies straight to the live reference renderer (render-time filter, no rebuild).</summary>
    private void SetShowMarkers(bool on)
    {
        _showMarkers = on;
        if (_references is not null)
        {
            _references.ShowMarkers = on;
        }
        if (EditorMarkersCheckBox is not null && EditorMarkersCheckBox.IsChecked != on)
        {
            EditorMarkersCheckBox.IsChecked = on;
        }
    }

    /// <summary>Activator-category visibility toggle. Default off = activators hidden. Updates the
    /// per-category filter on the reference renderer (render-time, no cache rebuild).</summary>
    private void SetShowActivators(bool on)
    {
        if (on) _hiddenCategories.Remove(PlacedObjectCategory.Activator);
        else _hiddenCategories.Add(PlacedObjectCategory.Activator);
        _references?.SetHiddenCategories(_hiddenCategories);
        if (ActivatorsCheckBox is not null && ActivatorsCheckBox.IsChecked != on)
        {
            ActivatorsCheckBox.IsChecked = on;
        }
    }

    /// <summary>Sky-category visibility toggle. Default off = placed sky/glow meshes (FO4's Sky\ folder,
    /// e.g. DiamondCityGlow) hidden — they're atmosphere props that otherwise clutter the scene. Distinct
    /// from the Skybox toggle, which controls the procedural sky DOME. Render-time filter, no rebuild.</summary>
    private void SetShowSkyMeshes(bool on)
    {
        if (on) _hiddenCategories.Remove(PlacedObjectCategory.Sky);
        else _hiddenCategories.Add(PlacedObjectCategory.Sky);
        _references?.SetHiddenCategories(_hiddenCategories);
        if (SkyMeshesCheckBox is not null && SkyMeshesCheckBox.IsChecked != on)
        {
            SkyMeshesCheckBox.IsChecked = on;
        }
    }

    /// <summary>Tree-category visibility toggle. Default on. Covers ALL tree kinds through the one
    /// reference funnel — Gamebryo .spt SpeedTrees (TREE records) and Skyrim/FO4 NIF trees (TREE
    /// records + landscape\trees\ statics). Render-time filter before the resolve/decode pass, so
    /// trees hidden before streaming never decode or upload.</summary>
    private void SetShowTrees(bool on)
    {
        if (on) _hiddenCategories.Remove(PlacedObjectCategory.Tree);
        else _hiddenCategories.Add(PlacedObjectCategory.Tree);
        _references?.SetHiddenCategories(_hiddenCategories);
        if (TreesCheckBox is not null && TreesCheckBox.IsChecked != on)
        {
            TreesCheckBox.IsChecked = on;
        }
    }

    /// <summary>Effects-category visibility toggle. Default on. Placed effect meshes (the Effects\
    /// folder: mist sheets, dust, glows, ambient FX planes) — real atmosphere in the scene, but the
    /// first thing to hide when inspecting the geometry they hover over. Render-time filter through
    /// the same per-category funnel as trees/activators (no cache rebuild).</summary>
    private void SetShowEffects(bool on)
    {
        if (on) _hiddenCategories.Remove(PlacedObjectCategory.Effects);
        else _hiddenCategories.Add(PlacedObjectCategory.Effects);
        _references?.SetHiddenCategories(_hiddenCategories);
        if (EffectsCheckBox is not null && EffectsCheckBox.IsChecked != on)
        {
            EffectsCheckBox.IsChecked = on;
        }
    }
}
