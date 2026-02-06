namespace FalloutXbox360Utils.Core;

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
    EsmFile
}
