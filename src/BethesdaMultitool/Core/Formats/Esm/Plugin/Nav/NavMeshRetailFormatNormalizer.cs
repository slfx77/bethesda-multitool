using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Nav;

/// <summary>
///     Upgrades a DMP-captured (prototype-format) NAVM subrecord list to the retail FNV
///     on-disk format. The July-2010 prototype serializes navmeshes with a different shape
///     than shipped FNV, and the retail engine's NAVM loader is strictly sequential — any
///     deviation fails the whole cell's temp-children stream, which silently skips the
///     cell's InitItem (decompile-verified) and leaves every ref in the cell with raw
///     FormIDs in its extra-data pointer slots (the Gomorrah attach AV family).
///     Deltas normalized here, all FNVEdit-verified against retail records:
///     <list type="bullet">
///         <item>
///             <description>
///                 <b>Order:</b> proto emits DATA before NVER; retail is
///                 EDID?, NVER, DATA, NVVX, NVTR, NVCA?, NVDP?, NVGD, NVEX?.
///             </description>
///         </item>
///         <item>
///             <description><b>NVER:</b> proto value 14; retail navmesh version is 11.</description>
///         </item>
///         <item>
///             <description>
///                 <b>DATA:</b> proto is 20 bytes; retail is 24 (Cell, Vertex
///                 Count, Triangle Count, Edge Link Count, Cover Triangle Count, Door Link
///                 Count). All five counts are re-derived from the actual sibling subrecord
///                 sizes rather than trusting proto field positions.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <b>NVGD:</b> proto captures lack the navmesh grid; a valid
///                 divisor-1 grid (single cell listing every triangle) is synthesized from the
///                 NVVX bounds. Functionally correct — grid queries just scan all triangles.
///             </description>
///         </item>
///     </list>
/// </summary>
internal static class NavMeshRetailFormatNormalizer
{
    private const uint RetailNavmeshVersion = 11;
    private const int VertexSize = 12;
    private const int TriangleSize = 16;
    private const int NvexEntrySize = 10;
    private const int NvcaEntrySize = 2;
    private const int NvdpEntrySize = 8;

    private static readonly string[] CanonicalOrder =
        ["EDID", "NVER", "DATA", "NVVX", "NVTR", "NVCA", "NVDP", "NVGD", "NVEX"];

    public static List<EncodedSubrecord> Normalize(IReadOnlyList<EncodedSubrecord> subrecords)
    {
        var bySig = new Dictionary<string, EncodedSubrecord>(StringComparer.Ordinal);
        var unknown = new List<EncodedSubrecord>();
        foreach (var sub in subrecords)
        {
            // Last-writer-wins on duplicates; proto captures don't repeat signatures.
            if (Array.IndexOf(CanonicalOrder, sub.Signature) >= 0)
            {
                bySig[sub.Signature] = sub;
            }
            else
            {
                unknown.Add(sub);
            }
        }

        var nvvx = bySig.TryGetValue("NVVX", out var v) ? v.Bytes : [];
        var nvtr = bySig.TryGetValue("NVTR", out var t) ? t.Bytes : [];

        // NVER: force the retail version value.
        var nver = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(nver, RetailNavmeshVersion);
        bySig["NVER"] = new EncodedSubrecord("NVER", nver);

        // DATA: rebuild as the 24-byte retail struct with counts derived from siblings.
        var cellFormId = 0u;
        if (bySig.TryGetValue("DATA", out var oldData) && oldData.Bytes.Length >= 4)
        {
            cellFormId = BinaryPrimitives.ReadUInt32LittleEndian(oldData.Bytes.AsSpan(0, 4));
        }

        var data = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), cellFormId);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4, 4), (uint)(nvvx.Length / VertexSize));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8, 4), (uint)(nvtr.Length / TriangleSize));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12, 4),
            (uint)((bySig.TryGetValue("NVEX", out var e) ? e.Bytes.Length : 0) / NvexEntrySize));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16, 4),
            (uint)((bySig.TryGetValue("NVCA", out var c) ? c.Bytes.Length : 0) / NvcaEntrySize));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20, 4),
            (uint)((bySig.TryGetValue("NVDP", out var d) ? d.Bytes.Length : 0) / NvdpEntrySize));
        bySig["DATA"] = new EncodedSubrecord("DATA", data);

        if (!bySig.ContainsKey("NVGD") && nvvx.Length >= VertexSize && nvtr.Length >= TriangleSize)
        {
            bySig["NVGD"] = new EncodedSubrecord("NVGD", BuildDivisorOneGrid(nvvx, nvtr));
        }

        var result = new List<EncodedSubrecord>(bySig.Count + unknown.Count);
        foreach (var sig in CanonicalOrder)
        {
            if (bySig.TryGetValue(sig, out var sub))
            {
                result.Add(sub);
            }
        }

        result.AddRange(unknown);
        return result;
    }

    /// <summary>
    ///     Retail NVGD layout: u32 Divisor, float MaxXDistance, float MaxYDistance,
    ///     bounds min/max vec3, then Divisor² grid cells each encoded as a u16 count
    ///     followed by that many u16 triangle indices. A divisor-1 grid holds every
    ///     triangle in its single cell.
    /// </summary>
    private static byte[] BuildDivisorOneGrid(byte[] nvvx, byte[] nvtr)
    {
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        for (var i = 0; i + VertexSize <= nvvx.Length; i += VertexSize)
        {
            var x = BinaryPrimitives.ReadSingleLittleEndian(nvvx.AsSpan(i, 4));
            var y = BinaryPrimitives.ReadSingleLittleEndian(nvvx.AsSpan(i + 4, 4));
            var z = BinaryPrimitives.ReadSingleLittleEndian(nvvx.AsSpan(i + 8, 4));
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            minZ = Math.Min(minZ, z);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
            maxZ = Math.Max(maxZ, z);
        }

        var triangleCount = Math.Min(nvtr.Length / TriangleSize, ushort.MaxValue);
        var bytes = new byte[4 + 4 + 4 + 24 + 2 + triangleCount * 2];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span[..4], 1);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(4, 4), maxX - minX);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(8, 4), maxY - minY);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(12, 4), minX);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(16, 4), minY);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(20, 4), minZ);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(24, 4), maxX);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(28, 4), maxY);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(32, 4), maxZ);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(36, 2), (ushort)triangleCount);
        for (var i = 0; i < triangleCount; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(38 + i * 2, 2), (ushort)i);
        }

        return bytes;
    }
}
