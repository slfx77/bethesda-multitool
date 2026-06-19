// Copyright (c) 2026 BethesdaMultitool Contributors
// Licensed under the MIT License.
//
// Re-implemented from the publicly documented Bethesda Archive v2 (BA2) format and the
// MIT-licensed fo76utils reference (loadBA2General / loadBA2Textures in
// libfo76utils/src/ba2file.cpp, https://github.com/fo76utils/fo76utils). Record layouts are the
// documented BA2 layouts; this code is not derived from any copyleft implementation.

using System.Text;

namespace BethesdaMultitool.Core.Formats.Bsa.Ba2;

/// <summary>
///     Parser for Bethesda BA2 archives (Fallout 4 + Fallout 76). BA2 is always little-endian, so
///     <see cref="BinaryReader" />'s default byte order is correct. Sits alongside
///     <see cref="BsaParser" /> for the classic .bsa format.
/// </summary>
public static class Ba2Parser
{
    private static readonly byte[] Ba2Magic = "BTDX"u8.ToArray();

    /// <summary>Parse a BA2 archive header and file listing from a path.</summary>
    public static Ba2Archive Parse(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Parse(stream, filePath);
    }

    /// <summary>Parse a BA2 archive from an open stream.</summary>
    public static Ba2Archive Parse(Stream stream, string filePath = "")
    {
        using var reader = new BinaryReader(stream, Encoding.ASCII, true);

        var header = Ba2Header.Read(reader);
        if (header.Type == Ba2HeaderType.Gnmf)
        {
            throw new NotSupportedException(
                "BA2 GNMF (PlayStation GNF texture) archives are not supported; PC FO4/FO76 use GNRL/DX10.");
        }

        var fileCount = (int)header.FileCount;
        var files = new List<Ba2FileRecord>(fileCount);
        var readRecord = header.Type == Ba2HeaderType.Texture
            ? (Func<BinaryReader, int, Ba2FileRecord>)ReadTextureRecord
            : ReadGeneralRecord;

        for (var i = 0; i < fileCount; i++)
        {
            files.Add(readRecord(reader, i));
        }

        if (header.HasNameTable)
        {
            ReadNameTable(stream, reader, header, files);
        }

        return new Ba2Archive
        {
            Header = header,
            Files = files,
            FilePath = filePath
        };
    }

    // Name table: at NameTableOffset, one entry per file in record order — a u16 length followed by
    // the UTF-8 path. Forward slashes are normalised to backslashes for parity with the .bsa parser.
    private static void ReadNameTable(
        Stream stream, BinaryReader reader, Ba2Header header, List<Ba2FileRecord> files)
    {
        stream.Seek((long)header.NameTableOffset, SeekOrigin.Begin);
        for (var i = 0; i < files.Count; i++)
        {
            var length = reader.ReadUInt16();
            var nameBytes = reader.ReadBytes(length);
            files[i].Name = Encoding.UTF8.GetString(nameBytes).Replace('/', '\\');
        }
    }

    // GNRL record (36 bytes): name hash, extension, dir hash, flags, data offset (u64),
    // packed size, real size, trailing alignment dword.
    private static Ba2FileRecord ReadGeneralRecord(BinaryReader reader, int index) => new()
    {
        Kind = Ba2HeaderType.General,
        Index = index,
        NameHash = reader.ReadUInt32(),
        Extension = ReadExtension(reader),
        DirHash = reader.ReadUInt32(),
        Flags = reader.ReadUInt32(),
        Offset = reader.ReadUInt64(),
        PackedSize = reader.ReadUInt32(),
        RealSize = reader.ReadUInt32(),
        Align = reader.ReadUInt32()
    };

    // DX10 record (24 bytes) followed by ChunkCount × 24-byte chunk records. The two bytes at
    // offset 22-23 are a little-endian flag word: bit 0 (low byte) marks a cubemap; the high byte
    // is the surface tile mode (8 = linear/PC, other values = Xbox-tiled).
    private static Ba2FileRecord ReadTextureRecord(BinaryReader reader, int index)
    {
        var nameHash = reader.ReadUInt32();
        var extension = ReadExtension(reader);
        var dirHash = reader.ReadUInt32();

        var unknown = reader.ReadByte();
        var chunkCount = reader.ReadByte();
        var chunkHeaderLength = reader.ReadUInt16();
        var height = reader.ReadUInt16();
        var width = reader.ReadUInt16();
        var mipCount = reader.ReadByte();
        var format = reader.ReadByte();
        var isCubemap = reader.ReadByte();
        var tileMode = reader.ReadByte();

        var chunks = new List<Ba2TextureChunk>(chunkCount);
        for (var c = 0; c < chunkCount; c++)
        {
            chunks.Add(Ba2TextureChunk.Read(reader));
        }

        return new Ba2FileRecord
        {
            Kind = Ba2HeaderType.Texture,
            Index = index,
            NameHash = nameHash,
            Extension = extension,
            DirHash = dirHash,
            Texture = new Ba2TextureInfo
            {
                Unknown = unknown,
                ChunkCount = chunkCount,
                ChunkHeaderLength = chunkHeaderLength,
                Height = height,
                Width = width,
                MipCount = mipCount,
                Format = format,
                IsCubemap = isCubemap,
                TileMode = tileMode,
                Chunks = chunks
            }
        };
    }

    private static string ReadExtension(BinaryReader reader)
        => Encoding.ASCII.GetString(reader.ReadBytes(4)).TrimEnd('\0');

    /// <summary>
    ///     Reads only the header so callers can classify an archive cheaply. Returns null when the
    ///     file is missing, unreadable, or not a valid BA2.
    /// </summary>
    public static Ba2Header? TryReadHeader(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            using var reader = new BinaryReader(stream, Encoding.ASCII, true);
            return Ba2Header.Read(reader);
        }
        catch (Exception ex) when (
            ex is IOException or InvalidDataException or UnauthorizedAccessException or EndOfStreamException)
        {
            return null;
        }
    }

    /// <summary>Returns true when <paramref name="filePath" /> begins with the BA2 "BTDX" magic.</summary>
    public static bool IsBa2File(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            var magic = new byte[4];
            return stream.Read(magic, 0, 4) == 4 && IsBa2File(magic);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Returns true when <paramref name="data" /> begins with the BA2 "BTDX" magic.</summary>
    public static bool IsBa2File(ReadOnlySpan<byte> data)
        => data.Length >= 4 && data[..4].SequenceEqual(Ba2Magic);
}
