using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Xaml.Controls;

namespace BethesdaMultitool.Localization;

/// <summary>Updates the compatibility viewer's own labels without navigation, model reload or camera changes.</summary>
internal static class CompatibilityViewerLocalization
{
    /// <summary>Sends display text only when an existing compatibility document is initialized.</summary>
    /// <param name="viewer">The existing WebView; this method never creates its browser process.</param>
    /// <param name="defaultKey">The NPC or NIF empty-state key.</param>
    /// <returns>Completion of the optional text update.</returns>
    internal static async Task RefreshAsync(WebView2 viewer, string defaultKey)
    {
        try
        {
            if (viewer.CoreWebView2 is null) return;
            var strings = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["defaultKey"] = defaultKey
            };
            foreach (var key in new[] { "Viewer_SelectNpc", "Viewer_SelectNif", "Viewer_LoadingModel",
                         "Viewer_LoadingCompatibility", "Viewer_BuildingCompatibility", "Viewer_FatalError" })
                strings[key] = Strings.Get(key);
            await viewer.ExecuteScriptAsync("if (typeof applyDisplayStrings === 'function') applyDisplayStrings(" +
                JsonSerializer.Serialize(strings) + ");");
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException)
        {
            // Closing a compatibility browser during native-renderer promotion is an expected lifetime race.
            Core.Diagnostics.Logger.Instance.Debug("Viewer language update skipped: {0}", exception.Message);
        }
    }
}
