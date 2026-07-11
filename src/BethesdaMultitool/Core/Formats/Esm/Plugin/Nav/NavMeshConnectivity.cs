using System.Buffers.Binary;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Nav;

/// <summary>
///     A navmesh's cross-navmesh connectivity, extracted from its emitted NVEX (edge links to other
///     navmeshes) and NVDP (door portals). Used to reconstruct the NAVI NVCI subrecord so the
///     NavMeshInfoMap graph the engine's cross-cell A* walks agrees with the navmesh's own links.
/// </summary>
/// <param name="StandardNeighbors">Distinct NVEX target NAVM FormIDs → NVCI Standard array.</param>
/// <param name="DoorRefs">Distinct NVDP door REFR FormIDs → NVCI Door-Links array.</param>
internal readonly record struct NavmConnectivity(
    IReadOnlyList<uint> StandardNeighbors,
    IReadOnlyList<uint> DoorRefs);

/// <summary>
///     Parses NVEX / NVDP connectivity out of an already-emitted NAVM record byte array.
/// </summary>
internal static class NavMeshConnectivity
{
    private const int RecordHeaderSize = 24;
    private const int NvexEntrySize = 10;
    private const int NvexTargetOffset = 4; // NVEX entry: Type(u32) Navmesh(FormId @4) Triangle(u16)
    private const int NvdpEntrySize = 8;
    private const int NvdpDoorRefOffset = 0; // NVDP entry: DoorRef(FormId @0) Triangle(u16) pad(2)

    /// <summary>
    ///     Extracts the NAVM's FormID and its distinct NVEX targets + NVDP door refs. Returns false
    ///     when the bytes aren't a NAVM record. Order of neighbors/doors is preserved (first seen).
    /// </summary>
    public static bool TryExtract(byte[] navmRecordBytes, out uint navmFormId, out NavmConnectivity connectivity)
    {
        navmFormId = 0;
        connectivity = new NavmConnectivity([], []);
        if (navmRecordBytes.Length < RecordHeaderSize
            || navmRecordBytes[0] != (byte)'N' || navmRecordBytes[1] != (byte)'A'
            || navmRecordBytes[2] != (byte)'V' || navmRecordBytes[3] != (byte)'M')
        {
            return false;
        }

        var bodySize = BinaryPrimitives.ReadUInt32LittleEndian(navmRecordBytes.AsSpan(4, 4));
        if ((long)RecordHeaderSize + bodySize > navmRecordBytes.Length)
        {
            return false;
        }

        navmFormId = BinaryPrimitives.ReadUInt32LittleEndian(navmRecordBytes.AsSpan(12, 4));

        var body = navmRecordBytes.AsSpan(RecordHeaderSize, (int)bodySize);
        var standard = new List<uint>();
        var standardSeen = new HashSet<uint>();
        var doors = new List<uint>();
        var doorsSeen = new HashSet<uint>();

        var j = 0;
        var pendingLargeSize = -1;
        while (j + 6 <= body.Length)
        {
            var sig = System.Text.Encoding.ASCII.GetString(body.Slice(j, 4));
            int subSize = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(j + 4, 2));
            if (sig == "XXXX")
            {
                pendingLargeSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(j + 6, subSize));
                j += 6 + subSize;
                continue;
            }
            if (pendingLargeSize >= 0)
            {
                subSize = pendingLargeSize;
                pendingLargeSize = -1;
            }
            if (j + 6 + subSize > body.Length)
            {
                break;
            }

            var payload = body.Slice(j + 6, subSize);
            if (sig == "NVEX")
            {
                for (var k = 0; k + NvexEntrySize <= payload.Length; k += NvexEntrySize)
                {
                    var target = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(k + NvexTargetOffset, 4));
                    if (target != 0 && standardSeen.Add(target))
                    {
                        standard.Add(target);
                    }
                }
            }
            else if (sig == "NVDP")
            {
                for (var k = 0; k + NvdpEntrySize <= payload.Length; k += NvdpEntrySize)
                {
                    var doorRef = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(k + NvdpDoorRefOffset, 4));
                    if (doorRef != 0 && doorsSeen.Add(doorRef))
                    {
                        doors.Add(doorRef);
                    }
                }
            }

            j += 6 + subSize;
        }

        connectivity = new NavmConnectivity(standard, doors);
        return true;
    }
}
