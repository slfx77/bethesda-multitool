using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FalloutXbox360Utils;

/// <summary>
///     Shared logic for toggling settings drawer overlays.
/// </summary>
public static class SettingsDrawerHelper
{
    public static void Toggle(Border drawer) =>
        drawer.Visibility = drawer.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;

    public static void Close(Border drawer) =>
        drawer.Visibility = Visibility.Collapsed;
}
