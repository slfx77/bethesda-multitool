using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Xbox360MemoryCarver;

/// <summary>
///     Helper for showing error dialogs with a "Copy to Clipboard" button.
/// </summary>
internal static class ErrorDialogHelper
{
    /// <summary>
    ///     Shows an error dialog with a "Copy to Clipboard" button.
    /// </summary>
    /// <param name="title">Dialog title</param>
    /// <param name="message">Error message to display</param>
    /// <param name="xamlRoot">XamlRoot for the dialog</param>
    public static async Task ShowErrorAsync(string title, string message, XamlRoot xamlRoot)
    {
        var scrollViewer = new ScrollViewer
        {
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            },
            MaxHeight = 400,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };

        var dialog = new ContentDialog
        {
            Title = title,
            Content = scrollViewer,
            CloseButtonText = "OK",
            PrimaryButtonText = "Copy to Clipboard",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = xamlRoot
        };

        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Primary) ClipboardHelper.CopyText(message);
    }

    /// <summary>
    ///     Shows a simple dialog without copy functionality.
    /// </summary>
    public static async Task ShowInfoAsync(string title, string message, XamlRoot xamlRoot)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = xamlRoot
        };
        await dialog.ShowAsync();
    }
}
