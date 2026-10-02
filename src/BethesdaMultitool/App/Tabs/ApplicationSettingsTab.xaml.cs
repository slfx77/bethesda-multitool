using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Slfx77.Multitool.WinUI.Settings;

namespace BethesdaMultitool;

/// <summary>Mounts the common settings control at BMT's existing persistent navigation destination.</summary>
public sealed partial class ApplicationSettingsTab : UserControl, IAsyncDisposable
{
    private ThemeLanguageSettings? _settings;
    /// <summary>Creates only the existing settings host.</summary>
    public ApplicationSettingsTab() => InitializeComponent();
    /// <summary>Connects common preferences to the retained window and its display controller.</summary>
    /// <param name="root">The window's existing theme root.</param>
    internal void InitializePreferences(FrameworkElement? root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (_settings is not null) return;
        _settings = new ThemeLanguageSettings(MainWindow.Instance!.Localization, "BethesdaMultitool", root);
        SharedPreferencesHost.Content = _settings;
        _ = _settings.InitializeAsync();
    }
    /// <summary>Drains common preference writes before the window releases its controller.</summary>
    /// <returns>Completion of the shared settings lifetime.</returns>
    public async ValueTask DisposeAsync()
    { if (_settings is not null) await _settings.DisposeAsync(); }
}
