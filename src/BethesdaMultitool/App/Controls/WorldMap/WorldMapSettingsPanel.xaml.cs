using Microsoft.UI.Xaml.Controls;

namespace BethesdaMultitool;

/// <summary>
///     Declarative settings panel for <see cref="WorldMapControl" /> (Lighting / Layers /
///     Visibility / Shading expanders). The viewer constructs it, exposes it via
///     <c>SettingsPanel</c> for the host's right-panel Settings tab, and wires every control's
///     events itself — no logic here.
/// </summary>
public sealed partial class WorldMapSettingsPanel : UserControl
{
    public WorldMapSettingsPanel()
    {
        InitializeComponent();
    }
}
