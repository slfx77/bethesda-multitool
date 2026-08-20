using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Bsa.Models;

namespace BethesdaMultitool.Core.Formats.Bsa.Parsing;

/// <summary>
///     Parser for Bethesda BSA archive files.
///     BSA files are ALWAYS little-endian, even for Xbox 360 archives.
///     The Xbox360Archive flag (bit 7) indicates Xbox 360 origin but does NOT affect byte order.
/// </summary>
public static class BsaParser
{
    /// <summary>BSA magic bytes.</summary>
    private static readonly byte[] BsaMagic = "BSA\0"u8.ToArray();

    /// <summary>
    ///     Parse a BSA archive header and file listing.
    /// </summary>
    public static BsaArchive Parse(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Parse(stream, filePath);
    }

    /// <summary>
    ///     Parse a BSA archive from a stream.
    ///     BSA format is ALWAYS little-endian - BinaryReader default is correct.
    /// </summary>
    public static BsaArchive Parse(Stream stream, string filePath = "")
    {
        using var reader = new BinaryReader(stream, Encoding.ASCII, true);

        // The legacy Morrowind format has no "BSA\0" magic — its first dword is the version 0x100.
        var firstWord = reader.ReadUInt32();
        stream.Seek(0, SeekOrigin.Begin);
        if (firstWord == MorrowindBsaParser.MorrowindVersion)
        {
            return MorrowindBsaParser.Parse(reader, filePath);
        }

        var header = ReadHeaderCore(reader);
        var archiveFlags = header.ArchiveFlags;
        var folderCount = header.FolderCount;

        // Read folder records. v103/v104 use a 16-byte record (hash+count+u32 offset); v105
        // (Skyrim Special Edition) inserts a 4-byte pad and widens the offset to 64-bit, making
        // the record 24 bytes. The folder offset itself is only informational here (file records
        // carry absolute data offsets), so the widened value is truncated to uint for storage.
        var isV105 = header.Version >= 105;

        // The declared counts must physically fit in the stream BEFORE they size any allocation:
        // a lying header would otherwise commit gigabytes up front. Each file record is 16 bytes
        // (hash + size + offset) at every version.
        var folderRecordSize = isV105 ? 24 : 16;
        var remaining = stream.Length - stream.Position;
        if (folderCount * folderRecordSize > remaining)
        {
            throw new InvalidDataException(
                $"BSA folder count {folderCount} cannot fit in {stream.Length}-byte archive");
        }

        if (folderCount * folderRecordSize + (long)header.FileCount * 16 > remaining)
        {
            throw new InvalidDataException(
                $"BSA file count {header.FileCount} cannot fit in {stream.Length}-byte archive");
        }

        var folders = new List<BsaFolderRecord>((int)folderCount);
        for (var i = 0; i < folderCount; i++)
        {
            var nameHash = reader.ReadUInt64();
            var count = reader.ReadUInt32();
            uint folderOffset;
            if (isV105)
            {
                _ = reader.ReadUInt32(); // padding (unknown / always 0)
                folderOffset = (uint)reader.ReadUInt64();
            }
            else
            {
                folderOffset = reader.ReadUInt32();
            }

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
            for (var i = 0; i < folder.FileCount; i++)
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
    ///     Reads and validates the 36-byte BSA header from the current reader position. Shared by
    ///     <see cref="Parse(Stream, string)" /> and <see cref="TryReadHeader" />. Throws
    ///     <see cref="InvalidDataException" /> on bad magic or unsupported version.
    /// </summary>
    private static BsaHeader ReadHeaderCore(BinaryReader reader)
    {
        var magic = reader.ReadBytes(4);
        if (!magic.SequenceEqual(BsaMagic))
        {
            throw new InvalidDataException(
                $"Invalid BSA magic: expected 'BSA\\0', got '{Encoding.ASCII.GetString(magic)}'");
        }

        // The remaining 32 header bytes must be present before any field reads — a truncated
        // header should fail as InvalidDataException, not EndOfStreamException.
        var stream = reader.BaseStream;
        if (stream.Length - stream.Position < 32)
        {
            throw new InvalidDataException(
                $"BSA header truncated: {stream.Length} bytes is smaller than the 36-byte header");
        }

        // BSA is always little-endian; valid versions are 103-105.
        var version = reader.ReadUInt32();
        if (version is < 103 or > 105)
        {
            throw new InvalidDataException($"Invalid BSA version: {version} (expected 103-105)");
        }

        var offset = reader.ReadUInt32();
        var archiveFlags = (BsaArchiveFlags)reader.ReadUInt32();
        var folderCount = reader.ReadUInt32();
        var fileCount = reader.ReadUInt32();
        var totalFolderNameLength = reader.ReadUInt32();
        var totalFileNameLength = reader.ReadUInt32();
        var fileFlags = (BsaFileFlags)reader.ReadUInt16();
        _ = reader.ReadUInt16(); // Padding

        return new BsaHeader
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
    }

    /// <summary>
    ///     Reads only the BSA header (no folder/file record tables) so callers can classify an archive
    ///     by its <see cref="BsaFileFlags" /> content bits cheaply. Returns null if the file is missing,
    ///     unreadable, or not a valid BSA.
    /// </summary>
    public static BsaHeader? TryReadHeader(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            using var reader = new BinaryReader(stream, Encoding.ASCII, true);
            var firstWord = reader.ReadUInt32();
            stream.Seek(0, SeekOrigin.Begin);
            if (firstWord == MorrowindBsaParser.MorrowindVersion)
            {
                // Full parse is needed to populate the Morrowind header (folder/file counts come from
                // the body, not a fixed header), but it's cheap relative to the rest of the pipeline.
                return MorrowindBsaParser.Parse(reader, filePath).Header;
            }

            return ReadHeaderCore(reader);
        }
        catch (Exception ex) when (
            ex is IOException or InvalidDataException or UnauthorizedAccessException or EndOfStreamException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Check if a file is a valid BSA archive.
    /// </summary>
    public static bool IsBsaFile(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            var magic = new byte[4];
            if (stream.Read(magic, 0, 4) != 4)
            {
                return false;
            }

            return IsBsaFile(magic);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///     Check if a file is a valid BSA archive from data. Accepts the "BSA\0" magic (v103-105) and the
    ///     legacy Morrowind format (first dword = version 0x100, no magic string).
    /// </summary>
    public static bool IsBsaFile(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
        {
            return false;
        }

        if (data[..4].SequenceEqual(BsaMagic))
        {
            return true;
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(data) == MorrowindBsaParser.MorrowindVersion;
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
