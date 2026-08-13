using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace BethesdaMultitool;

/// <summary>
///     Focus guard for the viewports' bare-letter keybinds (R = reset view, F, E, Q, …). A single
///     unmodified letter must never be swallowed while the user is typing, so those handlers consult
///     this before acting.
/// </summary>
internal static class TextEntryFocusGuard
{
    /// <summary>
    ///     True when the element holding focus in <paramref name="xamlRoot" /> accepts typed text.
    ///     Returns false when the root is unavailable (control not yet in a visual tree): the calling
    ///     handlers only run for their own focused render surface, so an unresolvable focus must leave
    ///     the keybind enabled rather than silently disable it.
    /// </summary>
    internal static bool IsTextEntryFocused(XamlRoot? xamlRoot)
    {
        if (xamlRoot is null)
        {
            return false;
        }

        // NumberBox/AutoSuggestBox route focus to an inner TextBox, so the TextBox case covers them;
        // they are listed for the (rare) template that keeps focus on the outer control.
        return FocusManager.GetFocusedElement(xamlRoot)
            is TextBox or RichEditBox or PasswordBox or AutoSuggestBox or NumberBox;
    }
}
