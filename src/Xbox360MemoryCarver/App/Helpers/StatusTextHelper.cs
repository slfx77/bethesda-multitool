namespace Xbox360MemoryCarver;

/// <summary>
///     Helper class to route status text updates to the global status bar.
///     Provides a Text property setter that mimics TextBlock for easy code migration.
/// </summary>
public sealed class StatusTextHelper
{
    // Intentionally instance property to allow StatusTextBlock.Text = "message" pattern
#pragma warning disable CA1822, S2325
    public string Text
#pragma warning restore CA1822, S2325
    {
        get => "";
        set => MainWindow.Instance?.SetStatus(value);
    }
}
