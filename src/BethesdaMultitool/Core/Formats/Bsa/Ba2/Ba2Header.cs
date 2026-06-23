// Header layout and per-version sizing follow the publicly documented Bethesda Archive v2
// format and the MIT-licensed fo76utils reference (loadArchiveFile / loadBA2General /
// loadBA2Textures in libfo76utils/src/ba2file.cpp). Not derived from any copyleft source.

using System.Text;

namespace BethesdaMultitool.Core.Formats.Bsa.Ba2;

/// <summary>
///     The fixed header at the start of every BA2 archive. BA2 is always little-endian. The base
///     header is 24 bytes — magic "BTDX" (4), version (u32), content tag (4), file count (u32),
///     name-table offset (u64) — followed by a small, version-specific run of extra dwords before
///     the file-record table begins. The reader leaves the stream positioned at the first record.
/// </summary>
public sealed record Ba2Header
{
    /// <summary>BA2 magic tag, always "BTDX".</summary>
    public const string Magic = "BTDX";

    /// <summary>Archive format version. Known values: 1 (FO4), 2/3 (FO76/Starfield), 7/8 (FO76).</summary>
    public required uint Version { get; init; }

    /// <summary>Raw 4-char content tag ("GNRL", "DX10", "GNMF").</summary>
    public required string TypeTag { get; init; }

    /// <summary>Parsed content kind.</summary>
    public required Ba2HeaderType Type { get; init; }

    /// <summary>Number of file entries in the archive.</summary>
    public required uint FileCount { get; init; }

    /// <summary>Absolute offset of the trailing name table (0 = archive has no names).</summary>
    public required ulong NameTableOffset { get; init; }

    /// <summary>Codec used by this archive's compressed payloads.</summary>
    public required Ba2CompressionFormat CompressionFormat { get; init; }

    /// <summary>True when the archive carries a name table of full virtual paths.</summary>
    public bool HasNameTable => NameTableOffset > 0;

    /// <summary>
    ///     Reads the header from the current position of <paramref name="reader" />, leaving it
    ///     positioned at the first file record. Throws <see cref="InvalidDataException" /> for a bad
    ///     magic or an unrecognised content tag.
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

        // After the 24-byte base, versions 2 and 3 carry extra dwords before the record table.
        // Per fo76utils header sizing: general archives add two dwords for versions two and three;
        // texture archives add two dwords for version two and three dwords for version three;
        // versions one, seven and eight add none. For version three the first extra dword selects
        // the compression codec.
        var extraDwords = (version, type) switch
        {
            (2, _) => 2,
            (3, Ba2HeaderType.Texture) => 3,
            (3, _) => 2,
            _ => 0
        };

        uint firstExtra = 0;
        for (var i = 0; i < extraDwords; i++)
        {
            var dword = reader.ReadUInt32();
            if (i == 0)
            {
                firstExtra = dword;
            }
        }

        var compression = version == 3 && firstExtra == 1
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
