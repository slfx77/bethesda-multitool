using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;

namespace FalloutXbox360Utils;

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
