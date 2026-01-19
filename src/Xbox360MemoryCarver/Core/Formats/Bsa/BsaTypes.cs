// Copyright (c) 2026 Xbox360MemoryCarver Contributors
// Licensed under the MIT License.

namespace Xbox360MemoryCarver.Core.Formats.Bsa;

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

/// <summary>
///     BSA archive flags.
/// </summary>
[Flags]
public enum BsaArchiveFlags : uint
{
    None = 0,

    /// <summary>Include directory names in archive.</summary>
    IncludeDirectoryNames = 0x0001,

    /// <summary>Include file names in archive.</summary>
    IncludeFileNames = 0x0002,

    /// <summary>Files are compressed by default.</summary>
    CompressedArchive = 0x0004,

    /// <summary>Retain directory names (?).</summary>
    RetainDirectoryNames = 0x0008,

    /// <summary>Retain file names (?).</summary>
    RetainFileNames = 0x0010,

    /// <summary>Retain file name offsets (?).</summary>
    RetainFileNameOffsets = 0x0020,

    /// <summary>Xbox 360 archive - all numbers are big-endian.</summary>
    Xbox360Archive = 0x0040,

    /// <summary>Retain strings during startup (?).</summary>
    RetainStringsDuringStartup = 0x0080,

    /// <summary>Embed file names in file data blocks.</summary>
    EmbedFileNames = 0x0100,

    /// <summary>XMem codec compression (Xbox 360).</summary>
    XMemCodec = 0x0200
}

/// <summary>
///     BSA file content type flags.
/// </summary>
[Flags]
public enum BsaFileFlags : ushort
{
    None = 0,
    Meshes = 0x0001,
    Textures = 0x0002,
    Menus = 0x0004,
    Sounds = 0x0008,
    Voices = 0x0010,
    Shaders = 0x0020,
    Trees = 0x0040,
    Fonts = 0x0080,
    Misc = 0x0100
}

/// <summary>
///     BSA folder record structure.
/// </summary>
public record BsaFolderRecord
{
    /// <summary>64-bit hash of folder path.</summary>
    public required ulong NameHash { get; init; }

    /// <summary>Number of files in this folder.</summary>
    public required uint FileCount { get; init; }

    /// <summary>Offset to file records for this folder.</summary>
    public required uint Offset { get; init; }

    /// <summary>Folder name (populated during parsing).</summary>
    public string? Name { get; set; }

    /// <summary>Files in this folder (populated during parsing).</summary>
    public List<BsaFileRecord> Files { get; } = [];
}

/// <summary>
///     BSA file record structure.
/// </summary>
public record BsaFileRecord
{
    /// <summary>64-bit hash of file name.</summary>
    public required ulong NameHash { get; init; }

    /// <summary>File size (bit 30 toggles compression from default).</summary>
    public required uint RawSize { get; init; }

    /// <summary>Offset to file data from start of archive.</summary>
    public required uint Offset { get; init; }

    /// <summary>File name (populated during parsing).</summary>
    public string? Name { get; set; }

    /// <summary>Parent folder (populated during parsing).</summary>
    public BsaFolderRecord? Folder { get; set; }

    /// <summary>Actual file size (without compression toggle bit).</summary>
    public uint Size => RawSize & 0x3FFFFFFF;

    /// <summary>Whether compression is toggled from archive default.</summary>
    public bool CompressionToggle => (RawSize & 0x40000000) != 0;

    /// <summary>Full path (folder + filename).</summary>
    public string FullPath => Folder?.Name is not null && Name is not null
        ? $"{Folder.Name}\\{Name}"
        : Name ?? $"unknown_{NameHash:X16}";
}

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
}

/// <summary>
///     Result of extracting a single file from BSA.
/// </summary>
public record BsaExtractResult
{
    public required string SourcePath { get; init; }
    public required string OutputPath { get; init; }
    public required bool Success { get; init; }
    public required long OriginalSize { get; init; }
    public required long ExtractedSize { get; init; }
    public required bool WasCompressed { get; init; }
    public bool WasConverted { get; init; }
    public string? ConversionType { get; init; }
    public string? Error { get; init; }
}
