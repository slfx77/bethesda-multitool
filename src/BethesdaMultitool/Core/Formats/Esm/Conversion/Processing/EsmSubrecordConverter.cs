using BethesdaMultitool.Core.Formats.Esm.Conversion.Schema;
using static BethesdaMultitool.Core.Formats.Esm.Conversion.EsmEndianHelpers;

namespace BethesdaMultitool.Core.Formats.Esm.Conversion.Processing;

/// <summary>
///     Structural scope for the ambiguous four-byte PERK DATA payload.
/// </summary>
internal enum PerkDataScope
{
    /// <summary>
    ///     No record-chain scope is available. For backward compatibility, four-byte PERK DATA
    ///     is treated as an ability-entry FormID and endian-swapped.
    /// </summary>
    Unspecified,

    /// <summary>The top-level four UInt8 fields on the PERK record.</summary>
    TopLevel,

    /// <summary>A type-specific DATA payload between PRKE and PRKF.</summary>
    Entry
}

/// <summary>
///     Converts subrecord data based on type and parent record.
///     Handles endian conversion for all known subrecord formats.
///     Uses schema-driven conversion.
/// </summary>
internal static class EsmSubrecordConverter
{
    /// <summary>
    ///     Converts subrecord data based on type. Callers processing a complete PERK record
    ///     should pass the record-chain-derived <paramref name="perkDataScope" />. The default
    ///     preserves the historical stateless behavior: ambiguous PERK DATA(4) is assumed to be
    ///     an ability-entry FormID and is endian-swapped.
    /// </summary>
    public static byte[] ConvertSubrecordData(
        string signature,
        ReadOnlySpan<byte> data,
        string recordType,
        PerkDataScope perkDataScope = PerkDataScope.Unspecified)
    {
        // DATA(4) has two unrelated PERK layouts. Outside a PRKE..PRKF entry chain it is four
        // independent UInt8 fields and must be copied byte-for-byte. Within an entry it is an
        // ability FormID and follows the existing schema conversion.
        if (recordType == "PERK" && signature == "DATA" && data.Length == 4 &&
            perkDataScope == PerkDataScope.TopLevel)
        {
            return data.ToArray();
        }

        var schemaResult = SubrecordSchemaProcessor.ConvertWithSchema(signature, data, recordType);
        return schemaResult ?? throw new NotSupportedException(
            $"No schema for subrecord '{signature}' ({data.Length} bytes) in record type '{recordType}'.");
    }

    /// <summary>
    ///     Converts NVMI (Navmesh Info) subrecord - variable length with optional island data.
    /// </summary>
    internal static void ConvertNvmi(byte[] data)
    {
        // Base structure (32 bytes minimum):
        // 0-3: Flags (uint32)
        // 4-7: Navmesh FormID
        // 8-11: Location FormID
        // 12-13: Grid Y (int16)
        // 14-15: Grid X (int16)
        // 16-27: Approx Location (Vec3, 3 floats)
        // Then island data (variable) if flag bit 5 set
        // Last 4 bytes: Preferred % (float)

        if (data.Length < 32)
        {
            // Shorter than the 28-byte base + trailing float. Endian converters never throw
            // (the write path has no catch), so pass malformed input through unmodified.
            return;
        }

        Swap4Bytes(data, 0); // Flags
        var flags = BitConverter.ToUInt32(data, 0);
        Swap4Bytes(data, 4); // Navmesh FormID
        Swap4Bytes(data, 8); // Location FormID
        Swap4Bytes(data, 12); // Grid Y (int16) + Grid X (int16) — packed as iCellKey (uint32) per PDB
        Swap4Bytes(data, 16); // Approx X
        Swap4Bytes(data, 20); // Approx Y
        Swap4Bytes(data, 24); // Approx Z

        var offset = 28;
        var isIsland = (flags & 0x20) != 0; // Bit 5 = Is Island

        // Island reads must stop before the trailing Preferred % float (last 4 bytes).
        // Counts and lengths are file-controlled: on truncation, stop converting and leave
        // the remaining bytes as-is (partial-swap degrade) rather than reading out of range.
        var limit = data.Length - 4;

        if (isIsland && offset + 28 <= limit)
        {
            // Island data (bounds Vec3 pair + two counts = 28 bytes minimum):
            // NavmeshBounds Min Vec3 (12)
            Swap4Bytes(data, offset);
            offset += 4;
            Swap4Bytes(data, offset);
            offset += 4;
            Swap4Bytes(data, offset);
            offset += 4;
            // NavmeshBounds Max Vec3 (12)
            Swap4Bytes(data, offset);
            offset += 4;
            Swap4Bytes(data, offset);
            offset += 4;
            Swap4Bytes(data, offset);
            offset += 4;
            // Vertex Count (uint16)
            Swap2Bytes(data, offset);
            var vertexCount = BitConverter.ToUInt16(data, offset);
            offset += 2;
            // Triangle Count (uint16)
            Swap2Bytes(data, offset);
            var triangleCount = BitConverter.ToUInt16(data, offset);
            offset += 2;
            // Vertices (Vec3 each = 12 bytes)
            for (var i = 0; i < vertexCount && offset + 12 <= limit; i++)
            {
                Swap4Bytes(data, offset);
                offset += 4;
                Swap4Bytes(data, offset);
                offset += 4;
                Swap4Bytes(data, offset);
                offset += 4;
            }

            // Triangles (3 x uint16 each = 6 bytes)
            for (var i = 0; i < triangleCount && offset + 6 <= limit; i++)
            {
                Swap2Bytes(data, offset);
                offset += 2;
                Swap2Bytes(data, offset);
                offset += 2;
                Swap2Bytes(data, offset);
                offset += 2;
            }
        }

        // Last 4 bytes: Preferred % (float)
        Swap4Bytes(data, data.Length - 4);
    }

    /// <summary>
    ///     Converts NVCI (Navmesh Connection Info) subrecord - variable length arrays of FormIDs.
    /// </summary>
    internal static void ConvertNvci(byte[] data)
    {
        // Layout: FormID (navmesh) + 3 count-prefixed arrays of FormIDs
        // Each array: uint32 count, then count × FormID entries

        var offset = 0;
        // Navmesh FormID
        Swap4Bytes(data, offset);
        offset += 4;

        // Standard array
        if (offset + 4 <= data.Length)
        {
            Swap4Bytes(data, offset);
            var standardCount = BitConverter.ToInt32(data, offset);
            offset += 4;
            for (var i = 0; i < standardCount && offset + 4 <= data.Length; i++)
            {
                Swap4Bytes(data, offset);
                offset += 4;
            }
        }

        // Preferred array
        if (offset + 4 <= data.Length)
        {
            Swap4Bytes(data, offset);
            var preferredCount = BitConverter.ToInt32(data, offset);
            offset += 4;
            for (var i = 0; i < preferredCount && offset + 4 <= data.Length; i++)
            {
                Swap4Bytes(data, offset);
                offset += 4;
            }
        }

        // Door Links array
        if (offset + 4 <= data.Length)
        {
            Swap4Bytes(data, offset);
            var doorLinksCount = BitConverter.ToInt32(data, offset);
            offset += 4;
            for (var i = 0; i < doorLinksCount && offset + 4 <= data.Length; i++)
            {
                Swap4Bytes(data, offset);
                offset += 4;
            }
        }
    }

    /// <summary>
    ///     Converts NVGD (Navmesh Grid) subrecord - variable length with cells array.
    /// </summary>
    internal static void ConvertNvgd(byte[] data)
    {
        // Base structure:
        // 0-3: Divisor (uint32)
        // 4-7: Max X Distance (float)
        // 8-11: Max Y Distance (float)
        // 12-23: Bounds Min (Vec3)
        // 24-35: Bounds Max (Vec3)
        // 36+: Variable cells array (each cell is -2 terminated uint16 array)

        Swap4Bytes(data, 0); // Divisor
        Swap4Bytes(data, 4); // Max X Distance
        Swap4Bytes(data, 8); // Max Y Distance
        // Bounds Min
        Swap4Bytes(data, 12);
        Swap4Bytes(data, 16);
        Swap4Bytes(data, 20);
        // Bounds Max
        Swap4Bytes(data, 24);
        Swap4Bytes(data, 28);
        Swap4Bytes(data, 32);

        // Cells array - all remaining data is uint16 values
        for (var i = 36; i + 2 <= data.Length; i += 2)
        {
            Swap2Bytes(data, i);
        }
    }
}
