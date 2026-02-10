using static FalloutXbox360Utils.Core.Formats.Esm.Conversion.EsmEndianHelpers;

namespace FalloutXbox360Utils.Core.Formats.Esm.Conversion;

internal static partial class EsmSubrecordConverter
{
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
        Swap4Bytes(data, 12); // Grid Y (int16) + Grid X (int16) — packed as iCellKey (uint32) per PDB
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
        {
            Swap2Bytes(data, i);
        }
    }
}
