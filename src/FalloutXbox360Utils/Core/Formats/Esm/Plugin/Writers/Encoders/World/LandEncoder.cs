using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Terrain;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;

/// <summary>
///     Encodes the LAND record subrecords for one exterior cell from the parsed
///     <see cref="LandHeightmap" />.
///     LAND records in FNV plugins carry the per-cell heightmap, vertex normals, and
///     (optionally) vertex colors / texture layers. They nest inside the cell's Temporary
///     Children GRUP (type 9), immediately before any REFR/ACHR records, with the LAND
///     record's own FormID allocated independently from the cell's FormID.
///     Emits terrain height plus visual subrecords when available:
///     <list type="bullet">
///         <item>
///             <description><c>DATA</c> — 4 bytes (flag byte + 3 padding). Always 0/zero for our output.</description>
///         </item>
///         <item>
///             <description>
///                 <c>VNML</c> — 3267 bytes (1089 x 3 sbyte). Normals generated from the reconstructed height
///                 grid.
///             </description>
///         </item>
///         <item>
///             <description><c>VHGT</c> — 1096 bytes (float offset + 1089 sbyte deltas + 3 padding).</description>
///         </item>
///         <item>
///             <description><c>VCLR</c> — optional 3267 bytes (1089 x RGB).</description>
///         </item>
///         <item>
///             <description><c>BTXT/ATXT/VTXT</c> — optional ordered texture layers.</description>
///         </item>
///         <item>
///             <description><c>VTEX</c> — optional texture index/FormID list.</description>
///         </item>
///     </list>
/// </summary>
public static class LandEncoder
{
    private const int LandGridSize = TerrainConstants.LandGridSize;
    private const int VertexCount = TerrainConstants.LandVertexCount;
    private const int VhgtSize = 1096; // 4-byte offset + 1089 deltas + 3 padding
    private const int VnmlSize = VertexCount * 3; // 3267 bytes

    /// <summary>
    ///     Encode the LAND subrecord list for the given heightmap. Returns null if the
    ///     heightmap is invalid (wrong delta count) — caller should skip LAND emission for
    ///     that cell.
    /// </summary>
    public static IReadOnlyList<EncodedSubrecord>? Encode(LandHeightmap heightmap, LandVisualData? visualData = null)
    {
        if (heightmap.HeightDeltas is null || heightmap.HeightDeltas.Length != VertexCount)
        {
            return null;
        }

        var subs = new List<EncodedSubrecord>(3 + (visualData?.TextureLayers.Count ?? 0) * 2);

        // DATA — 4 bytes per FNV LAND schema. Bits 0-3 are "Quad N has Data" flags (one per
        // quadrant); bit 4 (0x10) is set on every vanilla exterior LAND we've inspected and
        // appears to be the engine's "this cell has terrain" master flag. Emitting DATA=0
        // (the prior behavior) makes the engine treat the LAND as having no per-quadrant
        // texture/height data and the terrain renders flat. Compute the quadrant bits from
        // the BTXTs we'll emit; OR in 0x10 for canonical exterior shape.
        var dataFlags = (byte)0x10;
        if (visualData?.TextureLayers is { Count: > 0 } layersForDataFlags)
        {
            foreach (var layer in layersForDataFlags)
            {
                if (layer.Kind == LandTextureLayerKind.Base
                    && layer.TextureFormId != 0
                    && layer.Quadrant <= 3)
                {
                    dataFlags |= (byte)(1 << layer.Quadrant);
                }
            }
        }
        subs.Add(new EncodedSubrecord("DATA", new byte[] { dataFlags, 0, 0, 0 }));

        // VNML — prefer the runtime mesh's captured normals when surfaced on visualData;
        // otherwise compute from the reconstructed height grid. Runtime VNML wins because it
        // reflects the engine's live shading data, which can diverge from height-derived
        // gradients on cells where the runtime mesh was tessellated differently from a
        // canonical 33×33 grid.
        var vnml = visualData?.VertexNormals is { Length: VnmlSize } runtimeVnml
            ? (byte[])runtimeVnml.Clone()
            : BuildVnml(heightmap.CalculateHeights());
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

        if (visualData?.VertexColors is { Length: VnmlSize } vclr)
        {
            subs.Add(new EncodedSubrecord("VCLR", (byte[])vclr.Clone()));
        }

        if (visualData?.TextureLayers is { Count: > 0 } textureLayers)
        {
            foreach (var layer in textureLayers)
            {
                if (layer.TextureFormId == 0)
                {
                    continue;
                }

                subs.Add(new EncodedSubrecord(layer.SubrecordSignature, EncodeTextureLayer(layer)));

                if (layer.Kind == LandTextureLayerKind.Alpha && layer.BlendEntries.Count > 0)
                {
                    subs.Add(new EncodedSubrecord("VTXT", EncodeTextureBlendEntries(layer.BlendEntries)));
                }
            }
        }

        if (visualData?.TextureIndices is { Length: > 0 } vtex)
        {
            var bytes = new byte[vtex.Length * 4];
            for (var i = 0; i < vtex.Length; i++)
            {
                SubrecordEncoder.WriteUInt32(bytes, i * 4, vtex[i]);
            }

            subs.Add(new EncodedSubrecord("VTEX", bytes));
        }

        return subs;
    }

    private static byte[] EncodeTextureLayer(LandTextureLayer layer)
    {
        var bytes = new byte[8];
        SubrecordEncoder.WriteFormId(bytes, 0, layer.TextureFormId);
        bytes[4] = layer.Quadrant;
        bytes[5] = layer.PlatformFlag;
        // BTXT layer is the base-layer sentinel 0xFFFF in vanilla FNV LANDs; ATXT carries a
        // real layer index. Emitting Layer=0 on BTXTs (the prior behavior) confused the
        // engine into treating BTXT as an extra alpha layer with no base, which caused the
        // quadrant's terrain to render with no textured base — observed as the "flat
        // Goodsprings" symptom along with the DATA=0 bug.
        var layerIndex = layer.Kind == LandTextureLayerKind.Base
            ? (ushort)0xFFFF
            : layer.Layer;
        SubrecordEncoder.WriteUInt16(bytes, 6, layerIndex);
        return bytes;
    }

    private static byte[] EncodeTextureBlendEntries(List<LandTextureBlendEntry> entries)
    {
        var bytes = new byte[entries.Count * 8];
        for (var i = 0; i < entries.Count; i++)
        {
            var offset = i * 8;
            var entry = entries[i];
            SubrecordEncoder.WriteUInt16(bytes, offset, entry.Position);
            bytes[offset + 2] = entry.Unused0;
            bytes[offset + 3] = entry.Unused1;
            SubrecordEncoder.WriteFloat(bytes, offset + 4, entry.Opacity);
        }

        return bytes;
    }

    private static byte[] BuildVnml(float[,] heights)
    {
        const float horizontalScale = 128f;
        var vnml = new byte[VnmlSize];

        for (var y = 0; y < LandGridSize; y++)
        {
            for (var x = 0; x < LandGridSize; x++)
            {
                var left = heights[y, Math.Max(0, x - 1)];
                var right = heights[y, Math.Min(LandGridSize - 1, x + 1)];
                var down = heights[Math.Max(0, y - 1), x];
                var up = heights[Math.Min(LandGridSize - 1, y + 1), x];

                var xSpan = x is 0 or LandGridSize - 1 ? horizontalScale : horizontalScale * 2f;
                var ySpan = y is 0 or LandGridSize - 1 ? horizontalScale : horizontalScale * 2f;
                var dzdx = (right - left) / xSpan;
                var dzdy = (up - down) / ySpan;

                var nx = -dzdx;
                var ny = -dzdy;
                var nz = 1f;
                var length = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                if (length <= 0.0001f)
                {
                    nx = 0f;
                    ny = 0f;
                    nz = 1f;
                    length = 1f;
                }

                var index = (y * LandGridSize + x) * 3;
                vnml[index + 0] = ToNormalByte(nx / length);
                vnml[index + 1] = ToNormalByte(ny / length);
                vnml[index + 2] = ToNormalByte(nz / length);
            }
        }

        return vnml;
    }

    private static byte ToNormalByte(float value)
    {
        var scaled = Math.Clamp((int)MathF.Round(value * 127f), sbyte.MinValue, sbyte.MaxValue);
        return unchecked((byte)(sbyte)scaled);
    }
}
