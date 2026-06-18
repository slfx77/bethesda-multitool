// Copyright (c) 2026 FalloutXbox360Utils Contributors
// Licensed under the MIT License.

using System.Text;

namespace FalloutXbox360Utils.Core.Formats.Bsa.Ba2;

/// <summary>
///     Parser for Bethesda BA2 archive files (Fallout 4 + Fallout 76). BA2 is ALWAYS little-endian.
///     Ported from the community Sharp.BSA.BA2 reference (BSA_Browser) and aligned to this repo's
///     <see cref="BsaParser" /> conventions.
/// </summary>
public static class Ba2Parser
{
    /// <summary>BA2 magic bytes "BTDX".</summary>
    private static readonly byte[] Ba2Magic = "BTDX"u8.ToArray();

    /// <summary>Parse a BA2 archive header and file listing.</summary>
    public static Ba2Archive Parse(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Parse(stream, filePath);
    }

    /// <summary>
    ///     Parse a BA2 archive from a stream. BA2 is always little-endian — BinaryReader's default is
    ///     correct.
    /// </summary>
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
        for (var i = 0; i < fileCount; i++)
        {
            files.Add(header.Type == Ba2HeaderType.Texture
                ? ReadTextureRecord(reader, i)
                : ReadGeneralRecord(reader, i));
        }

        if (header.HasNameTable)
        {
            stream.Seek((long)header.NameTableOffset, SeekOrigin.Begin);
            for (var i = 0; i < fileCount; i++)
            {
                var len = reader.ReadUInt16();
                var nameBytes = reader.ReadBytes(len);
                files[i].Name = Encoding.UTF8.GetString(nameBytes).Replace('/', '\\');
            }
        }

        return new Ba2Archive
        {
            Header = header,
            Files = files,
            FilePath = filePath
        };
    }

    private static Ba2FileRecord ReadGeneralRecord(BinaryReader reader, int index)
    {
        var nameHash = reader.ReadUInt32();
        var extension = ReadExtension(reader);
        var dirHash = reader.ReadUInt32();
        var flags = reader.ReadUInt32();
        var offset = reader.ReadUInt64();
        var packedSize = reader.ReadUInt32();
        var realSize = reader.ReadUInt32();
        var align = reader.ReadUInt32();

        return new Ba2FileRecord
        {
            Kind = Ba2HeaderType.General,
            Index = index,
            NameHash = nameHash,
            Extension = extension,
            DirHash = dirHash,
            Flags = flags,
            Offset = offset,
            PackedSize = packedSize,
            RealSize = realSize,
            Align = align
        };
    }

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
    ///     Reads only the BA2 header so callers can classify an archive cheaply. Returns null if the
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

    /// <summary>Check if a file is a valid BA2 archive (by "BTDX" magic).</summary>
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

    /// <summary>Check if data begins with the BA2 "BTDX" magic.</summary>
    public static bool IsBa2File(ReadOnlySpan<byte> data)
        => data.Length >= 4 && data[..4].SequenceEqual(Ba2Magic);
}
