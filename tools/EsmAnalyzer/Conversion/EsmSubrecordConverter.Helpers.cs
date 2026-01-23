using static EsmAnalyzer.Conversion.EsmEndianHelpers;

namespace EsmAnalyzer.Conversion;

internal static partial class EsmSubrecordConverter
{
    /// <summary>
    ///     Converts ENIT based on record type.
    /// </summary>
    private static void ConvertEnit(byte[] data, string recordType)
    {
        switch (recordType)
        {
            case "ENCH" when data.Length == 16:
                Swap4Bytes(data, 0);
                Swap4Bytes(data, 4);
                Swap4Bytes(data, 8);
                break;

            case "ALCH" when data.Length == 20:
                Swap4Bytes(data, 0);
                Swap4Bytes(data, 16);
                break;

            default:
                for (var i = 0; i < data.Length / 4; i++) Swap4Bytes(data, i * 4);
                break;
        }
    }

    /// <summary>
    ///     Converts IMAD (Image Space Adapter) subrecords.
    /// </summary>
    private static byte[] ConvertImadSubrecord(string signature, byte[] data)
    {
        // EDID is a string - no conversion needed
        if (signature == "EDID") return data;

        // Float array subrecords in IMAD
        if (signature is "DNAM" or "BNAM" or "VNAM" or "TNAM" or "NAM3" or "RNAM" or "SNAM" or "UNAM"
            or "NAM1" or "NAM2" or "WNAM" or "XNAM" or "YNAM" or "NAM4")
        {
            SwapAllFloats(data);
            return data;
        }

        // Keyed *IAD subrecords (time/value float pairs)
        if (signature.Length == 4 && signature[1] == 'I' && signature[2] == 'A' && signature[3] == 'D')
        {
            SwapAllFloats(data);
            return data;
        }

        // Unknown IMAD subrecord - try float array conversion
        if (data.Length >= 4 && data.Length % 4 == 0) SwapAllFloats(data);

        return data;
    }

    /// <summary>
    ///     Converts VTXT (vertex texture blend) entries.
    ///     Each entry is 8 bytes: uint16 + uint16 + float
    /// </summary>
    private static void ConvertVtxt(byte[] data)
    {
        const int entrySize = 8;
        var entryCount = data.Length / entrySize;

        for (var i = 0; i < entryCount; i++)
        {
            var offset = i * entrySize;
            Swap2Bytes(data, offset); // Position index
            // Bytes 2-3: flags (FF FF) - no swap
            Swap4Bytes(data, offset + 4); // Opacity float
        }
    }

    /// <summary>
    ///     Throws an exception for unknown subrecords so we can identify and fix them.
    /// </summary>
    private static void ConvertUnknownSubrecord(byte[] data, string signature, string recordType)
    {
        // Empty marker subrecords - no conversion needed
        if (data.Length == 0) return;

        // Single byte - no conversion needed
        if (data.Length == 1) return;

        // Exactly 2 bytes - likely uint16
        if (data.Length == 2)
        {
            Swap2Bytes(data, 0);
            return;
        }

        // Exactly 4 bytes - likely uint32/float/FormID
        if (data.Length == 4)
        {
            Swap4Bytes(data, 0);
            return;
        }

        // Unknown subrecord with complex structure - throw error so we can add explicit handler
        throw new NotSupportedException(
            $"Unknown subrecord '{signature}' ({data.Length} bytes) in record type '{recordType}'. " +
            $"Add explicit conversion handler in EsmSubrecordConverter.cs");
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

        Swap4Bytes(data, 0); // Flags
        var flags = BitConverter.ToUInt32(data, 0);
        Swap4Bytes(data, 4); // Navmesh FormID
        Swap4Bytes(data, 8); // Location FormID
        Swap2Bytes(data, 12); // Grid Y
        Swap2Bytes(data, 14); // Grid X
        Swap4Bytes(data, 16); // Approx X
        Swap4Bytes(data, 20); // Approx Y
        Swap4Bytes(data, 24); // Approx Z

        var offset = 28;
        var isIsland = (flags & 0x20) != 0; // Bit 5 = Is Island

        if (isIsland && data.Length > 32)
        {
            // Island data:
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
            for (var i = 0; i < vertexCount; i++)
            {
                Swap4Bytes(data, offset);
                offset += 4;
                Swap4Bytes(data, offset);
                offset += 4;
                Swap4Bytes(data, offset);
                offset += 4;
            }

            // Triangles (3 x uint16 each = 6 bytes)
            for (var i = 0; i < triangleCount; i++)
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
        // Structure: FormID Navmesh + 3 variable-length arrays of FormIDs
        // Each array is: uint32 count + FormID[] items
        // But the count is stored as -1 terminated (unknown count until end)
        // Looking at 24 bytes: 4 (FormID) + 4 (count) + 4 (count) + 4 (count) + 8 remaining
        // Actually it's likely: FormID + count1 + FormIDs... + count2 + FormIDs... + count3 + FormIDs...

        var offset = 0;
        // Navmesh FormID
        Swap4Bytes(data, offset);
        offset += 4;

        // For -1 terminated arrays, we need to parse the count first to know how many FormIDs
        // FNV uses wbArrayS with -1 which means unknown count, read until next element
        // But in binary, each array likely has a uint32 count prefix

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
            Swap2Bytes(data, i);
    }

    /// <summary>
    ///     Swaps all 4-byte values in the data.
    /// </summary>
    private static void SwapAllFloats(byte[] data)
    {
        var floatCount = data.Length / 4;
        for (var i = 0; i < floatCount; i++) Swap4Bytes(data, i * 4);
    }
}