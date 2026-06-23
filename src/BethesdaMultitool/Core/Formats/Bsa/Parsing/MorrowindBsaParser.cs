using System.Text;
using BethesdaMultitool.Core.Formats.Bsa.Extraction;
using BethesdaMultitool.Core.Formats.Bsa.Models;

namespace BethesdaMultitool.Core.Formats.Bsa.Parsing;

/// <summary>
///     Parser for the legacy Morrowind BSA format, which predates the "BSA\0"-magic archives
///     (Oblivion v103 .. Skyrim SE v105). A Morrowind archive has no magic string: the first four
///     bytes are the version number <c>0x00000100</c>. Layout (all little-endian):
///     <code>
///     uint32 version    = 0x100
///     uint32 hashOffset                 ; bytes from byte 12 to the hash table
///     uint32 fileCount
///     { uint32 size, uint32 offset } x fileCount   ; offset is relative to the data section
///     uint32 nameOffset x fileCount                ; offset into the names block
///     byte[] names                                 ; concatenated NUL-terminated full paths
///     { uint64 } x fileCount                        ; hash table (unused here)
///     byte[] data                                   ; file data, UNCOMPRESSED
///     </code>
///     Files are uncompressed and names are full backslash paths (e.g. <c>meshes\x\foo.nif</c>).
///     This parser populates the same <see cref="BsaArchive" /> model the rest of the stack consumes:
///     paths are split on the last '\' into synthetic folders, and <see cref="BsaFileRecord.Offset" />
///     is stored ABSOLUTE so <see cref="BsaExtractor" />'s uncompressed read path works unchanged
///     (the header carries no Compressed/EmbedFileNames flags).
/// </summary>
public static class MorrowindBsaParser
{
    /// <summary>The version dword that stands in for a magic string in Morrowind archives.</summary>
    public const uint MorrowindVersion = 0x00000100;

    /// <summary>Parse a Morrowind BSA from a file path.</summary>
    public static BsaArchive Parse(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var reader = new BinaryReader(stream, Encoding.ASCII, true);
        return Parse(reader, filePath);
    }

    /// <summary>
    ///     Parse a Morrowind BSA from a reader positioned at the start of the archive.
    /// </summary>
    public static BsaArchive Parse(BinaryReader reader, string filePath = "")
    {
        var version = reader.ReadUInt32();
        if (version != MorrowindVersion)
        {
            throw new InvalidDataException(
                $"Invalid Morrowind BSA version: 0x{version:X8} (expected 0x{MorrowindVersion:X8})");
        }

        var hashOffset = reader.ReadUInt32();
        var fileCount = reader.ReadUInt32();
        if (fileCount > 1_000_000)
        {
            throw new InvalidDataException($"Implausible Morrowind BSA file count: {fileCount}");
        }

        var sizes = new uint[fileCount];
        var offsets = new uint[fileCount];
        for (var i = 0; i < fileCount; i++)
        {
            sizes[i] = reader.ReadUInt32();
            offsets[i] = reader.ReadUInt32();
        }

        var nameOffsets = new uint[fileCount];
        for (var i = 0; i < fileCount; i++)
        {
            nameOffsets[i] = reader.ReadUInt32();
        }

        // The names block fills the remainder of the pre-hash region: hashOffset spans the file
        // records (8 bytes each) + name offsets (4 bytes each) + the names block.
        var nameBlockLength = (int)hashOffset - (int)(12 * fileCount);
        if (nameBlockLength < 0)
        {
            throw new InvalidDataException("Corrupt Morrowind BSA: negative name block length.");
        }

        var nameBlock = reader.ReadBytes(nameBlockLength);

        // Data section follows the hash table (8 bytes per file) which sits at byte 12 + hashOffset.
        var dataStart = 12L + hashOffset + 8L * fileCount;

        var folderMap = new Dictionary<string, BsaFolderRecord>(StringComparer.OrdinalIgnoreCase);
        var folders = new List<BsaFolderRecord>();
        long totalFolderNameLength = 0;

        for (var i = 0; i < fileCount; i++)
        {
            var fullPath = ReadCString(nameBlock, (int)nameOffsets[i]).Replace('/', '\\');
            var sep = fullPath.LastIndexOf('\\');
            var folderName = sep >= 0 ? fullPath[..sep] : string.Empty;
            var fileName = sep >= 0 ? fullPath[(sep + 1)..] : fullPath;

            if (!folderMap.TryGetValue(folderName, out var folder))
            {
                folder = new BsaFolderRecord { NameHash = 0, FileCount = 0, Offset = 0, Name = folderName };
                folderMap[folderName] = folder;
                folders.Add(folder);
                totalFolderNameLength += folderName.Length + 1;
            }

            folder.Files.Add(new BsaFileRecord
            {
                NameHash = 0,
                RawSize = sizes[i],
                Offset = (uint)(dataStart + offsets[i]),
                Name = fileName,
                Folder = folder
            });
        }

        // FileCount is init-only; recreate folder records now that per-folder counts are known.
        var finalFolders = new List<BsaFolderRecord>(folders.Count);
        foreach (var folder in folders)
        {
            var rebuilt = new BsaFolderRecord
            {
                NameHash = 0,
                FileCount = (uint)folder.Files.Count,
                Offset = 0,
                Name = folder.Name
            };
            foreach (var file in folder.Files)
            {
                rebuilt.Files.Add(file with { Folder = rebuilt });
            }

            finalFolders.Add(rebuilt);
        }

        var header = new BsaHeader
        {
            FileId = "Morrowind",
            Version = MorrowindVersion,
            FolderRecordOffset = 12,
            // Names are available; NOT compressed and NOT embedded (so BsaExtractor reads raw bytes).
            ArchiveFlags = BsaArchiveFlags.IncludeDirectoryNames | BsaArchiveFlags.IncludeFileNames,
            FolderCount = (uint)finalFolders.Count,
            FileCount = fileCount,
            TotalFolderNameLength = (uint)totalFolderNameLength,
            TotalFileNameLength = (uint)nameBlockLength,
            FileFlags = BsaFileFlags.None,
            IsMorrowind = true
        };

        return new BsaArchive
        {
            Header = header,
            Folders = finalFolders,
            FilePath = filePath
        };
    }

    private static string ReadCString(byte[] buffer, int offset)
    {
        if (offset < 0 || offset >= buffer.Length)
        {
            return string.Empty;
        }

        var end = offset;
        while (end < buffer.Length && buffer[end] != 0)
        {
            end++;
        }

        return Encoding.ASCII.GetString(buffer, offset, end - offset);
    }
}
