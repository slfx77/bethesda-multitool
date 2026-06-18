// Copyright (c) 2026 FalloutXbox360Utils Contributors
// Licensed under the MIT License.

using System.Text;

namespace FalloutXbox360Utils.Core.Formats.Bsa.Ba2;

/// <summary>
///     BA2 archive header. BA2 is ALWAYS little-endian. Layout (ported from Sharp.BSA.BA2):
///     magic "BTDX" (4), version (u32), type tag (4), file count (u32), name-table offset (u64),
///     then version-specific trailing dwords. The file-record table begins immediately after the
///     header the reader stops on.
/// </summary>
public sealed record Ba2Header
{
    /// <summary>BA2 magic tag, always "BTDX".</summary>
    public const string Magic = "BTDX";

    /// <summary>Archive format version (1 = FO4, 2/3/7/8 = FO76 variants).</summary>
    public required uint Version { get; init; }

    /// <summary>Raw 4-char content tag ("GNRL", "DX10", "GNMF").</summary>
    public required string TypeTag { get; init; }

    /// <summary>Parsed content type.</summary>
    public required Ba2HeaderType Type { get; init; }

    /// <summary>Number of file entries in the archive.</summary>
    public required uint FileCount { get; init; }

    /// <summary>Absolute offset of the name table (0 = no name table).</summary>
    public required ulong NameTableOffset { get; init; }

    /// <summary>Codec used by compressed entries.</summary>
    public required Ba2CompressionFormat CompressionFormat { get; init; }

    /// <summary>Whether the archive carries a name table (full virtual paths).</summary>
    public bool HasNameTable => NameTableOffset > 0;

    /// <summary>
    ///     Reads the header from the current position of <paramref name="reader" />, leaving the reader
    ///     positioned at the first file record. Throws <see cref="InvalidDataException" /> on bad magic or
    ///     an unknown content tag.
    /// </summary>
    public static Ba2Header Read(BinaryReader reader)
    {
        var magic = Encoding.ASCII.GetString(reader.ReadBytes(4));
        if (magic != Magic)
        {
            throw new InvalidDataException($"Invalid BA2 magic: expected '{Magic}', got '{magic}'.");
        }

        var version = reader.ReadUInt32();
        var typeTag = Encoding.ASCII.GetString(reader.ReadBytes(4));
        var type = typeTag switch
        {
            "GNRL" => Ba2HeaderType.General,
            "DX10" => Ba2HeaderType.Texture,
            "GNMF" => Ba2HeaderType.Gnmf,
            _ => Ba2HeaderType.Unknown
        };
        if (type == Ba2HeaderType.Unknown)
        {
            throw new InvalidDataException($"Unknown BA2 content type tag: '{typeTag}'.");
        }

        var fileCount = reader.ReadUInt32();
        var nameTableOffset = reader.ReadUInt64();

        // Version-specific trailing dwords. Only version 2 (two) and version 3 (three) carry extras;
        // for version 3 the first extra dword selects the compression codec.
        uint unknown1 = 0;
        if (version == 2)
        {
            _ = reader.ReadUInt32();
            _ = reader.ReadUInt32();
        }
        else if (version == 3)
        {
            unknown1 = reader.ReadUInt32();
            _ = reader.ReadUInt32();
            _ = reader.ReadUInt32();
        }

        var compression = version == 3 && unknown1 == 1
            ? Ba2CompressionFormat.Lz4
            : Ba2CompressionFormat.Zip;

        return new Ba2Header
        {
            Version = version,
            TypeTag = typeTag,
            Type = type,
            FileCount = fileCount,
            NameTableOffset = nameTableOffset,
            CompressionFormat = compression
        };
    }
}
