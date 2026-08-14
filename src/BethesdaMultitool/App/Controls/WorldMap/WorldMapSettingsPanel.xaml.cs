using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace BethesdaMultitool;

/// <summary>
///     Declarative settings panel for <see cref="WorldMapControl" /> (Lighting / Layers /
///     Visibility / Shading expanders). The viewer constructs it, exposes it via
///     <c>SettingsPanel</c> for the host's right-panel Settings tab, and wires every control's
///     events itself. This panel owns only the layout warmup needed for smooth first expansion.
/// </summary>
public sealed partial class WorldMapSettingsPanel : UserControl
{
    public WorldMapSettingsPanel()
    {
        InitializeComponent();
        Loaded += SettingsPanel_Loaded;
    }

    private void SettingsPanel_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= SettingsPanel_Loaded;

        var availableWidth = ActualWidth > 0 ? ActualWidth : double.PositiveInfinity;
        PremeasureCollapsedContent(availableWidth, LayersExpander, VisibilityExpander, ShadingExpander);
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
