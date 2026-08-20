using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.App;

public sealed class WorldViewSettingsOrganizationSourceContractTests
{
    [Fact]
    public void OverlaysContainOnlyDebugOverlays()
    {
        var xaml = SourceContract.ReadAppSource("WorldView3DSettingsPanel.xaml");
        var overlays = SourceContract.Extract(xaml, "<Expander x:Name=\"OverlaysExpander\"",
            "<!-- ============================== Visibility");

        Assert.Contains("x:Name=\"CellsCheckBox\"", overlays, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NavMeshCheckBox\"", overlays, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CollisionCheckBox\"", overlays, StringComparison.Ordinal);
        Assert.DoesNotContain("TerrainTexturesToggle", overlays, StringComparison.Ordinal);
        Assert.DoesNotContain("VertexColorsToggle", overlays, StringComparison.Ordinal);
    }

    [Fact]
    public void VisibilityGroupsExistingControlsByPurpose()
    {
        var xaml = SourceContract.ReadAppSource("WorldView3DSettingsPanel.xaml");
        var visibility = SourceContract.Extract(xaml, "<Expander x:Name=\"VisibilityExpander\"",
            "<!-- ============================== Camera");

        SourceContract.AssertOrder(
            visibility,
            "Text=\"Terrain\"",
            "x:Name=\"TerrainToggle\"",
            "x:Name=\"TerrainTexturesToggle\"",
            "x:Name=\"VertexColorsToggle\"",
            "x:Name=\"WaterCheckBox\"",
            "x:Name=\"RefsToggle\"",
            "Text=\"Plants\"",
            "x:Name=\"GrassCheckBox\"",
            "x:Name=\"TreesCheckBox\"",
            "Text=\"Effects\"",
            "x:Name=\"EffectsCheckBox\"",
            "x:Name=\"SkyMeshesCheckBox\"",
            "x:Name=\"AnimationsCheckBox\"",
            "Text=\"Others\"",
            "x:Name=\"EditorMarkersCheckBox\"",
            "x:Name=\"ActivatorsCheckBox\"",
            "x:Name=\"DisabledCheckBox\"");
    }

    [Fact]
    public void DependentContainersCoverOnlyTheirRequestedChildren()
    {
        var xaml = SourceContract.ReadAppSource("WorldView3DSettingsPanel.xaml");
        var terrain = SourceContract.Extract(
            xaml, "<StackPanel x:Name=\"TerrainDependentControls\"", "</StackPanel>");
        Assert.Contains("x:Name=\"TerrainTexturesToggle\"", terrain, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"VertexColorsToggle\"", terrain, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"TerrainToggle\"", terrain, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"WaterCheckBox\"", terrain, StringComparison.Ordinal);

        var meshes = SourceContract.Extract(
            xaml, "<StackPanel x:Name=\"MeshDependentControls\"", "</StackPanel>");
        SourceContract.AssertOrder(
            meshes,
            "Text=\"Plants\"",
            "x:Name=\"GrassCheckBox\"",
            "x:Name=\"TreesCheckBox\"",
            "Text=\"Effects\"",
            "x:Name=\"EffectsCheckBox\"",
            "x:Name=\"SkyMeshesCheckBox\"",
            "x:Name=\"AnimationsCheckBox\"",
            "Text=\"Others\"",
            "x:Name=\"EditorMarkersCheckBox\"",
            "x:Name=\"ActivatorsCheckBox\"",
            "x:Name=\"DisabledCheckBox\"");
        Assert.DoesNotContain("x:Name=\"RefsToggle\"", meshes, StringComparison.Ordinal);
    }

    [Fact]
    public void DependencyStateChangesOnlyAvailabilityAndEveryParentRefreshesIt()
    {
        var panel = SourceContract.ReadAppSource("WorldView3DSettingsPanel.xaml.cs");
        // End the window at the next member's doc comment: the Video-profile members that now
        // follow ApplyDependencyState legitimately discuss IsOn/visibility in their own contracts.
        var apply = SourceContract.Extract(
            panel, "internal void ApplyDependencyState", "/// <summary>");
        Assert.Contains("Lighting.ShadowsControlEnabled = lighting;", apply, StringComparison.Ordinal);
        Assert.Contains("PlacedLightsToggle.IsEnabled = lighting;", apply, StringComparison.Ordinal);
        Assert.Contains("TerrainTexturesToggle.IsEnabled = terrain;", apply, StringComparison.Ordinal);
        Assert.Contains("VertexColorsToggle.IsEnabled = terrain;", apply, StringComparison.Ordinal);
        foreach (var child in new[]
                 {
                     "GrassCheckBox", "TreesCheckBox", "EffectsCheckBox", "SkyMeshesCheckBox",
                     "AnimationsCheckBox", "EditorMarkersCheckBox", "ActivatorsCheckBox", "DisabledCheckBox"
                 })
        {
            Assert.Contains($"{child}.IsEnabled = meshes;", apply, StringComparison.Ordinal);
        }

        // Panels are FrameworkElements rather than Controls in WinUI and cannot be disabled.
        Assert.DoesNotContain("DependentControls.IsEnabled", apply, StringComparison.Ordinal);
        Assert.DoesNotContain("IsChecked", apply, StringComparison.Ordinal);
        Assert.DoesNotContain("IsOn", apply, StringComparison.Ordinal);

        var lighting = SourceContract.ReadAppSource("LightingControlsPanel.xaml.cs");
        var shadowsAvailability = SourceContract.Extract(
            lighting, "internal bool ShadowsControlEnabled", "/// <summary>Skybox on/off.");
        Assert.Contains("ShadowsToggle.IsEnabled", shadowsAvailability, StringComparison.Ordinal);
        Assert.DoesNotContain("ShadowsToggle.IsOn =", shadowsAvailability, StringComparison.Ordinal);

        var toolbar = SourceContract.ReadAppSource("WorldView3DControl.Toolbar.cs");
        Assert.Contains(
            "p.ApplyDependencyState(_showLighting, _showTerrain, _showReferences);",
            SourceContract.Extract(
                toolbar, "private void WireSettingsPanel()", "private void PlacedLightsToggle_Changed"),
            StringComparison.Ordinal);
        Assert.Contains(
            "SettingsPanel.ApplyDependencyState(_showLighting, _showTerrain, _showReferences);",
            SourceContract.Extract(
                toolbar, "private void TerrainToggle_Changed", "private void WaterCheckBox_Changed"),
            StringComparison.Ordinal);
        Assert.Contains(
            "SettingsPanel.ApplyDependencyState(_showLighting, _showTerrain, _showReferences);",
            SourceContract.Extract(
                toolbar, "private void RefsToggle_Changed", "private void LightingPanel_LightingToggled"),
            StringComparison.Ordinal);
        Assert.Contains(
            "SettingsPanel.ApplyDependencyState(_showLighting, _showTerrain, _showReferences);",
            SourceContract.Extract(
                toolbar, "private void LightingPanel_LightingToggled", "private void LightingPanel_SkyboxToggled"),
            StringComparison.Ordinal);
        Assert.Equal(4, SourceContract.CountOccurrences(
            toolbar,
            "ApplyDependencyState(_showLighting, _showTerrain, _showReferences);"));
    }

    [Fact]
    public void DisabledChildShortcutsPreserveTheirLatentPreferences()
    {
        var input = SourceContract.ReadAppSource("WorldView3DControl.Input.cs");
        Assert.Contains(
            "e.Key == VirtualKey.Number4 && _showTerrain",
            input,
            StringComparison.Ordinal);
        Assert.Contains(
            "e.Key == VirtualKey.Number7 && _showReferences",
            input,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MeshesParentGatesLivePickingAndSelectionOutlineButNotExportFraming()
    {
        var input = SourceContract.ReadAppSource("WorldView3DControl.Input.cs");
        var pick = SourceContract.Extract(
            input, "private void TryPickObject", "private void PushSelection");
        Assert.Contains(
            "if (!_showReferences || _data is null || _spatialIndex is null) return;",
            pick,
            StringComparison.Ordinal);

        var frame = SourceContract.ReadAppSource("WorldView3DControl.Frame.cs");
        var overlays = SourceContract.Extract(
            frame, "var wantsSelectionOverlay", "_gpuTimestampProfiler12?.Write(cmd, GpuTimestampRegion.ResolveEnd)");
        Assert.Contains(
            "var wantsSelectionOverlay = _selectionHighlight is not null && IsSelectedReferenceVisible();",
            overlays,
            StringComparison.Ordinal);
        var selectionEligibility = SourceContract.Extract(
            input, "private bool IsSelectedReferenceVisible()", "private void ClearSelection3D()");
        Assert.Contains("if (!_showReferences", selectionEligibility, StringComparison.Ordinal);
        Assert.Contains(
            "var exportFramingOverlay = ExportFramingVisible ? _exportFraming : null;",
            overlays,
            StringComparison.Ordinal);
        Assert.Contains("if (exportFramingOverlay is not null)", overlays, StringComparison.Ordinal);
        Assert.Contains("exportFramingOverlay.Render", overlays, StringComparison.Ordinal);
        Assert.Contains("if (wantsSelectionOverlay)", overlays, StringComparison.Ordinal);
        Assert.Contains("_selectionHighlight!.Render", overlays, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectionControlsLiveInsideTheCameraExpander()
    {
        var xaml = SourceContract.ReadAppSource("WorldView3DSettingsPanel.xaml");
        Assert.DoesNotContain("x:Name=\"ProjectionExpander\"", xaml, StringComparison.Ordinal);
        SourceContract.AssertOrder(
            xaml,
            "<Expander x:Name=\"CameraExpander\"",
            "Text=\"Projection\"",
            "x:Name=\"ProjectionModeRadios\"",
            "x:Name=\"FovSlider\"",
            "x:Name=\"FlyModeToggle\"",
            "x:Name=\"DrawDistanceSlider\"");
    }
}