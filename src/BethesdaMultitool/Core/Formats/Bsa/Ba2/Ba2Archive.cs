using BethesdaMultitool.Core.Formats.Bsa.Models;
// Copyright (c) 2026 BethesdaMultitool Contributors
// Licensed under the MIT License.

namespace BethesdaMultitool.Core.Formats.Bsa.Ba2;

/// <summary>
///     The result of parsing a BA2 (Fallout 4 / Fallout 76) archive. Unlike a classic BSA, a BA2
///     has no folder hierarchy, so <see cref="Files" /> is a single flat list in archive order.
/// </summary>
public sealed record Ba2Archive
{
    /// <summary>Archive header.</summary>
    public required Ba2Header Header { get; init; }

    /// <summary>All file entries, in archive order.</summary>
    public required List<Ba2FileRecord> Files { get; init; }

    /// <summary>Path to the BA2 file on disk.</summary>
    public required string FilePath { get; init; }

    /// <summary>Total number of files in the archive.</summary>
    public int TotalFiles => Files.Count;

    /// <summary>The flat file list (named for parity with <c>BsaArchive.AllFiles</c>).</summary>
    public IEnumerable<Ba2FileRecord> AllFiles => Files;

    /// <summary>Find an entry by its full virtual path (case-insensitive; accepts / or \).</summary>
    public Ba2FileRecord? FindFile(string path)
    {
        var normalized = path.Replace('/', '\\');
        return Files.FirstOrDefault(f =>
            string.Equals(f.FullPath, normalized, StringComparison.OrdinalIgnoreCase));
    }
}
