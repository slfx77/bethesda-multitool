namespace FalloutXbox360Utils;

/// <summary>
///     Interface for tabs that provide a settings drawer toggled from MainWindow's nav bar.
/// </summary>
public interface IHasSettingsDrawer
{
    void ToggleSettingsDrawer();
    void CloseSettingsDrawer();
}
