using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;

namespace BethesdaMultitool;

/// <summary>
///     F1 "Keyboard shortcuts" reference dialog. Lists every shortcut the app exposes,
///     grouped by area. Keep this list in sync with the matching
///     <c>&lt;KeyboardAccelerator&gt;</c> declarations in XAML.
/// </summary>
public sealed partial class KeyboardShortcutsDialog : ContentDialog
{
    /// <summary>
    ///     Source of truth for the dialog's contents. Add a row when you add a new
    ///     XAML <c>KeyboardAccelerator</c>; remove a row when you remove one.
    /// </summary>
    private static readonly IReadOnlyList<KeyboardShortcut> All =
    [
        new("HexViewer", "Ctrl+F", "Open the search bar"),
        new("HexViewer", "F3", "Find next match"),
        new("HexViewer", "Shift+F3", "Find previous match"),
        new("HexViewer", "Esc", "Close the search bar"),
        new("HexViewer", "Arrow keys", "Move hex cursor"),
        new("HexViewer", "Page Up / Page Down", "Scroll by one screen"),

        new("Model Tools — Viewer", "Ctrl+O", "Open folder or BSA"),
        new("Model Tools — Viewer", "Ctrl+E", "Export current NIF as GLB"),
        new("Model Tools — Viewer", "Ctrl+R", "Render current NIF as PNG"),

        new("Navigation", "Alt+Left", "Previously viewed tab"),
        new("Navigation", "Alt+Right", "Next tab in history"),

        // The 3D world viewer's shortcuts use direct KeyDown handling (WorldView3DControl.Input),
        // not KeyboardAccelerators — keep this group in sync with OnRenderPanelKeyDown.
        new("World Viewer — 3D", "W / A / S / D", "Move camera"),
        new("World Viewer — 3D", "Q / E", "Descend / climb (fly mode)"),
        new("World Viewer — 3D", "Shift / Ctrl", "Move faster / slower"),
        new("World Viewer — 3D", "Mouse drag", "Look around"),
        new("World Viewer — 3D", "Mouse wheel", "Adjust move speed"),
        new("World Viewer — 3D", "F", "Toggle fly / walk camera"),
        new("World Viewer — 3D", "Page Up / Page Down", "Increase / decrease draw distance"),
        new("World Viewer — 3D", "1–7", "Toggle layers (cell grid, terrain, water, vertex colors, meshes, nav mesh, disabled objects)"),
        new("World Viewer — 3D", "8 / 9 / 0", "Toggle lighting / skybox / fog"),
        new("World Viewer — 3D", "E (walk mode)", "Select object at crosshair"),
        new("World Viewer — 3D", "Q (walk mode)", "Reselect previous pick"),
        new("World Viewer — 3D", "Enter", "Warp through the selected door"),
        new("World Viewer — 3D", "P", "Copy camera pose as headless-capture arguments"),
        new("World Viewer — 3D", "R", "Reset view (re-frame the worldspace or interior)"),
        new("World Viewer — 3D", "Esc", "Clear selection"),

        // The 2D map's shortcuts also use direct KeyDown handling (WorldMapControl.Input) —
        // keep this group in sync with MapCanvas_KeyDown.
        new("World Viewer — 2D Map", "W / A / S / D", "Pan the map"),
        new("World Viewer — 2D Map", "Mouse drag", "Pan the map"),
        new("World Viewer — 2D Map", "Mouse wheel", "Zoom in / out"),
        new("World Viewer — 2D Map", "R", "Reset view (re-frame the worldspace or interior)"),

        new("Help", "F1", "Show this keyboard shortcuts dialog")
    ];

    public KeyboardShortcutsDialog()
    {
        InitializeComponent();

        // ListView.GroupStyle.HeaderTemplate binds against a grouped CollectionViewSource
        // (System.Linq.IGrouping<K,V> surfaces via .Key and IEnumerable<V> items).
        var grouped = All.GroupBy(s => s.Group).ToList();
        var source = new CollectionViewSource
        {
            IsSourceGrouped = true,
            Source = grouped
        };
        ShortcutsList.ItemsSource = source.View;
    }
}
