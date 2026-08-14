using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

    private void SettingsPanel_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= SettingsPanel_Loaded;

        var availableWidth = ActualWidth > 0 ? ActualWidth : double.PositiveInfinity;
        PremeasureCollapsedContent(availableWidth, OverlaysExpander, VisibilityExpander, CameraExpander);
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
