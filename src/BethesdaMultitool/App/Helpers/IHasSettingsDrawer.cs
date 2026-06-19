namespace BethesdaMultitool;

/// <summary>
///     Interface for tabs that provide a settings drawer toggled from MainWindow's nav bar.
/// </summary>
public interface IHasSettingsDrawer
{
    /// <summary>Opens the settings drawer if closed, or closes it if open.</summary>
    void ToggleSettingsDrawer();

    /// <summary>Closes the settings drawer if it is open.</summary>
    void CloseSettingsDrawer();
}
