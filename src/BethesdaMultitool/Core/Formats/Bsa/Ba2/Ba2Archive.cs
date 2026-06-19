// Copyright (c) 2026 BethesdaMultitool Contributors
// Licensed under the MIT License.

namespace BethesdaMultitool.Core.Formats.Bsa.Ba2;

/// <summary>
///     Result of parsing a BA2 (Fallout 4 / Fallout 76) archive. Unlike BSA, BA2 has no folder
///     tree — <see cref="Files" /> is a flat list.
/// </summary>
public sealed record Ba2Archive
{
    /// <summary>Archive header.</summary>
    public required Ba2Header Header { get; init; }

    /// <summary>All file entries, in archive order.</summary>
    public required List<Ba2FileRecord> Files { get; init; }

    /// <summary>Path to the BA2 file.</summary>
    public required string FilePath { get; init; }

    /// <summary>Total number of files.</summary>
    public int TotalFiles => Files.Count;

    /// <summary>Get all files as a flat list (parity with <c>BsaArchive.AllFiles</c>).</summary>
    public IEnumerable<Ba2FileRecord> AllFiles => Files;

    /// <summary>Find a file by its full virtual path (case-insensitive, accepts / or \).</summary>
    public Ba2FileRecord? FindFile(string path)
    {
        var normalized = path.Replace('/', '\\');
        return Files.FirstOrDefault(f =>
            string.Equals(f.FullPath, normalized, StringComparison.OrdinalIgnoreCase));
    }
}
