// Copyright (c) 2026 Xbox360MemoryCarver Contributors
// Licensed under the MIT License.

using System.Text;

namespace Xbox360MemoryCarver.Core.Formats.Bsa;

/// <summary>
/// Parser for Bethesda BSA archive files.
/// BSA files are ALWAYS little-endian, even for Xbox 360 archives.
/// The Xbox360Archive flag (bit 7) indicates Xbox 360 origin but does NOT affect byte order.
/// </summary>
public static class BsaParser
{
    /// <summary>BSA magic bytes.</summary>
    private static readonly byte[] BsaMagic = "BSA\0"u8.ToArray();

    /// <summary>
    /// Parse a BSA archive header and file listing.
    /// </summary>
    public static BsaArchive Parse(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Parse(stream, filePath);
    }

    /// <summary>
    /// Parse a BSA archive from a stream.
    /// BSA format is ALWAYS little-endian - BinaryReader default is correct.
    /// </summary>
    public static BsaArchive Parse(Stream stream, string filePath = "")
    {
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

        // Read and validate magic
        var magic = reader.ReadBytes(4);
        if (!magic.SequenceEqual(BsaMagic))
        {
            throw new InvalidDataException($"Invalid BSA magic: expected 'BSA\\0', got '{Encoding.ASCII.GetString(magic)}'");
        }

        // Read version - BSA is always little-endian
        var version = reader.ReadUInt32();

        // Valid versions are 103-105
        if (version is < 103 or > 105)
        {
            throw new InvalidDataException($"Invalid BSA version: {version} (expected 103-105)");
        }

        // Read the rest of the header - all little-endian
        var offset = reader.ReadUInt32();
        var archiveFlags = (BsaArchiveFlags)reader.ReadUInt32();
        var folderCount = reader.ReadUInt32();
        var fileCount = reader.ReadUInt32();
        var totalFolderNameLength = reader.ReadUInt32();
        var totalFileNameLength = reader.ReadUInt32();
        var fileFlags = (BsaFileFlags)reader.ReadUInt16();
        var padding = reader.ReadUInt16(); // Padding

        var header = new BsaHeader
        {
            FileId = "BSA",
            Version = version,
            FolderRecordOffset = offset,
            ArchiveFlags = archiveFlags,
            FolderCount = folderCount,
            FileCount = fileCount,
            TotalFolderNameLength = totalFolderNameLength,
            TotalFileNameLength = totalFileNameLength,
            FileFlags = fileFlags
        };

        // Read folder records
        var folders = new List<BsaFolderRecord>((int)folderCount);
        for (int i = 0; i < folderCount; i++)
        {
            var nameHash = reader.ReadUInt64();
            var count = reader.ReadUInt32();
            var folderOffset = reader.ReadUInt32();

            folders.Add(new BsaFolderRecord
            {
                NameHash = nameHash,
                FileCount = count,
                Offset = folderOffset
            });
        }

        // Read file record blocks (folder name + file records)
        var includeNames = archiveFlags.HasFlag(BsaArchiveFlags.IncludeDirectoryNames);

        foreach (var folder in folders)
        {
            // Read folder name if present
            if (includeNames)
            {
                var nameLen = reader.ReadByte();
                var nameBytes = reader.ReadBytes(nameLen);
                // Remove trailing null if present
                var name = Encoding.ASCII.GetString(nameBytes).TrimEnd('\0');
                folder.Name = name;
            }

            // Read file records for this folder
            for (int i = 0; i < folder.FileCount; i++)
            {
                var fileNameHash = reader.ReadUInt64();
                var size = reader.ReadUInt32();
                var fileOffset = reader.ReadUInt32();

                var file = new BsaFileRecord
                {
                    NameHash = fileNameHash,
                    RawSize = size,
                    Offset = fileOffset,
                    Folder = folder
                };
                folder.Files.Add(file);
            }
        }

        // Read file names block if present
        if (archiveFlags.HasFlag(BsaArchiveFlags.IncludeFileNames))
        {
            foreach (var folder in folders)
            {
                foreach (var file in folder.Files)
                {
                    var fileName = ReadNullTerminatedString(reader);
                    file.Name = fileName;
                }
            }
        }

        return new BsaArchive
        {
            Header = header,
            Folders = folders,
            FilePath = filePath
        };
    }

    /// <summary>
    /// Check if a file is a valid BSA archive.
    /// </summary>
    public static bool IsBsaFile(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            var magic = new byte[4];
            if (stream.Read(magic, 0, 4) != 4)
                return false;
            return magic.SequenceEqual(BsaMagic);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Check if a file is a valid BSA archive from data.
    /// </summary>
    public static bool IsBsaFile(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
            return false;
        return data[..4].SequenceEqual(BsaMagic);
    }

    private static string ReadNullTerminatedString(BinaryReader reader)
    {
        var bytes = new List<byte>();
        byte b;
        while ((b = reader.ReadByte()) != 0)
        {
            bytes.Add(b);
        }
        return Encoding.ASCII.GetString(bytes.ToArray());
    }
}
