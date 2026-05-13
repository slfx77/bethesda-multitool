using FalloutXbox360Utils.Core.Formats.Esm.Models.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;

/// <summary>
///     Encodes the LAND record subrecords for one exterior cell from the parsed
///     <see cref="LandHeightmap"/>.
///
///     LAND records in FNV plugins carry the per-cell heightmap, vertex normals, and
///     (optionally) vertex colors / texture layers. They nest inside the cell's Temporary
///     Children GRUP (type 9), immediately before any REFR/ACHR records, with the LAND
///     record's own FormID allocated independently from the cell's FormID.
///
///     v1 emits the minimum-viable terrain so cells render with their captured elevation:
///     <list type="bullet">
///         <item><description><c>DATA</c> — 4 bytes (flag byte + 3 padding). Always 0/zero for our output.</description></item>
///         <item><description><c>VNML</c> — 3267 bytes (1089 × 3 sbyte). Default flat-up normals (0, 0, 127) so terrain shades acceptably without computed normals.</description></item>
///         <item><description><c>VHGT</c> — 1096 bytes (float offset + 1089 sbyte deltas + 3 padding).</description></item>
///     </list>
///     Vertex colors (VCLR), base/additional textures (BTXT/ATXT/VTXT), and texture FormID
///     list (VTEX) are omitted — the engine falls back to the worldspace default land texture.
/// </summary>
public static class LandEncoder
{
    private const int LandGridSize = 33;
    private const int VertexCount = LandGridSize * LandGridSize; // 1089
    private const int VhgtSize = 1096; // 4-byte offset + 1089 deltas + 3 padding
    private const int VnmlSize = VertexCount * 3; // 3267 bytes

    /// <summary>
    ///     Encode the LAND subrecord list for the given heightmap. Returns null if the
    ///     heightmap is invalid (wrong delta count) — caller should skip LAND emission for
    ///     that cell.
    /// </summary>
    public static IReadOnlyList<EncodedSubrecord>? Encode(LandHeightmap heightmap)
    {
        if (heightmap.HeightDeltas is null || heightmap.HeightDeltas.Length != VertexCount)
        {
            return null;
        }

        var subs = new List<EncodedSubrecord>(3);

        // DATA — 4 bytes. Flag byte 0 + 3 padding. Most vanilla cells have DATA=0.
        subs.Add(new EncodedSubrecord("DATA", new byte[4]));

        // VNML — default flat normals. Each vertex normal is (0, 0, 127) → upward-facing,
        // unit length when interpreted as sbyte/127. Terrain shades like a flat plane,
        // good enough for "is the ground there at all" rendering.
        var vnml = new byte[VnmlSize];
        for (var i = 0; i < VertexCount; i++)
        {
            vnml[i * 3 + 0] = 0;   // X
            vnml[i * 3 + 1] = 0;   // Y
            vnml[i * 3 + 2] = 127; // Z (up, max sbyte)
        }
        subs.Add(new EncodedSubrecord("VNML", vnml));

        // VHGT — float HeightOffset + 1089 sbyte deltas + 3 padding bytes.
        var vhgt = new byte[VhgtSize];
        SubrecordEncoder.WriteFloat(vhgt, 0, heightmap.HeightOffset);
        for (var i = 0; i < VertexCount; i++)
        {
            vhgt[4 + i] = unchecked((byte)heightmap.HeightDeltas[i]);
        }
        // Bytes [4+1089 .. 4+1089+3] stay zero — VHGT padding.
        subs.Add(new EncodedSubrecord("VHGT", vhgt));

        return subs;
    }
}
