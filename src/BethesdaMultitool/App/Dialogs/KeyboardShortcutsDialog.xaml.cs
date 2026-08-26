using System.Linq;
using BethesdaMultitool.Core.Ui;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;

namespace BethesdaMultitool;

/// <summary>
///     F1 "Keyboard shortcuts" reference dialog. Lists every shortcut the app exposes, grouped by
///     area. The rows themselves live in <see cref="KeyboardShortcutRegistry" />; keep that list in
///     sync with the matching <c>&lt;KeyboardAccelerator&gt;</c> declarations in XAML.
/// </summary>
public sealed partial class KeyboardShortcutsDialog : ContentDialog
{
    public KeyboardShortcutsDialog()
    {
        InitializeComponent();

        // ListView.GroupStyle.HeaderTemplate binds against a grouped CollectionViewSource
        // (System.Linq.IGrouping<K,V> surfaces via .Key and IEnumerable<V> items).
        var grouped = KeyboardShortcutRegistry.All.GroupBy(s => s.Group).ToList();
        var source = new CollectionViewSource
        {
            IsSourceGrouped = true,
            Source = grouped
        };
        ShortcutsList.ItemsSource = source.View;
    }
}
