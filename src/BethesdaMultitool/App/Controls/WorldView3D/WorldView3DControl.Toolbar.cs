using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.WorldData;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BethesdaMultitool;

public sealed partial class WorldView3DControl
{
    /// <summary>
    ///     The viewer's settings panel (Lighting / Overlays / Visibility / Camera expanders),
    ///     constructed in the ctor and displayed by the host's right-panel Settings tab. Owned here
    ///     so standalone hosts (the profiler apps) keep working without a SingleFileTab.
    /// </summary>
    internal WorldView3DSettingsPanel SettingsPanel { get; }

    // Accessor shims for the controls that moved from the old toolbar dropdowns into the settings
    // panel — the keyboard shortcuts (Input.cs), SetShow* re-syncs, and projection/lighting partials
    // keep compiling against the original x:Name identifiers.
    private LightingControlsPanel LightingPanel => SettingsPanel.Lighting;
    private CheckBox CellsCheckBox => SettingsPanel.CellsCheckBox;
    private CheckBox TerrainTexturesToggle => SettingsPanel.TerrainTexturesToggle;
    private CheckBox VertexColorsToggle => SettingsPanel.VertexColorsToggle;
    private CheckBox NavMeshCheckBox => SettingsPanel.NavMeshCheckBox;
    private CheckBox CollisionCheckBox => SettingsPanel.CollisionCheckBox;
    private CheckBox TerrainToggle => SettingsPanel.TerrainToggle;
    private CheckBox RefsToggle => SettingsPanel.RefsToggle;
    private CheckBox WaterCheckBox => SettingsPanel.WaterCheckBox;
    private CheckBox GrassCheckBox => SettingsPanel.GrassCheckBox;
    private CheckBox EditorMarkersCheckBox => SettingsPanel.EditorMarkersCheckBox;
    private CheckBox ActivatorsCheckBox => SettingsPanel.ActivatorsCheckBox;
    private CheckBox SkyMeshesCheckBox => SettingsPanel.SkyMeshesCheckBox;
    private CheckBox TreesCheckBox => SettingsPanel.TreesCheckBox;
    private CheckBox EffectsCheckBox => SettingsPanel.EffectsCheckBox;
    private CheckBox DisabledCheckBox => SettingsPanel.DisabledCheckBox;
    private CheckBox AnimationsCheckBox => SettingsPanel.AnimationsCheckBox;
    private RadioButtons ProjectionModeRadios => SettingsPanel.ProjectionModeRadios;
    private Slider FovSlider => SettingsPanel.FovSlider;
    private TextBlock FovLabel => SettingsPanel.FovLabel;
    private ToggleSwitch FlyModeToggle => SettingsPanel.FlyModeToggle;
    private Slider DrawDistanceSlider => SettingsPanel.DrawDistanceSlider;
    private TextBlock DrawDistanceValueLabel => SettingsPanel.DrawDistanceValueLabel;

    /// <summary>
    ///     Subscribes every settings-panel control to its handler. Runs in the ctor while
    ///     <c>_initializing</c> is still true, replacing the XAML event attributes the controls had
    ///     on the old toolbar. Dependency availability is initialized from the owner's authoritative
    ///     parent fields after every control has been subscribed.
    /// </summary>
    private void WireSettingsPanel()
    {
        var p = SettingsPanel;

        p.Lighting.LightingToggled += LightingPanel_LightingToggled;
        p.Lighting.FogToggled += LightingPanel_FogToggled;
        p.Lighting.ShadowsToggled += LightingPanel_ShadowsToggled;
        p.Lighting.SkyboxToggled += LightingPanel_SkyboxToggled;
        p.Lighting.TimeChanged += LightingPanel_TimeChanged;
        p.Lighting.DayChanged += LightingPanel_DayChanged;
        p.Lighting.WeatherChanged += LightingPanel_WeatherChanged;
        p.Lighting.WindOverrideChanged += LightingPanel_WindOverrideChanged;
        p.Lighting.WindSpeedChanged += LightingPanel_WindSpeedChanged;

        p.PlacedLightsToggle.Toggled += PlacedLightsToggle_Changed;
        p.HdrToggle.Toggled += HdrToggle_Changed;
        p.BloomToggle.Toggled += BloomToggle_Changed;
        p.ImagespaceComboBox.SelectionChanged += ImagespaceComboBox_SelectionChanged;

        Wire(p.CellsCheckBox, CellsCheckBox_Changed);
        Wire(p.TerrainTexturesToggle, TerrainTexturesToggle_Changed);
        Wire(p.VertexColorsToggle, VertexColorsToggle_Changed);
        Wire(p.NavMeshCheckBox, NavMeshCheckBox_Changed);
        Wire(p.CollisionCheckBox, CollisionCheckBox_Changed);
        Wire(p.TerrainToggle, TerrainToggle_Changed);
        Wire(p.RefsToggle, RefsToggle_Changed);
        Wire(p.WaterCheckBox, WaterCheckBox_Changed);
        Wire(p.GrassCheckBox, GrassCheckBox_Changed);
        Wire(p.EditorMarkersCheckBox, EditorMarkersCheckBox_Changed);
        Wire(p.ActivatorsCheckBox, ActivatorsCheckBox_Changed);
        Wire(p.SkyMeshesCheckBox, SkyMeshesCheckBox_Changed);
        Wire(p.TreesCheckBox, TreesCheckBox_Changed);
        Wire(p.EffectsCheckBox, EffectsCheckBox_Changed);
        Wire(p.DisabledCheckBox, DisabledCheckBox_Changed);
        Wire(p.AnimationsCheckBox, AnimationsCheckBox_Changed);
        p.ResetReferenceOverridesButton.Click += ResetReferenceOverridesButton_Click;

        p.ProjectionModeRadios.SelectionChanged += ProjectionModeRadios_SelectionChanged;
        p.FovSlider.ValueChanged += FovSlider_ValueChanged;
        p.FlyModeToggle.Toggled += FlyModeToggle_Changed;
        p.DrawDistanceSlider.ValueChanged += DrawDistanceSlider_ValueChanged;

        p.ApplyDependencyState(_showLighting, _showTerrain, _showReferences);

        static void Wire(CheckBox box, RoutedEventHandler handler)
        {
            box.Checked += handler;
            box.Unchecked += handler;
        }
    }

    private void HdrToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _hdrEnabled = SettingsPanel.HdrToggle.IsOn;
    }

    private void PlacedLightsToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _placedLightsEnabled = SettingsPanel.PlacedLightsToggle.IsOn;
    }

    private void BloomToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _bloomEnabled = SettingsPanel.BloomToggle.IsOn;
    }

    private void ImagespaceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _suppressImagespaceSelectionEvent) return;
        if (SettingsPanel.ImagespaceComboBox.SelectedItem is not ImagespaceDropdownItem item) return;
        _imagespaceMode = item.Mode;
        _imagespaceExplicitFormId = item.FormId;
    }

    /// <summary>
    ///     Builds the imagespace dropdown once per load: "(Automatic)" (the cell/worldspace
    ///     resolution — today's behavior, the default), "(None)" (neutral grade), then every IMGS
    ///     record sorted by EditorID. Selection is restored to Automatic on reload so a stale
    ///     explicit FormID from another ESM can never leak across loads.
    /// </summary>
    private void PopulateImagespaceDropdown()
    {
        var items = new List<ImagespaceDropdownItem>
        {
            new("(Automatic)", ImagespaceSelectionMode.Automatic, 0),
            new("(None)", ImagespaceSelectionMode.None, 0),
        };
        if (_data is not null)
        {
            foreach (var imgs in _data.ImageSpacesByFormId.Values
                         .OrderBy(static i => i.EditorId ?? $"0x{i.FormId:X8}", StringComparer.OrdinalIgnoreCase))
            {
                var label = string.IsNullOrEmpty(imgs.EditorId)
                    ? $"0x{imgs.FormId:X8}"
                    : $"{imgs.EditorId} (0x{imgs.FormId:X8})";
                items.Add(new ImagespaceDropdownItem(label, ImagespaceSelectionMode.Explicit, imgs.FormId));
            }
        }

        _suppressImagespaceSelectionEvent = true;
        SettingsPanel.ImagespaceComboBox.ItemsSource = items;
        SettingsPanel.ImagespaceComboBox.SelectedIndex = 0;
        _suppressImagespaceSelectionEvent = false;
        _imagespaceMode = ImagespaceSelectionMode.Automatic;
        _imagespaceExplicitFormId = 0;
    }

    /// <summary>Programmatic selection (profiler scenarios): Automatic or None only.</summary>
    private void SetImagespaceSelection(ImagespaceSelectionMode mode)
    {
        _imagespaceMode = mode;
        _imagespaceExplicitFormId = 0;
        if (SettingsPanel.ImagespaceComboBox.ItemsSource is List<ImagespaceDropdownItem> items)
        {
            var index = items.FindIndex(i => i.Mode == mode);
            if (index >= 0)
            {
                _suppressImagespaceSelectionEvent = true;
                SettingsPanel.ImagespaceComboBox.SelectedIndex = index;
                _suppressImagespaceSelectionEvent = false;
            }
        }
    }

    private bool _suppressImagespaceSelectionEvent;

    /// <summary>One imagespace dropdown entry; <see cref="ToString" /> drives the display.
    /// <see cref="FormId" /> is meaningful only for <see cref="ImagespaceSelectionMode.Explicit" />.</summary>
    private sealed record ImagespaceDropdownItem(string Label, ImagespaceSelectionMode Mode, uint FormId)
    {
        public override string ToString() => Label;
    }

    // Settings-panel checkboxes mirror the 2D viewer. Handlers are subscribed programmatically in
    // WireSettingsPanel and must be safe to run before LoadData — they touch only their own control
    // (assigned) and null-guarded state.
    private void CellsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _showWireframe = CellsCheckBox.IsChecked == true;
    }

    private void TerrainToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _showTerrain = TerrainToggle.IsChecked == true;
        SettingsPanel.ApplyDependencyState(_showLighting, _showTerrain, _showReferences);
    }

    private void WaterCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _showWater = WaterCheckBox.IsChecked == true;
    }

    private void GrassCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        SetShowGrass(GrassCheckBox.IsChecked == true);
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
        SettingsPanel.ApplyDependencyState(_showLighting, _showTerrain, _showReferences);
    }

    private void LightingPanel_LightingToggled(object? sender, bool isOn)
    {
        if (_initializing) return;
        _showLighting = isOn;
        SettingsPanel.ApplyDependencyState(_showLighting, _showTerrain, _showReferences);
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

    private void ResetReferenceOverridesButton_Click(object sender, RoutedEventArgs e) =>
        ClearReferenceEnabledOverrides();

    private void EditorMarkersCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        SetShowMarkers(EditorMarkersCheckBox.IsChecked == true);
    }

    private void AnimationsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        SetAnimationsEnabled(AnimationsCheckBox.IsChecked == true);
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

    /// <summary>Single funnel for every draw-distance change (PageUp/PageDown, the settings-panel
    /// slider, and worldspace/cell-size switches) so field, camera, slider, and label stay in sync.</summary>
    private void SetRenderDistance(float distance)
    {
        _renderDistance = Math.Clamp(distance, MinRenderDistanceCells * _cellSize, MaxRenderDistance);
        _camera.FarPlane = _renderDistance;
        SyncDrawDistanceSlider();
    }

    private void SyncDrawDistanceSlider()
    {
        var cells = _renderDistance / _cellSize;
        var sliderValue = RenderDistanceScale.SliderFromCells(cells);
        _syncingDrawDistance = true;
        try
        {
            // Only reseat on a real difference to avoid float ping-pong with ValueChanged.
            if (Math.Abs(DrawDistanceSlider.Value - sliderValue) > 0.005)
            {
                DrawDistanceSlider.Value = sliderValue;
            }

            DrawDistanceValueLabel.Text = $"{cells:0.#} c";
        }
        finally
        {
            _syncingDrawDistance = false;
        }
    }

    private void DrawDistanceSlider_ValueChanged(
        object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_initializing || _syncingDrawDistance) return;
        SetRenderDistance(RenderDistanceScale.CellsFromSlider(e.NewValue) * _cellSize);
    }

    /// <summary>Single funnel for fly/walk (keyboard F + the settings-panel toggle) so the
    /// controller mode and the ToggleSwitch stay in sync (the controller setter early-returns on
    /// no-change, killing feedback loops).</summary>
    private void SetCameraMode(CameraMode mode)
    {
        _controller.Mode = mode;
        var isFly = mode == CameraMode.Fly;
        if (FlyModeToggle.IsOn != isFly)
        {
            FlyModeToggle.IsOn = isFly;
        }
    }

    private void FlyModeToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        SetCameraMode(FlyModeToggle.IsOn ? CameraMode.Fly : CameraMode.Walk);
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

    /// <summary>NIF animation playback toggle (banner sway, waterfall/lava UV scroll). Default on.
    /// Applies straight to the live reference renderer — off pauses playback at the rest pose /
    /// base frame (render-time only, no rebuild).</summary>
    private void SetAnimationsEnabled(bool on)
    {
        _animationsEnabled = on;
        if (_references is not null)
        {
            _references.AnimationsEnabled = on;
        }
        if (AnimationsCheckBox is not null && AnimationsCheckBox.IsChecked != on)
        {
            AnimationsCheckBox.IsChecked = on;
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

    /// <summary>LAND/GRAS visibility toggle. The synthetic placements stay cached; filtering before
    /// mesh resolution means grass hidden before streaming never decodes or uploads.</summary>
    private void SetShowGrass(bool on)
    {
        _showGrass = on;
        if (_references is not null) _references.ShowGrass = on;
        if (GrassCheckBox is not null && GrassCheckBox.IsChecked != on)
        {
            GrassCheckBox.IsChecked = on;
        }
    }

    /// <summary>Activator-category visibility toggle. Default on. Updates the
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

/// <summary>
///     The viewer's imagespace grading source. <see cref="Automatic" /> follows the engine's
///     cell/worldspace resolution (XCIM → WRLD INAM → shipped defaults); <see cref="None" /> renders
///     a neutral grade (eye-adapt exposure stays only while an HDR operator is active);
///     <see cref="Explicit" /> forces one IMGS record chosen in the settings-panel dropdown.
/// </summary>
internal enum ImagespaceSelectionMode
{
    Automatic,
    None,
    Explicit,
}
