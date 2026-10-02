namespace BethesdaMultitool.Core.Media.Audio.Dialogue;

/// <summary>Preserves the dialogue audio API while the shared plugin-header reader owns parsing.</summary>
public static class DialoguePluginIdentity
{
    /// <summary>Resolves an original plugin-local FormID through its complete declared master table.</summary>
    /// <param name="path">The original Fallout plugin path.</param>
    /// <param name="formId">The original plugin-local record identity.</param>
    /// <returns>The declared owner, or null when its complete identity is unavailable.</returns>
    public static string? ResolveOwner(string path, uint formId) =>
        BethesdaMultitool.Core.Formats.Esm.Parsing.DialoguePluginIdentity.ResolveOwner(path, formId);

    /// <summary>Reads the master slots once for the audio catalog's original voice identities.</summary>
    /// <param name="path">The original Fallout plugin path.</param>
    /// <returns>The complete declared master slots followed by the source plugin, or null.</returns>
    public static IReadOnlyList<string>? ReadOwners(string path) =>
        BethesdaMultitool.Core.Formats.Esm.Parsing.DialoguePluginIdentity.ReadOwners(path);
}
