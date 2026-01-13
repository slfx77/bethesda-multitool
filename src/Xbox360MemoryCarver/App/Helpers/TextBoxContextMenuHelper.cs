using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Xbox360MemoryCarver;

/// <summary>
///     Helper class to attach consistent context menus with clipboard operations to TextBox controls.
///     Uses ClipboardHelper for reliable clipboard operations in WinUI 3.
/// </summary>
internal static class TextBoxContextMenuHelper
{
    /// <summary>
    ///     Attach a context menu with Cut, Copy, Paste, and Select All to a TextBox.
    ///     For read-only TextBoxes, Cut and Paste will be disabled.
    /// </summary>
    public static void AttachContextMenu(TextBox textBox)
    {
        var flyout = new MenuFlyout();

        var cutItem = new MenuFlyoutItem
        {
            Text = "Cut",
            Icon = new FontIcon { Glyph = "\uE8C6" }
        };
        cutItem.Click += (_, _) => Cut(textBox);
        flyout.Items.Add(cutItem);

        var copyItem = new MenuFlyoutItem
        {
            Text = "Copy",
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyItem.Click += (_, _) => Copy(textBox);
        flyout.Items.Add(copyItem);

        var pasteItem = new MenuFlyoutItem
        {
            Text = "Paste",
            Icon = new FontIcon { Glyph = "\uE77F" }
        };
        pasteItem.Click += (_, _) => Paste(textBox);
        flyout.Items.Add(pasteItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var selectAllItem = new MenuFlyoutItem
        {
            Text = "Select All",
            Icon = new FontIcon { Glyph = "\uE8B3" }
        };
        selectAllItem.Click += (_, _) => SelectAll(textBox);
        flyout.Items.Add(selectAllItem);

        // Update enabled state when flyout opens
        flyout.Opening += (_, _) =>
        {
            var hasSelection = textBox.SelectionLength > 0;
            var hasText = !string.IsNullOrEmpty(textBox.Text);
            var isEditable = !textBox.IsReadOnly;
            var clipboardHasText = ClipboardHelper.HasText();

            cutItem.IsEnabled = hasSelection && isEditable;
            copyItem.IsEnabled = hasSelection || hasText;
            pasteItem.IsEnabled = clipboardHasText && isEditable;
            selectAllItem.IsEnabled = hasText;
        };

        textBox.ContextFlyout = flyout;
    }

    /// <summary>
    ///     Attach context menus to all TextBox children of a panel or container.
    /// </summary>
    public static void AttachContextMenusToChildren(DependencyObject parent)
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is TextBox textBox)
                AttachContextMenu(textBox);
            else if (child is DependencyObject container) AttachContextMenusToChildren(container);
        }
    }

    private static void Cut(TextBox textBox)
    {
        if (textBox.IsReadOnly) return;

        var selectedText = textBox.SelectedText;
        if (string.IsNullOrEmpty(selectedText))
        {
            // If nothing selected, cut all text
            selectedText = textBox.Text;
            if (string.IsNullOrEmpty(selectedText)) return;
            ClipboardHelper.CopyText(selectedText);
            textBox.Text = string.Empty;
        }
        else
        {
            ClipboardHelper.CopyText(selectedText);
            var start = textBox.SelectionStart;
            textBox.Text = textBox.Text.Remove(start, textBox.SelectionLength);
            textBox.SelectionStart = start;
        }
    }

    private static void Copy(TextBox textBox)
    {
        var selectedText = textBox.SelectedText;
        if (string.IsNullOrEmpty(selectedText))
        {
            // If nothing selected, copy all text
            selectedText = textBox.Text;
        }

        if (!string.IsNullOrEmpty(selectedText))
        {
            ClipboardHelper.CopyText(selectedText);
        }
    }

    private static void Paste(TextBox textBox)
    {
        if (textBox.IsReadOnly) return;

        var clipboardText = ClipboardHelper.GetText();
        if (string.IsNullOrEmpty(clipboardText)) return;

        var start = textBox.SelectionStart;
        var length = textBox.SelectionLength;

        if (length > 0)
        {
            // Replace selection
            textBox.Text = textBox.Text.Remove(start, length).Insert(start, clipboardText);
        }
        else
        {
            // Insert at cursor
            textBox.Text = textBox.Text.Insert(start, clipboardText);
        }

        textBox.SelectionStart = start + clipboardText.Length;
        textBox.SelectionLength = 0;
    }

    private static void SelectAll(TextBox textBox)
    {
        textBox.SelectAll();
    }
}
