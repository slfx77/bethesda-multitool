namespace BethesdaMultitool.Core.Carving;

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
    ///     Fraction of the file's bytes that were resident in the dump, 0..1. Always written, so a
    ///     consumer can tell "fully captured" from "never measured" — previously the only trace of
    ///     coverage was a percentage embedded in the free-text <see cref="Notes" />, and only when
    ///     the file was partial.
    /// </summary>
    public double Coverage { get; set; } = 1.0;

    /// <summary>
    ///     True when the file's own tail is missing (the capture stopped before the file ended), as
    ///     opposed to a hole inside it. The two failure modes have very different consequences and
    ///     used to share one <see cref="IsPartial" /> flag.
    /// </summary>
    public bool TailTruncated { get; set; }

    /// <summary>
    ///     Zero-filled byte runs, ascending, relative to the start of this file. Null when the file
    ///     is fully resident. Without these a zero-filled hole is indistinguishable from a run of
    ///     legitimate zero bytes.
    /// </summary>
    public List<CarveHole>? Holes { get; set; }

    /// <summary>
    ///     Set when a hole landed inside bytes the format needs to be structurally valid (a mip
    ///     table, a block list, a frame header). A file can be 95% resident and still unusable if
    ///     the missing 5% is the wrong 5%.
    /// </summary>
    public string? CriticalRangeHit { get; set; }

    /// <summary>
    ///     Format-specific metadata (e.g., qualityEstimate for XMA files).
    /// </summary>
    public Dictionary<string, object>? Metadata { get; set; }
}
