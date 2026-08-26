namespace BethesdaMultitool.Core.Ui;

/// <summary>
///     One row in the F1 keyboard-shortcuts reference.
/// </summary>
/// <param name="Group">The area heading the row is listed under.</param>
/// <param name="Keys">The chord, as the user should read it.</param>
/// <param name="Action">What the chord does.</param>
public sealed record KeyboardShortcut(string Group, string Keys, string Action);

/// <summary>
///     Source of truth for the shortcuts the F1 dialog lists. Add a row when you add a new XAML
///     <c>&lt;KeyboardAccelerator&gt;</c>; remove a row when you remove one.
///     <para>
///         In <c>Core/</c> rather than beside the dialog because it is pure data with no WinUI
///         dependency, and the dialog lives under <c>App/</c>, which is excluded from the
///         <c>net10.0</c> target framework. While the list sat there, the only available coverage
///         was asserting that particular <c>new(WorldViewer3DGroup, "R", …)</c> literals still
///         appeared in the file — which could not detect a duplicated chord, an empty description,
///         or a row whose group heading had drifted.
///     </para>
/// </summary>
public static class KeyboardShortcutRegistry
{
    /// <summary>Group heading for the 3D world viewer's direct-KeyDown shortcuts.</summary>
    public const string WorldViewer3DGroup = "World Viewer — 3D";

    /// <summary>Group heading for the 2D map's direct-KeyDown shortcuts.</summary>
    public const string WorldViewer2DGroup = "World Viewer — 2D Map";

    /// <summary>Every shortcut, in display order.</summary>
    public static IReadOnlyList<KeyboardShortcut> All { get; } =
    [
        new("HexViewer", "Ctrl+F", "Open the search bar"),
        new("HexViewer", "F3", "Find next match"),
        new("HexViewer", "Shift+F3", "Find previous match"),
        new("HexViewer", "Esc", "Close the search bar"),
        new("HexViewer", "Arrow keys", "Move hex cursor"),
        new("HexViewer", "Page Up / Page Down", "Scroll by one screen"),

        new("Model Tools — Viewer", "Ctrl+O", "Open folder or archive"),
        new("Model Tools — Viewer", "Ctrl+E", "Export current NIF as GLB"),
        new("Model Tools — Viewer", "Ctrl+R", "Render current NIF as PNG"),

        new("Navigation", "Alt+Left", "Previously viewed tab"),
        new("Navigation", "Alt+Right", "Next tab in history"),

        // The 3D world viewer's shortcuts use direct KeyDown handling (WorldView3DControl.Input),
        // not KeyboardAccelerators — keep this group in sync with OnRenderPanelKeyDown.
        new(WorldViewer3DGroup, "W / A / S / D", "Move camera"),
        new(WorldViewer3DGroup, "Q / E", "Descend / climb (fly mode)"),
        new(WorldViewer3DGroup, "Shift / Ctrl", "Move faster / slower"),
        new(WorldViewer3DGroup, "Mouse drag", "Look around"),
        new(WorldViewer3DGroup, "Shift+Mouse drag", "Rotate a projection view"),
        new(WorldViewer3DGroup, "Mouse wheel", "Adjust move speed"),
        new(WorldViewer3DGroup, "F", "Toggle fly / walk camera"),
        new(WorldViewer3DGroup, "Page Up / Page Down", "Increase / decrease draw distance"),
        new(WorldViewer3DGroup, "1–7",
            "Toggle visibility and overlays (cell grid, terrain, water, vertex colors, meshes, nav mesh, disabled objects)"),
        new(WorldViewer3DGroup, "8 / 9 / 0", "Toggle lighting / skybox / fog"),
        new(WorldViewer3DGroup, "E (walk mode)", "Select object at crosshair"),
        new(WorldViewer3DGroup, "Q (walk mode)", "Reselect previous pick"),
        new(WorldViewer3DGroup, "Enter", "Warp through the selected door"),
        new(WorldViewer3DGroup, "P", "Copy camera pose as headless-capture arguments"),
        new(WorldViewer3DGroup, "R", "Reset view (re-frame the worldspace or interior)"),
        new(WorldViewer3DGroup, "Esc", "Clear selection"),

        // The 2D map's shortcuts also use direct KeyDown handling (WorldMapControl.Input) —
        // keep this group in sync with MapCanvas_KeyDown.
        new(WorldViewer2DGroup, "W / A / S / D", "Pan the map"),
        new(WorldViewer2DGroup, "Mouse drag", "Pan the map"),
        new(WorldViewer2DGroup, "Mouse wheel", "Zoom in / out"),
        new(WorldViewer2DGroup, "R", "Reset view (re-frame the worldspace or interior)"),

        new("Help", "F1", "Show this keyboard shortcuts dialog")
    ];

    /// <summary>The rows for one group heading, in display order.</summary>
    public static IReadOnlyList<KeyboardShortcut> ForGroup(string group)
    {
        return [.. All.Where(s => string.Equals(s.Group, group, StringComparison.Ordinal))];
    }
}
