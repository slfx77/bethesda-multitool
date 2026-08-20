using BethesdaMultitool.Core.Formats.Nif.Rendering.Scene;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace BethesdaMultitool;

/// <summary>
///     Declarative settings panel for <see cref="WorldView3DControl" /> (Lighting / Overlays /
///     Visibility / Camera expanders). The viewer constructs it, exposes it via
///     <c>SettingsPanel</c> for the host's right-panel Settings tab, and wires every control's
///     events itself. This panel owns view-only layout warmup and dependent-control availability.
/// </summary>
public sealed partial class WorldView3DSettingsPanel : UserControl
{
    public WorldView3DSettingsPanel()
    {
        InitializeComponent();
        Loaded += SettingsPanel_Loaded;
        HeatmapKeyGradientBar.Fill = BuildHeatmapKeyBrush();
    }

    /// <summary>
    ///     The heatmap key's gradient, sampled straight from <see cref="FormIdHeatmapPalette" /> so the
    ///     bar shows exactly the ramp the meshes get. The ramp is piecewise (nine zones from blue
    ///     through red and pink to white, each with its own hue/saturation/lightness slopes), so
    ///     endpoint interpolation would misdraw all of it; sampling densely enough that every zone
    ///     boundary falls between adjacent stops keeps the bar faithful.
    /// </summary>
    private static LinearGradientBrush BuildHeatmapKeyBrush()
    {
        const int stops = 48;
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (var i = 0; i <= stops; i++)
        {
            var t = i / (float)stops;
            var (r, g, b) = FormIdHeatmapPalette.ToRgb(t);
            brush.GradientStops.Add(new GradientStop
            {
                Offset = t,
                Color = Windows.UI.Color.FromArgb(255, r, g, b)
            });
        }

        return brush;
    }

    /// <summary>Shows/collapses the heatmap key (bar + live window labels) with the overlay toggle.</summary>
    internal void SetHeatmapKeyVisible(bool visible) =>
        HeatmapKeyPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    ///     Writes the live ranking endpoints into the key labels: the lowest FormID in the selected
    ///     cell block anchors the blue end, the highest the white end. Null (heatmap off or nothing in
    ///     range) shows placeholder dashes. <paramref name="rankedCount" /> is the number of distinct
    ///     FormIDs — i.e. the ramp steps actually in use — appended to the newest label so a genuinely
    ///     uniform scene is distinguishable from a collapsed gradient.
    /// </summary>
    internal void UpdateHeatmapKeyWindow((uint Min, uint Max)? window, int rankedCount = 0)
    {
        HeatmapKeyMinLabel.Text = window is { } w ? $"oldest {w.Min:X8}" : "oldest –";
        HeatmapKeyMaxLabel.Text = window is { } w2
            ? $"{w2.Max:X8} newest ({rankedCount} steps)"
            : "– newest";
    }

    /// <summary>
    ///     Updates only whether subordinate settings can be edited. Their checked/on values remain
    ///     latent preferences and become effective again when the corresponding parent is re-enabled.
    /// </summary>
    internal void ApplyDependencyState(bool lighting, bool terrain, bool meshes)
    {
        Lighting.ShadowsControlEnabled = lighting;
        PlacedLightsToggle.IsEnabled = lighting;
        TerrainTexturesToggle.IsEnabled = terrain;
        VertexColorsToggle.IsEnabled = terrain;

        GrassCheckBox.IsEnabled = meshes;
        TreesCheckBox.IsEnabled = meshes;
        EffectsCheckBox.IsEnabled = meshes;
        SkyMeshesCheckBox.IsEnabled = meshes;
        AnimationsCheckBox.IsEnabled = meshes;
        EditorMarkersCheckBox.IsEnabled = meshes;
        ActivatorsCheckBox.IsEnabled = meshes;
        DisabledCheckBox.IsEnabled = meshes;
    }

    /// <summary>
    ///     Shows/hides each Video-section control per the loaded game's capability profile.
    ///     Visibility ONLY — never IsOn/IsEnabled: hidden toggles keep their latent preference and
    ///     re-apply when a game that supports them loads (the same rule ApplyDependencyState
    ///     follows for availability).
    /// </summary>
    internal void ApplyVideoProfile(Core.Formats.Nif.Rendering.VideoSettingsProfile profile)
    {
        static Visibility Vis(bool show) => show ? Visibility.Visible : Visibility.Collapsed;
        VideoExpander.Visibility = Vis(profile.ShowVideoSection);
        TonemapModePanel.Visibility = Vis(profile.HasTonemapSelector);
        // The launcher's middle state exists only for the classic-HDR games; elsewhere the radio
        // offers None/HDR and the owner coerces a latent Bloom selection to HDR in LoadData.
        TonemapBloomRadio.Visibility = Vis(profile.HasClassicHdrTriple);
        WaterReflectionsToggle.Visibility = Vis(profile.HasWaterReflections);
        WaterRipplesToggle.Visibility = Vis(profile.HasWaterRipples);
        WindowReflectionsToggle.Visibility = Vis(profile.HasWindowReflections);
        GrassShadowsToggle.Visibility = Vis(profile.HasGrassShadows);
        CanopyShadowsToggle.Visibility = Vis(profile.HasCanopyShadows);
    }

    /// <summary>
    ///     Reseats the Screen-effects radio without firing the owner's handler loop (the
    ///     owner guards with its syncing flag; index order = None(0)/Bloom(1)/HDR(2)).
    /// </summary>
    internal void SelectTonemapRadio(int index)
    {
        var target = index switch
        {
            0 => TonemapNoneRadio,
            1 => TonemapBloomRadio,
            _ => TonemapHdrRadio
        };
        if (target.IsChecked != true)
        {
            target.IsChecked = true;
        }
    }

    private void SettingsPanel_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= SettingsPanel_Loaded;

        var availableWidth = ActualWidth > 0 ? ActualWidth : double.PositiveInfinity;
        PremeasureCollapsedContent(availableWidth, VideoExpander, OverlaysExpander, VisibilityExpander, CameraExpander);
    }

    private static void PremeasureCollapsedContent(double availableWidth, params Expander[] expanders)
    {
        foreach (var expander in expanders)
        {
            if (!expander.IsExpanded && expander.Content is UIElement content)
            {
                content.Measure(new Size(availableWidth, double.PositiveInfinity));
            }
        }
    }
}
