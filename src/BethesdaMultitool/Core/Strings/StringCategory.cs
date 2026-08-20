namespace BethesdaMultitool.Core.Strings;

/// <summary>
///     Classifies a string found in a memory dump by its semantic role (asset path, EditorID, dialogue line, game
///     setting, or other).
/// </summary>
public enum StringCategory
{
    FilePath,
    EditorId,
    DialogueLine,
    GameSetting,
    Other
}
