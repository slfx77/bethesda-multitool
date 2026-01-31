// Copyright (c) 2026 FalloutXbox360Utils Contributors
// Licensed under the MIT License.

namespace FalloutXbox360Utils.Core.Formats.Bsa;

/// <summary>
///     BSA archive header structure (36 bytes).
/// </summary>
public record BsaHeader
{
    /// <summary>Magic bytes "BSA\0".</summary>
    public required string FileId { get; init; }

    /// <summary>Version: 104 (0x68) for FO3/FNV/Skyrim, 105 (0x69) for SSE.</summary>
    public required uint Version { get; init; }

    /// <summary>Offset to folder records (always 36 for v104).</summary>
    public required uint FolderRecordOffset { get; init; }

    /// <summary>Archive flags - bit 7 indicates Xbox 360 origin (NOT byte order).</summary>
    public required BsaArchiveFlags ArchiveFlags { get; init; }

    /// <summary>Total number of folders in archive.</summary>
    public required uint FolderCount { get; init; }

    /// <summary>Total number of files in archive.</summary>
    public required uint FileCount { get; init; }

    /// <summary>Total length of all folder names.</summary>
    public required uint TotalFolderNameLength { get; init; }

    /// <summary>Total length of all file names.</summary>
    public required uint TotalFileNameLength { get; init; }

    /// <summary>Content type flags.</summary>
    public required BsaFileFlags FileFlags { get; init; }

    /// <summary>Whether this archive originated from Xbox 360 (flag only, data is still little-endian).</summary>
    public bool IsXbox360 => ArchiveFlags.HasFlag(BsaArchiveFlags.Xbox360Archive);

    /// <summary>Whether files are compressed by default.</summary>
    public bool DefaultCompressed => ArchiveFlags.HasFlag(BsaArchiveFlags.CompressedArchive);

    /// <summary>Whether file names are embedded in file data blocks.</summary>
    public bool EmbedFileNames => ArchiveFlags.HasFlag(BsaArchiveFlags.EmbedFileNames);
}
