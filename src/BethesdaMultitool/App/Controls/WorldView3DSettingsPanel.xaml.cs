using Microsoft.UI.Xaml.Controls;

namespace BethesdaMultitool;

/// <summary>
///     Declarative settings panel for <see cref="WorldView3DControl" /> (Lighting / Layers /
///     Visibility / Projection expanders). The viewer constructs it, exposes it via
///     <c>SettingsPanel</c> for the host's right-panel Settings tab, and wires every control's
///     events itself — no logic here.
/// </summary>
public sealed partial class WorldView3DSettingsPanel : UserControl
{
    public WorldView3DSettingsPanel()
    {
        InitializeComponent();
    }
}
