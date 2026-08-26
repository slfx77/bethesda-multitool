using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.App;

/// <summary>
///     Pins the selected-reference visibility seam across the shared inspector, renderer, picking,
///     collision consumers, and the recovery action. Per-reference previews must not silently turn
///     into category/global filter changes.
/// </summary>
public sealed class SelectedReferenceVisibilitySourceContractTests
{
    [Fact]
    public void InspectorOffersAuthoredShownHiddenOnlyForViewerSupportedPlacements()
    {
        var xaml = SourceContract.ReadAppSource("SingleFileTab.xaml");
        var panel = SourceContract.Extract(
            xaml,
            "<StackPanel x:Name=\"ReferenceStatePanel\"",
            "</StackPanel>");

        SourceContract.AssertOrder(
            panel,
            "Text=\"3D visibility preview\"",
            "x:Name=\"ReferenceStateComboBox\"",
            "AutomationProperties.Name=\"Selected reference 3D visibility preview state\"",
            "<ComboBoxItem Content=\"Authored\" />",
            "<ComboBoxItem Content=\"Shown\" />",
            "<ComboBoxItem Content=\"Hidden\" />");

        var host = SourceContract.ReadAppSource("SingleFileTab.WorldMap.cs");
        var update = SourceContract.Extract(
            host,
            "private void UpdateReferenceStateInspection",
            "private void ReferenceStateComboBox_SelectionChanged");
        Assert.Contains(
            "var hasReferencePreview = WorldView3DControl.CanPreviewReferenceVisibility(obj);",
            update,
            StringComparison.Ordinal);
        Assert.DoesNotContain("PlacedObjectCategory.", update, StringComparison.Ordinal);

        // The eligibility RULE now lives in Core as ReferencePreviewEligibility and is covered by
        // value in ReferencePreviewEligibilityTests. All that stays pinned here is that the control
        // still routes through it rather than growing a second, divergent copy.
        var state = SourceContract.ReadAppSource("WorldView3DControl.ActivatorState.cs");
        Assert.Contains("ReferencePreviewEligibility.CanPreview(reference, _data)", state,
            StringComparison.Ordinal);
        Assert.DoesNotContain("RenderableReference.TryBuild(", state, StringComparison.Ordinal);

        Assert.Contains(
            "AutomationProperties.LiveSetting=\"Polite\"",
            panel,
            StringComparison.Ordinal);

        var handler = SourceContract.Extract(
            host,
            "private void ReferenceStateComboBox_SelectionChanged",
            "private void WorldView3D_ReferenceEnabledOverridesReset");
        Assert.Contains(
            "WorldView3DControl.SetReferenceEnabledOverride(obj.FormId, enabledOverride);",
            handler,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitOffReachesPixelsPickingOutlineAndBothCollisionConsumers()
    {
        var referenceRenderer = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "ReferenceRenderer12.cs");
        Assert.Contains(
            "visibilityKey.IsVisible(r, _enabledOverrides, DayNightStates)",
            referenceRenderer,
            StringComparison.Ordinal);

        var input = SourceContract.ReadAppSource("WorldView3DControl.Input.cs");
        Assert.Contains(
            "_referenceEnabledOverrides.IsVisible(r.FormId, r.IsInitiallyDisabled, _showDisabled)",
            input,
            StringComparison.Ordinal);

        var frame = SourceContract.ReadAppSource("WorldView3DControl.Frame.cs");
        Assert.Contains(
            "_selectionHighlight is not null && IsSelectedReferenceVisible()",
            frame,
            StringComparison.Ordinal);
        var selectionEligibility = SourceContract.Extract(
            input,
            "private bool IsSelectedReferenceVisible()",
            "private void ClearSelection3D()");
        Assert.Contains("_referenceEnabledOverrides.IsVisible(", selectionEligibility, StringComparison.Ordinal);
        Assert.Contains("_hiddenCategories.Contains(reference.Category)", selectionEligibility,
            StringComparison.Ordinal);
        Assert.Contains("reference.IsGrass && !_showGrass", selectionEligibility, StringComparison.Ordinal);
        Assert.Contains("reference.IsMarker && !_showMarkers", selectionEligibility, StringComparison.Ordinal);

        var collision = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "CollisionDebugRenderer12.cs");
        Assert.Contains(
            "_enabledOverrides.IsVisible(p.FormId, authoredDisabled, _showDisabled)",
            collision,
            StringComparison.Ordinal);

        var ground = SourceContract.ReadAppSource("WorldView3DControl.GroundRaycast.cs");
        Assert.Contains(
            "_referenceEnabledOverrides.IsVisible(p.FormId, authoredDisabled, _showDisabled)",
            ground,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PlacedNifWaterPublicationTracksItsOwnerWithoutDependingOnTheMeshesParent()
    {
        var referenceRenderer = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "ReferenceRenderer12.cs");
        Assert.Contains(
            "private readonly PlacedNifWaterRegistry _nifWaterRegistry = new();",
            referenceRenderer,
            StringComparison.Ordinal);
        Assert.Contains(
            "_nifWaterRegistry.GetPublished(VisibilityKey, _enabledOverrides)",
            referenceRenderer,
            StringComparison.Ordinal);
        Assert.Contains(
            "AccumulateNifWaterPlanes(r, mesh.WaterPlanesLocal);",
            referenceRenderer,
            StringComparison.Ordinal);
        Assert.DoesNotContain("_nifWaterPlaneSeen", referenceRenderer, StringComparison.Ordinal);

        var registry = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Water",
            "PlacedNifWaterRegistry.cs");
        Assert.Contains(
            "visibility.IsVisible(entry.Owner, enabledOverrides)",
            registry,
            StringComparison.Ordinal);
        Assert.DoesNotContain("_showReferences", registry, StringComparison.Ordinal);
    }

    [Fact]
    public void StationaryShadowKeyIncludesExactReferenceVisibilityAndClearsTheLastCaster()
    {
        var frame = SourceContract.ReadAppSource("WorldView3DControl.Frame.cs");
        Assert.Contains("private readonly record struct ShadowContentKey(", frame, StringComparison.Ordinal);
        Assert.Contains(
            "ReferenceVisibilityKey ReferenceVisibility",
            frame,
            StringComparison.Ordinal);
        Assert.Contains("bool TerrainVisible", frame, StringComparison.Ordinal);
        Assert.Contains(
            "var referenceVisibility = _references.VisibilityKey;",
            frame,
            StringComparison.Ordinal);
        Assert.Contains(
            "enableShadows: shadowsActive, lightVisibility: cylinder",
            frame,
            StringComparison.Ordinal);
        Assert.Contains("visibilityPending[i] =", frame, StringComparison.Ordinal);
        Assert.Contains(
            "contentKey.TerrainVisible != _shadowContentKeys[i].TerrainVisible",
            frame,
            StringComparison.Ordinal);
        Assert.Contains(
            "!firstPublish && !anyVisibilityPending && dueCount > ShadowMaxCascadeRendersPerFrame",
            frame,
            StringComparison.Ordinal);
        // A visibility-driven clear still commits its keys, but now through the shared authoritative
        // predicate rather than a bespoke visibility-only branch — the old branch's condition was a
        // strict subset, and gating on it left every OTHER authoritatively-empty cascade re-rendering
        // every frame forever.
        Assert.Contains(
            "SunShadowMath.ShouldCommitCascadeState(",
            frame,
            StringComparison.Ordinal);
        Assert.Contains(
            "terrainReplayCompleted = _terrain!.LastShadowReplayCompleted;",
            frame,
            StringComparison.Ordinal);
        Assert.Contains(
            "_shadowContentKeys[i] = contentKey;",
            frame,
            StringComparison.Ordinal);
        Assert.Contains(
            "SunShadowMath.ShouldDeferForReferenceExtentChange(",
            frame,
            StringComparison.Ordinal);
        Assert.Contains("_references.DisarmShadowCapture();", frame, StringComparison.Ordinal);

        var referenceRenderer = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "ReferenceRenderer12.cs");
        Assert.Contains(
            "internal ReferenceVisibilityKey VisibilityKey => ReferenceVisibilityKey.Capture(",
            referenceRenderer,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Bumped whenever the opaque batches are REBUILT",
            referenceRenderer,
            StringComparison.Ordinal);
        var loadData = SourceContract.Extract(
            referenceRenderer,
            "public void LoadData(",
            "private Vector4 ResolveTextureState");
        Assert.Contains("BatchContentVersion++;", loadData, StringComparison.Ordinal);
    }

    [Fact]
    public void PlacedLightEmissionUsesTheSharedOverrideWithoutBypassingLightingGates()
    {
        var host = SourceContract.ReadAppSource("WorldView3DControl.PointLights.cs");
        var bind = SourceContract.Extract(
            host,
            "private unsafe int BindPlacedLights(",
            "private void ApplyFramePlacedLightCap");
        Assert.Contains(
            "PlacedLightsEnvEnabled && _placedLightsEnabled && lightingEnabled",
            bind,
            StringComparison.Ordinal);

        var append = SourceContract.Extract(
            host,
            "private void AppendCellLights(",
            "}");
        Assert.Contains(
            "enabledOverrides: _referenceEnabledOverrides",
            append,
            StringComparison.Ordinal);
        Assert.Contains(
            "includeInitiallyDisabled: _showDisabled",
            append,
            StringComparison.Ordinal);

        var selector = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Lighting",
            "PlacedLightSelector.cs");
        Assert.Contains(
            "enabledOverrides.IsVisible(",
            selector,
            StringComparison.Ordinal);
        // The scripted day/night layer replaces the AUTHORED disabled state for gated emitters but
        // still funnels through the shared override policy — the explicit On/Off preview and the
        // show-disabled diagnostic keep their precedence.
        Assert.Contains(
            "light.FormId, authoredDisabled, includeInitiallyDisabled",
            selector,
            StringComparison.Ordinal);
        Assert.Contains(
            "dayNightStates?.TryGetDisabled(light.FormId, out var hourDisabled)",
            selector,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SceneWideResetIsAlwaysIndependentFromTheMeshesDependencyContainer()
    {
        var settings = SourceContract.ReadAppSource("WorldView3DSettingsPanel.xaml");
        var meshDependents = SourceContract.Extract(
            settings,
            "<StackPanel x:Name=\"MeshDependentControls\"",
            "</StackPanel>");
        Assert.DoesNotContain("ResetReferenceOverridesButton", meshDependents, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ResetReferenceOverridesButton\"", settings, StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.Name=\"Reset all per-object visibility previews\"",
            settings,
            StringComparison.Ordinal);

        var state = SourceContract.ReadAppSource("WorldView3DControl.ActivatorState.cs");
        var reset = SourceContract.Extract(
            state,
            "private void ClearReferenceEnabledOverrides",
            "private void RefreshReferenceOverrideRecoveryUi");
        SourceContract.AssertOrder(
            reset,
            "if (_referenceEnabledOverrides.Count == 0) return;",
            "_referenceEnabledOverrides.Clear();",
            "RefreshReferenceOverrideRecoveryUi();",
            "ReferenceEnabledOverridesReset?.Invoke(this, EventArgs.Empty);");

        Assert.Contains("var count = _referenceEnabledOverrides.Count;", state, StringComparison.Ordinal);
        Assert.Contains("IsEnabled = count > 0;", state, StringComparison.Ordinal);
    }
}