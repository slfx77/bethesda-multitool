namespace FalloutXbox360Utils.Core.Formats.Scda;

/// <summary>
///     Summary of script extraction results.
/// </summary>
public record ScdaExtractionResult
{
    public int TotalRecords { get; init; }
    public int GroupedQuests { get; init; }
    public int UngroupedScripts { get; init; }
    public int TotalBytecodeBytes { get; init; }
    public int RecordsWithSource { get; init; }

    /// <summary>
    ///     List of extracted scripts with their names and offsets.
    /// </summary>
    public List<ScriptInfo> Scripts { get; init; } = [];
}
