using Microsoft.UI.Xaml.Controls;

namespace BethesdaMultitool;

/// <summary>
///     Declarative 3D-export panel for <see cref="WorldView3DControl" /> (Projection / Include /
///     Output expanders). The viewer constructs it, exposes it via <c>ExportPanel</c> for the host's
///     right-panel Export tab, and wires every control's events itself in <c>WireExportPanel</c> — no
///     logic here. Replaces the old modal <c>MapExport3DDialog</c>.
/// </summary>
public sealed partial class WorldView3DExportPanel : UserControl
{
    public WorldView3DExportPanel()
    {
        InitializeComponent();
    }
}
