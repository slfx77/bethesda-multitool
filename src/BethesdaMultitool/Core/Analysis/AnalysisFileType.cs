namespace BethesdaMultitool.Core.Analysis;

/// <summary>
///     Identifies file types for the Single File Analysis tab.
/// </summary>
public enum AnalysisFileType
{
    /// <summary>Unknown or unsupported file type.</summary>
    Unknown,

    /// <summary>Windows minidump file (.dmp).</summary>
    Minidump,

    /// <summary>Elder Scrolls Master/Plugin file (.esm/.esp).</summary>
    EsmFile,

    /// <summary>Fallout 3/NV save file (.fxs/.fos).</summary>
    SaveFile,

    /// <summary>
    ///     Classic-era (pre-Morrowind) game content source — a classic archive or data file resolved
    ///     against its install root by <c>ClassicSourceProbe</c>. One member for all seven classic
    ///     games: WHICH game travels in the resolved <c>GameProfile</c>, the same division of labor
    ///     as <see cref="EsmFile" /> vs <c>BethesdaGame</c>.
    /// </summary>
    ClassicGameData
}
