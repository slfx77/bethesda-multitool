namespace FalloutXbox360Utils.Core.Formats.Bsa;

/// <summary>
///     Result of BSA archive parsing.
/// </summary>
public record BsaArchive
{
    /// <summary>Archive header.</summary>
    public required BsaHeader Header { get; init; }

    /// <summary>All folders in the archive.</summary>
    public required List<BsaFolderRecord> Folders { get; init; }

    /// <summary>Path to the BSA file.</summary>
    public required string FilePath { get; init; }

    /// <summary>Total number of files.</summary>
    public int TotalFiles => Folders.Sum(f => f.Files.Count);

    /// <summary>Platform description.</summary>
    public string Platform => Header.IsXbox360 ? "Xbox 360" : "PC";

    /// <summary>Get all files as a flat list.</summary>
    public IEnumerable<BsaFileRecord> AllFiles => Folders.SelectMany(f => f.Files);

    /// <summary>Find a file by its full virtual path (case-insensitive, accepts / or \).</summary>
    public BsaFileRecord? FindFile(string path)
    {
        var normalized = path.Replace('/', '\\');
        return AllFiles.FirstOrDefault(f =>
            string.Equals(f.FullPath, normalized, StringComparison.OrdinalIgnoreCase));
    }
}
