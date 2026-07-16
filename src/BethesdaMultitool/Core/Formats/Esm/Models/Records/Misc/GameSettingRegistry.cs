namespace BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;

/// <summary>
///     Builds the case-insensitive runtime GMST lookup used by renderer systems whose settings are
///     addressed by EditorID (moon size/path/fade today). Kept in Core so the projection is covered by
///     the cross-platform parity suite rather than existing only in the Windows world-view builder.
/// </summary>
internal static class GameSettingRegistry
{
    internal static Dictionary<string, GameSettingRecord> BuildIndex(
        IReadOnlyList<GameSettingRecord> settings)
    {
        var result = new Dictionary<string, GameSettingRecord>(
            settings.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var setting in settings)
        {
            if (!string.IsNullOrWhiteSpace(setting.EditorId))
            {
                result.TryAdd(setting.EditorId, setting);
            }
        }

        return result;
    }
}
