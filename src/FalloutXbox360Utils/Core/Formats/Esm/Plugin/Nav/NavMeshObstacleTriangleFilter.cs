using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Nav;

/// <summary>
///     Drops runtime obstacle-split triangles from a captured navmesh before it is re-emitted as
///     a persistent ESM navmesh.
///
///     <para>
///     When the engine routes around a dynamic obstacle it splits the affected base triangles
///     into smaller sub-triangles and appends them to the live <c>BSNavMesh</c> triangle array,
///     leaving the originals in place. Those appended triangles are flagged differently — they
///     LACK the <c>0x800</c> "real navmesh triangle" bit that every base-mesh triangle carries —
///     and they overlap the originals geometrically. A runtime DMP capture serializes both, so
///     the re-emitted navmesh has coincident triangles; FNV's load-time validator then logs
///     <c>opposite normals but are linked</c> / <c>vertices do not match</c> and AVs in
///     <c>NavMeshSearchClosePoint</c> on cell entry (the Gomorrah01 crash). Obstacles are dynamic
///     and re-applied at runtime, so the persistent navmesh must contain only the base mesh.
///     </para>
///
///     <para>
///     This pass keeps only triangles with the <c>0x800</c> flag, updates <c>DATA.TriangleCount</c>,
///     and remaps the triangle indices that <c>NVCA</c> (cover triangles) and <c>NVDP</c> (door
///     portals, owning-triangle at +4) reference, dropping any that pointed at a removed triangle.
///     Vertices (NVVX) are left untouched — orphaned ones are harmless and dropping them would
///     force a full vertex-index remap. Triangle neighbour links are NOT remapped here: the
///     downstream <see cref="NavMeshAdjacencyRebuild" /> regenerates them from geometry on the
///     filtered index space.
///     </para>
/// </summary>
internal static class NavMeshObstacleTriangleFilter
{
    private const int NvtrEntrySize = 16;
    private const int NvtrFlagsOffset = 12;
    private const uint RealTriangleFlag = 0x800u;

    private const int DataTriangleCountOffset = 8;
    private const int DataDoorLinkCountOffset = 16;

    private const int NvcaEntrySize = 2;
    private const int NvdpEntrySize = 8;
    private const int NvdpOwningTriOffset = 4;
    private const int NvexEntrySize = 10;
    private const int NvexTriangleOffset = 8;

    /// <summary>
    ///     Returns a filtered copy of <paramref name="subrecords" /> with non-<c>0x800</c>
    ///     triangles removed, or the original list when there is nothing to remove (or no usable
    ///     NVTR). The input list and its byte arrays are not mutated.
    /// </summary>
    public static IReadOnlyList<NavMeshSubrecord> Filter(IReadOnlyList<NavMeshSubrecord> subrecords)
    {
        byte[]? nvtr = null;
        foreach (var sub in subrecords)
        {
            if (sub.Signature == "NVTR")
            {
                nvtr = sub.Bytes;
                break;
            }
        }

        if (nvtr is null || nvtr.Length == 0 || nvtr.Length % NvtrEntrySize != 0)
        {
            return subrecords;
        }

        var triangleCount = nvtr.Length / NvtrEntrySize;
        var remap = new int[triangleCount];
        var kept = 0;
        for (var t = 0; t < triangleCount; t++)
        {
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(nvtr.AsSpan(t * NvtrEntrySize + NvtrFlagsOffset, 4));
            remap[t] = (flags & RealTriangleFlag) != 0 ? kept++ : -1;
        }

        if (kept == triangleCount)
        {
            return subrecords; // No runtime obstacle-split triangles present.
        }

        var newNvtr = new byte[kept * NvtrEntrySize];
        var w = 0;
        for (var t = 0; t < triangleCount; t++)
        {
            if (remap[t] < 0)
            {
                continue;
            }

            Array.Copy(nvtr, t * NvtrEntrySize, newNvtr, w * NvtrEntrySize, NvtrEntrySize);
            w++;
        }

        var result = new List<NavMeshSubrecord>(subrecords.Count);
        foreach (var sub in subrecords)
        {
            switch (sub.Signature)
            {
                case "NVTR":
                    result.Add(new NavMeshSubrecord("NVTR", newNvtr));
                    break;
                case "DATA":
                    result.Add(new NavMeshSubrecord("DATA", PatchData(sub.Bytes, kept)));
                    break;
                case "NVCA":
                    result.Add(new NavMeshSubrecord("NVCA", RemapNvca(sub.Bytes, remap)));
                    break;
                case "NVDP":
                    result.Add(new NavMeshSubrecord("NVDP", RemapNvdp(sub.Bytes, remap, out var keptDoorLinks)));
                    // Keep DATA.DoorLinkCount consistent with the surviving NVDP entries.
                    PatchDoorLinkCountInList(result, keptDoorLinks);
                    break;
                case "NVEX":
                    // NVEX (10-byte entries) carries a per-link triangle index at +8; remap it and
                    // drop links anchored to a removed triangle. The downstream FormID rewrite /
                    // NVEX sanitizer still run on the result.
                    result.Add(new NavMeshSubrecord("NVEX", RemapNvex(sub.Bytes, remap)));
                    break;
                default:
                    result.Add(sub);
                    break;
            }
        }

        return result;
    }

    private static byte[] PatchData(byte[] data, int triangleCount)
    {
        var copy = (byte[])data.Clone();
        if (copy.Length >= DataTriangleCountOffset + 4)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(DataTriangleCountOffset, 4), (uint)triangleCount);
        }

        return copy;
    }

    private static void PatchDoorLinkCountInList(List<NavMeshSubrecord> result, int doorLinkCount)
    {
        for (var i = 0; i < result.Count; i++)
        {
            if (result[i].Signature != "DATA")
            {
                continue;
            }

            var copy = (byte[])result[i].Bytes.Clone();
            if (copy.Length >= DataDoorLinkCountOffset + 4)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(DataDoorLinkCountOffset, 4), (uint)doorLinkCount);
                result[i] = new NavMeshSubrecord("DATA", copy);
            }

            return;
        }
    }

    private static byte[] RemapNvca(byte[] nvca, int[] remap)
    {
        if (nvca.Length < NvcaEntrySize)
        {
            return nvca;
        }

        var keptIndices = new List<ushort>(nvca.Length / NvcaEntrySize);
        for (var off = 0; off + NvcaEntrySize <= nvca.Length; off += NvcaEntrySize)
        {
            var tri = BinaryPrimitives.ReadUInt16LittleEndian(nvca.AsSpan(off, NvcaEntrySize));
            if (tri < remap.Length && remap[tri] >= 0)
            {
                keptIndices.Add((ushort)remap[tri]);
            }
        }

        var result = new byte[keptIndices.Count * NvcaEntrySize];
        for (var i = 0; i < keptIndices.Count; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(i * NvcaEntrySize, NvcaEntrySize), keptIndices[i]);
        }

        return result;
    }

    private static byte[] RemapNvex(byte[] nvex, int[] remap)
    {
        if (nvex.Length < NvexEntrySize)
        {
            return nvex;
        }

        var keptBytes = new List<byte>(nvex.Length);
        for (var off = 0; off + NvexEntrySize <= nvex.Length; off += NvexEntrySize)
        {
            var tri = BinaryPrimitives.ReadUInt16LittleEndian(nvex.AsSpan(off + NvexTriangleOffset, 2));
            if (tri >= remap.Length || remap[tri] < 0)
            {
                continue; // Edge link anchored to a removed triangle — drop it.
            }

            var entry = nvex.AsSpan(off, NvexEntrySize).ToArray();
            BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(NvexTriangleOffset, 2), (ushort)remap[tri]);
            keptBytes.AddRange(entry);
        }

        return keptBytes.ToArray();
    }

    private static byte[] RemapNvdp(byte[] nvdp, int[] remap, out int keptEntries)
    {
        keptEntries = 0;
        if (nvdp.Length < NvdpEntrySize)
        {
            keptEntries = nvdp.Length / NvdpEntrySize;
            return nvdp;
        }

        var keptBytes = new List<byte>(nvdp.Length);
        for (var off = 0; off + NvdpEntrySize <= nvdp.Length; off += NvdpEntrySize)
        {
            var owningTri = BinaryPrimitives.ReadUInt16LittleEndian(nvdp.AsSpan(off + NvdpOwningTriOffset, 2));
            if (owningTri >= remap.Length || remap[owningTri] < 0)
            {
                continue; // Door portal anchored to a removed triangle — drop it.
            }

            var entry = nvdp.AsSpan(off, NvdpEntrySize).ToArray();
            BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(NvdpOwningTriOffset, 2), (ushort)remap[owningTri]);
            keptBytes.AddRange(entry);
            keptEntries++;
        }

        return keptBytes.ToArray();
    }
}
