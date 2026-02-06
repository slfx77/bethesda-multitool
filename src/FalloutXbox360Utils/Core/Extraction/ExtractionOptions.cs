namespace FalloutXbox360Utils.Core.Extraction;

/// <summary>
///     Options for file extraction.
/// </summary>
public record ExtractionOptions
{
    public string OutputPath { get; init; } = "output";
    public bool ConvertDdx { get; init; } = true;
    public bool SaveAtlas { get; init; }
    public bool Verbose { get; init; }
    public int MaxFilesPerType { get; init; } = 10000;
    public List<string>? FileTypes { get; init; }

    /// <summary>
    ///     Extract compiled scripts (SCDA records) from release dumps.
    ///     Scripts are grouped by quest name for easier analysis.
    /// </summary>
    public bool ExtractScripts { get; init; } = true;

    /// <summary>
    ///     Enable PC-friendly normal map conversion during DDX extraction.
    ///     This post-processes normal maps to merge specular data for PC compatibility.
    /// </summary>
    public bool PcFriendly { get; init; } = true;

    /// <summary>
    ///     Generate ESM semantic reports and heightmap PNGs during extraction.
    /// </summary>
    public bool GenerateEsmReports { get; init; } = true;
}
