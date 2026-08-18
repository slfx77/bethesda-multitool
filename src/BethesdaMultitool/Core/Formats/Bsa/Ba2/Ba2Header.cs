// Header layout follows the publicly documented Bethesda Archive v2 format. Per-version sizing
// follows xEdit's writer (Core/wbBSArchive.pas, MPL-2.0 — read, not transliterated) and the 0BSD
// bsa-rs reference, both of which size a version-3 header the same way for GNRL and DX10. The
// MIT fo76utils reference (loadBA2General / loadBA2Textures in libfo76utils/src/ba2file.cpp) is
// still the source for the record and chunk layouts, but is NOT authoritative on version-3 header
// sizing — it gives version-3 GNRL 32 bytes where the other two give 36. Not derived from any
// copyleft source.

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

        // After the 24-byte base, versions 2 and 3 carry extra dwords before the record table, and the
        // count depends only on the VERSION — not on the content tag. Version 2 adds two (Unknown1,
        // Unknown2); version 3 adds a third, CompressionMethod. Versions 1, 7 and 8 add none.
        //
        // fo76utils sizes a version-3 GNRL header at 32 bytes, which is wrong; xEdit's writer
        // (Core/wbBSArchive.pas, `if Version >= HEADER_VERSION_SFv3 then CompressionMethod := ...`)
        // and the bsa-rs reference both read CompressionMethod for version 3 regardless of tag.
        // Retail Starfield ships no version-3 GNRL archive, so the difference only shows on an
        // archive rebuilt by BSArch/Archive2 — which would otherwise read the record table 4 bytes
        // early and produce garbage entries.
        var extraDwords = version switch
        {
            2 => 2,
            3 => 3,
            _ => 0
        };

        uint compressionMethod = 0;
        for (var i = 0; i < extraDwords; i++)
        {
            var dword = reader.ReadUInt32();
            if (i == 2)
            {
                compressionMethod = dword;
            }
        }

        // CompressionMethod 3 == LZ4 block (xEdit writes exactly that constant). Keying off the first
        // extra dword instead would be wrong: it is Unknown1, and it reads 1 in every retail archive
        // including all the version-2 ones.
        var compression = compressionMethod == 3
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
