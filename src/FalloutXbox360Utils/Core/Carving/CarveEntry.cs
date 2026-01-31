namespace FalloutXbox360Utils.Core.Carving;

/// <summary>
///     Entry in the carving manifest.
/// </summary>
public class CarveEntry
{
    public string FileType { get; set; } = "";
    public long Offset { get; set; }
    public int SizeInDump { get; set; }
    public int SizeOutput { get; set; }
    public string Filename { get; set; } = "";

    /// <summary>
    ///     Original file path from the game data (e.g., "textures\architecture\anvil\anvildoor01.ddx").
    ///     Only populated for files where the path could be extracted from memory.
    /// </summary>
    public string? OriginalPath { get; set; }

    public bool IsCompressed { get; set; }
    public string? ContentType { get; set; }
    public bool IsPartial { get; set; }
    public string? Notes { get; set; }

    /// <summary>
    ///     Format-specific metadata (e.g., qualityEstimate for XMA files).
    /// </summary>
    public Dictionary<string, object>? Metadata { get; set; }
}
