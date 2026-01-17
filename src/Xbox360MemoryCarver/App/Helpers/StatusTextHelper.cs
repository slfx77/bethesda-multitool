namespace Xbox360MemoryCarver;

/// <summary>
///     Helper class to route status text updates to the global status bar.
///     Provides a Text property setter that mimics TextBlock for easy code migration.
/// </summary>
public sealed class StatusTextHelper
{
    public string Text
    {
        get => "";
        set => MainWindow.Instance?.SetStatus(value);
    }
}
