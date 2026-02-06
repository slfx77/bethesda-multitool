namespace FalloutXbox360Utils.Core;

public sealed class StringPoolSummary
{
    public int TotalStrings { get; set; }
    public int UniqueStrings { get; set; }
    public int FilePaths { get; set; }
    public int EditorIds { get; set; }
    public int DialogueLines { get; set; }
    public int GameSettings { get; set; }
    public int Other { get; set; }
    public long TotalBytes { get; set; }
    public int RegionCount { get; set; }
    public List<string> SampleFilePaths { get; } = [];
    public List<string> SampleEditorIds { get; } = [];
    public List<string> SampleDialogue { get; } = [];
    public List<string> SampleSettings { get; } = [];

    // Full sets for export (transferred from extraction; ~60K strings total)
    public HashSet<string> AllFilePaths { get; set; } = [];
    public HashSet<string> AllEditorIds { get; set; } = [];
    public HashSet<string> AllDialogue { get; set; } = [];
    public HashSet<string> AllSettings { get; set; } = [];

    // Carved file cross-reference
    public int MatchedToCarvedFiles { get; set; }
    public int UnmatchedFilePaths { get; set; }
}
