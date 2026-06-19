using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BethesdaMultitool;

/// <summary>
///     Shared logic for toggling settings drawer overlays.
/// </summary>
public static class SettingsDrawerHelper
{
    /// <summary>Shows the drawer if hidden, or hides it if shown.</summary>
    public static void Toggle(Border drawer) =>
        drawer.Visibility = drawer.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;

    /// <summary>Hides the drawer.</summary>
    public static void Close(Border drawer) =>
        drawer.Visibility = Visibility.Collapsed;
}
