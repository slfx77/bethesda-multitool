using Microsoft.UI.Xaml.Controls;

namespace BethesdaMultitool;

/// <summary>
///     Declarative 2D-map export panel for <see cref="WorldMapControl" /> (Layer / Include / Output
///     expanders). The viewer constructs it, exposes it via <c>ExportPanel</c> for the host's
///     right-panel Export tab, and wires every control's events itself in <c>WireExportPanel</c> — no
///     logic here. Replaces the old modal <c>MapExportDialog</c>, and mirrors
///     <see cref="WorldView3DExportPanel" /> so the tab looks the same in both viewers.
/// </summary>
public sealed partial class WorldMapExportPanel : UserControl
{
    public WorldMapExportPanel()
    {
        InitializeComponent();
    }
}
