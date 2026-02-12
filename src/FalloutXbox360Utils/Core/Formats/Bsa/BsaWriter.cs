// Copyright (c) 2026 FalloutXbox360Utils Contributors
// Licensed under the MIT License.

using System.IO.Compression;
using System.Text;

namespace FalloutXbox360Utils.Core.Formats.Bsa;

/// <summary>
///     Writer for Bethesda BSA archive files (version 104, FO3/FNV).
///     Creates PC-compatible BSA archives with zlib compression support.
///     Hash algorithm and flag logic verified against BSArchPro (wbBSArchive.pas).
/// </summary>
public sealed class BsaWriter : IDisposable
{
    /// <summary>BSA header size (36 bytes).</summary>
    private const int HeaderSize = 36;

    /// <summary>Folder record size (16 bytes).</summary>
    private const int FolderRecordSize = 16;

    /// <summary>File record size (16 bytes).</summary>
    private const int FileRecordSize = 16;

    /// <summary>BSA magic bytes.</summary>
    private static readonly byte[] BsaMagic = "BSA\0"u8.ToArray();

    private readonly BsaArchiveFlags _archiveFlags;
    private readonly bool _compressFiles;
    private readonly CompressionLevel _compressionLevel;
    private readonly bool _embedFileNames;
    private readonly BsaFileFlags _fileFlags;

    private readonly Dictionary<string, List<PendingFile>> _folders = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>
    ///     Creates a new BSA writer with explicit flags.
    /// </summary>
    /// <param name="compressFiles">Whether to compress file data with zlib.</param>
    /// <param name="fileFlags">Content type flags for the archive.</param>
    /// <param name="embedFileNames">Whether to embed filenames in file data blocks (FO3/FNV texture BSAs).</param>
    /// <param name="compressionLevel">Zlib compression level (default: Optimal for closest match with Bethesda tools).</param>
    public BsaWriter(bool compressFiles = true, BsaFileFlags fileFlags = BsaFileFlags.None,
        bool embedFileNames = false,
        CompressionLevel compressionLevel = CompressionLevel.Optimal)
    {
        _compressFiles = compressFiles;
        _fileFlags = fileFlags;
        _embedFileNames = embedFileNames;
        _compressionLevel = compressionLevel;

        _archiveFlags = BsaArchiveFlags.IncludeDirectoryNames | BsaArchiveFlags.IncludeFileNames;
        if (compressFiles)
        {
            _archiveFlags |= BsaArchiveFlags.CompressedArchive;
        }

        if (embedFileNames)
        {
            _archiveFlags |= BsaArchiveFlags.EmbedFileNames;
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _folders.Clear();
            _disposed = true;
        }
    }

    /// <summary>
    ///     Creates a BSA writer with auto-detected flags based on file content.
    ///     Follows BSArchPro conventions for FO3/FNV archives:
    ///     - FileFlags auto-detected from folder paths and extensions
    ///     - EmbedFileNames enabled for texture-only archives
    ///     - Fonts/XML/TXT file flags stripped (Oblivion-only)
    /// </summary>
    /// <param name="files">List of relative file paths to be added.</param>
    /// <param name="compressFiles">Whether to compress file data with zlib.</param>
    /// <param name="compressionLevel">Zlib compression level.</param>
    public static BsaWriter CreateWithAutoFlags(IEnumerable<string> files, bool compressFiles = true,
        CompressionLevel compressionLevel = CompressionLevel.Optimal)
    {
        var fileFlags = BsaFileFlags.None;

        foreach (var file in files)
        {
            var normalized = file.Replace('/', '\\').TrimStart('\\');
            var ext = Path.GetExtension(normalized).ToLowerInvariant();
            var dir = Path.GetDirectoryName(normalized)?.ToLowerInvariant() ?? "";

            // Folder-based detection first (like BSArchPro)
            if (dir.StartsWith("meshes\\", StringComparison.Ordinal) || dir == "meshes")
            {
                fileFlags |= BsaFileFlags.Meshes;
            }
            else if (dir.StartsWith("textures\\", StringComparison.Ordinal) || dir == "textures")
            {
                fileFlags |= BsaFileFlags.Textures;
            }
            else if (dir.StartsWith("sound\\", StringComparison.Ordinal) || dir == "sound")
            {
                fileFlags |= BsaFileFlags.Sounds | BsaFileFlags.Voices;
            }
            else
            {
                // Extension-based fallback
                fileFlags |= ext switch
                {
                    ".nif" or ".kf" or ".kfm" or ".egm" or ".egt" or ".tri" or ".hkx" or ".lod" or ".bto"
                        or ".btr" => BsaFileFlags.Meshes,
                    ".dds" or ".tga" or ".bmp" or ".png" => BsaFileFlags.Textures,
                    ".wav" or ".fuz" => BsaFileFlags.Sounds,
                    ".lip" or ".mp3" or ".ogg" => BsaFileFlags.Voices,
                    ".xml" or ".swf" => BsaFileFlags.Menus | BsaFileFlags.Misc,
                    ".spt" => BsaFileFlags.Trees,
                    // .fnt and .tex are Oblivion-only flags, stripped for FO3/FNV (see below)
                    _ => BsaFileFlags.None
                };
            }
        }

        // FO3/FNV: strip Oblivion-only flags (Fonts, Menus text, Misc XML)
        // Reference: BSArchPro line 1519-1521
        fileFlags &= ~(BsaFileFlags.Fonts);

        // Texture-only archives get EmbedFileNames (BSArchPro line 1508-1509)
        var embedNames = fileFlags == BsaFileFlags.Textures;

        return new BsaWriter(compressFiles, fileFlags, embedNames, compressionLevel);
    }

    /// <summary>
    ///     Adds a file to the archive.
    /// </summary>
    /// <param name="relativePath">Relative path within archive (e.g., "meshes\clutter\bucket.nif")</param>
    /// <param name="data">File data</param>
    public void AddFile(string relativePath, byte[] data)
    {
        // Normalize path separators
        relativePath = relativePath.Replace('/', '\\').TrimStart('\\');

        var lastSlash = relativePath.LastIndexOf('\\');
        string folderName;
        string fileName;

        if (lastSlash >= 0)
        {
            folderName = relativePath[..lastSlash].ToLowerInvariant();
            fileName = relativePath[(lastSlash + 1)..].ToLowerInvariant();
        }
        else
        {
            folderName = "";
            fileName = relativePath.ToLowerInvariant();
        }

        if (!_folders.TryGetValue(folderName, out var files))
        {
            files = [];
            _folders[folderName] = files;
        }

        files.Add(new PendingFile(fileName, relativePath.Replace('/', '\\').TrimStart('\\'), data));
    }

    /// <summary>
    ///     Writes the BSA archive to a file.
    /// </summary>
    public void Write(string outputPath)
    {
        using var stream = File.Create(outputPath);
        Write(stream);
    }

    /// <summary>
    ///     Writes the BSA archive to a stream.
    /// </summary>
    public void Write(Stream stream)
    {
        using var writer = new BinaryWriter(stream, Encoding.ASCII, true);

        // Sort folders and files by hash
        var sortedFolders = _folders
            .OrderBy(kvp => HashPath(kvp.Key, true))
            .Select(kvp => new
            {
                Name = kvp.Key,
                Hash = HashPath(kvp.Key, true),
                Files = kvp.Value.OrderBy(f => HashPath(f.Name, false)).ToList()
            })
            .ToList();

        var folderCount = (uint)sortedFolders.Count;
        var fileCount = (uint)sortedFolders.Sum(f => f.Files.Count);

        // Calculate total folder/file name lengths (including null terminators)
        // Note: TotalFolderNameLength does NOT include the length bytes, only name chars + null terminators
        var totalFolderNameLength = (uint)sortedFolders.Sum(f => f.Name.Length + 1); // +1 for null terminator only
        var totalFileNameLength =
            (uint)sortedFolders.Sum(f => f.Files.Sum(file => file.Name.Length + 1)); // +1 for null

        // Calculate offsets
        // File record blocks contain: 1 byte (name length) + name bytes + null + file records per folder
        // So total size = folderCount (length bytes) + totalFolderNameLength (name+null) + fileCount * FileRecordSize
        var folderRecordsOffset = (uint)HeaderSize;
        var fileRecordsOffset = folderRecordsOffset + folderCount * FolderRecordSize;
        var fileNamesOffset = fileRecordsOffset + folderCount + totalFolderNameLength + fileCount * FileRecordSize;
        var fileDataOffset = fileNamesOffset + totalFileNameLength;

        // Write header
        writer.Write(BsaMagic);
        writer.Write(104u); // Version
        writer.Write(folderRecordsOffset);
        writer.Write((uint)_archiveFlags);
        writer.Write(folderCount);
        writer.Write(fileCount);
        writer.Write(totalFolderNameLength);
        writer.Write(totalFileNameLength);
        writer.Write((ushort)_fileFlags);
        writer.Write((ushort)0); // Padding

        // Build file records with data offsets
        var currentDataOffset = fileDataOffset;
        var folderRecords = new List<FolderRecordData>();

        foreach (var folder in sortedFolders)
        {
            var fileRecords = new List<FileRecordData>();

            foreach (var file in folder.Files)
            {
                var fileData = PrepareFileData(file.Data, file.FullPath);
                var size = (uint)fileData.Length;

                // If compression is toggled for this file (individual file toggle)
                // For now we just use archive default, no per-file toggle

                fileRecords.Add(new FileRecordData
                {
                    Hash = HashPath(file.Name, false),
                    Size = size,
                    Offset = currentDataOffset,
                    Data = fileData,
                    Name = file.Name
                });

                currentDataOffset += (uint)fileData.Length;
            }

            folderRecords.Add(new FolderRecordData
            {
                Hash = folder.Hash,
                FileCount = (uint)folder.Files.Count,
                Name = folder.Name,
                Files = fileRecords
            });
        }

        // Write folder records (first pass to set offsets)
        var currentFileRecordOffset = fileRecordsOffset;
        foreach (var folder in folderRecords)
        {
            writer.Write(folder.Hash);
            writer.Write(folder.FileCount);
            writer.Write(currentFileRecordOffset);

            // Offset includes folder name length byte + null + name + file records
            currentFileRecordOffset += (uint)(1 + folder.Name.Length + 1 + folder.FileCount * FileRecordSize);
        }

        // Write file record blocks (folder name + file records)
        foreach (var folder in folderRecords)
        {
            // Write folder name (length-prefixed, null-terminated)
            var folderNameBytes = Encoding.ASCII.GetBytes(folder.Name + "\0");
            writer.Write((byte)folderNameBytes.Length);
            writer.Write(folderNameBytes);

            // Write file records
            foreach (var file in folder.Files)
            {
                writer.Write(file.Hash);
                writer.Write(file.Size);
                writer.Write(file.Offset);
            }
        }

        // Write file names block (null-terminated strings)
        foreach (var folder in folderRecords)
        {
            foreach (var file in folder.Files)
            {
                var nameBytes = Encoding.ASCII.GetBytes(file.Name + "\0");
                writer.Write(nameBytes);
            }
        }

        // Write file data
        foreach (var folder in folderRecords)
        {
            foreach (var file in folder.Files)
            {
                writer.Write(file.Data);
            }
        }
    }

    private byte[] PrepareFileData(byte[] data, string fullPath)
    {
        using var output = new MemoryStream();
        using var bw = new BinaryWriter(output);

        // Embedded filename (BString: length byte + chars, no null terminator)
        // Written BEFORE compression header, per BSArchPro line 1863-1865
        if (_embedFileNames)
        {
            var pathBytes = Encoding.ASCII.GetBytes(fullPath.ToLowerInvariant());
            bw.Write((byte)pathBytes.Length);
            bw.Write(pathBytes);
        }

        if (_compressFiles)
        {
            // Write original size first (for decompression)
            bw.Write((uint)data.Length);

            // Compress with zlib
            using (var zlibStream = new ZLibStream(output, _compressionLevel, true))
            {
                zlibStream.Write(data);
            }
        }
        else
        {
            bw.Write(data);
        }

        return output.ToArray();
    }

    /// <summary>
    ///     Computes BSA hash for a path or filename.
    ///     Algorithm verified against BSArchPro CreateHashTES4 (wbBSArchive.pas:733-776).
    /// </summary>
    private static ulong HashPath(string path, bool isFolder)
    {
        if (string.IsNullOrEmpty(path))
        {
            return 0;
        }

        path = path.ToLowerInvariant().Replace('/', '\\');

        if (isFolder)
        {
            // For folders, use the full path as name with empty extension
            return ComputeHash(path, "");
        }

        // For files, separate name and extension
        var lastDot = path.LastIndexOf('.');
        if (lastDot >= 0)
        {
            var name = path[..lastDot];
            var ext = path[lastDot..];
            return ComputeHash(name, ext);
        }

        return ComputeHash(path, "");
    }

    /// <summary>
    ///     Computes the BSA hash for a name+extension pair.
    ///     Reference: BSArchPro CreateHashTES4 (wbBSArchive.pas:733-776).
    ///     CRITICAL: Name-middle hash and extension hash are computed SEPARATELY then added.
    /// </summary>
    private static ulong ComputeHash(string name, string extension)
    {
        if (string.IsNullOrEmpty(name))
        {
            return 0;
        }

        var nameBytes = Encoding.ASCII.GetBytes(name);
        var extBytes = Encoding.ASCII.GetBytes(extension);

        // Hash1 - based on name endpoints, length, and extension type
        ulong hash1 = 0;
        if (nameBytes.Length > 0)
        {
            hash1 = (ulong)(nameBytes[^1] | // Last char
                            (nameBytes.Length > 2 ? nameBytes[^2] << 8 : 0) | // Second to last
                            (nameBytes.Length << 16) | // Length
                            (nameBytes[0] << 24)); // First char

            // Extension contribution to hash1 (specific well-known extensions)
            switch (extension.ToLowerInvariant())
            {
                case ".kf":
                    hash1 |= 0x80;
                    break;
                case ".nif":
                    hash1 |= 0x8000;
                    break;
                case ".dds":
                    hash1 |= 0x8080;
                    break;
                case ".wav":
                    hash1 |= 0x80000000;
                    break;
            }
        }

        // Hash2 - name middle chars (index 1 through length-3)
        // IMPORTANT: computed separately from extension hash, then added
        uint nameHash = 0;
        for (var i = 1; i < nameBytes.Length - 2; i++)
        {
            nameHash = nameBytes[i] + (nameHash << 6) + (nameHash << 16) - nameHash;
        }

        // Extension hash (starts fresh from 0, NOT continuing from nameHash)
        uint extHash = 0;
        foreach (var b in extBytes)
        {
            extHash = b + (extHash << 6) + (extHash << 16) - extHash;
        }

        // High 32 bits = nameHash + extHash (as separate additions to upper half)
        var hash2 = (ulong)(nameHash + extHash);
        return (hash2 << 32) | hash1;
    }

    private sealed record PendingFile(string Name, string FullPath, byte[] Data);

    private sealed class FolderRecordData
    {
        public required ulong Hash { get; init; }
        public required uint FileCount { get; init; }
        public required string Name { get; init; }
        public required List<FileRecordData> Files { get; init; }
    }

    private sealed class FileRecordData
    {
        public required ulong Hash { get; init; }
        public required uint Size { get; set; }
        public required uint Offset { get; set; }
        public required byte[] Data { get; init; }
        public required string Name { get; init; }
    }
}
