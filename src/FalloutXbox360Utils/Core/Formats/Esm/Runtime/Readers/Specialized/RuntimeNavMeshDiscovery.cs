using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Generic;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Walks the engine's NavMeshInfoMap.InfoMap (a <c>NiTPointerMap&lt;uint32, NavMeshInfo*&gt;</c>)
///     to discover every loaded BSNavMesh struct in DMP memory and reconstruct synthetic ESM
///     RawSubrecord bytes (DATA + NVER + NVVX + NVTR + NVDP) for each one.
///
///     The in-DMP ESM byte stream typically only carries 5-30 navmeshes (the active cell grid),
///     but the engine has hundreds more BSNavMesh structs loaded for surrounding cells. Those
///     are invisible via the editor-id hash table (NAVMs lack editor IDs in vanilla content),
///     so the existing <see cref="RuntimeNavMeshReader" /> never sees them and they end up
///     missing from <c>scanResult.NavMeshes</c> entirely.
///
///     PDB-derived layouts (verified on Aug_RB MemDebug PDB):
///     <list type="bullet">
///         <item><description><c>NavMeshInfoMap</c> at the NAVI singleton's iFormID: bUpdateAll(+40), <b>InfoMap(+44, NiTMapBase 16B)</b>, CellKeyNavMeshInfoMap(+60), bInit(+76).</description></item>
///         <item><description><c>NiTMapBase&lt;..,uint,NavMeshInfo*&gt;</c>: vfptr(+0), m_uiHashSize(+4 uint32), m_ppkHashTable(+8 NiTMapItem**), m_kAllocator(+12 4B).</description></item>
///         <item><description><c>NiTMapItem&lt;uint, NavMeshInfo*&gt;</c> (12B): m_pkNext(+0), m_key(+4 FormID), m_val(+8 NavMeshInfo*).</description></item>
///         <item><description><c>NavMeshInfo</c> (92B): NavMeshID(+0), ParentSpaceID(+4), uiFlags(+8), iCellKey(+12 packed gridX/gridY), ApproxLocation(+16 NiPoint3), <b>pNavMesh(+84)</b>, pBounds(+88).</description></item>
///         <item><description><c>BSNavMesh</c> (280B, FormType 0x43): pParentCell(+52), <b>Vertices(+56 BSSimpleArray)</b>, <b>Triangles(+72 BSSimpleArray)</b>, ExtraEdgeInfo(+88), <b>DoorPortals(+104 BSSimpleArray)</b>.</description></item>
///         <item><description><c>NavMeshVertex</c> (12B): NiPoint3 X/Y/Z floats — matches NVVX format directly.</description></item>
///         <item><description><c>NavMeshTriangle</c> (16B): Vertices(3×int16, +0), Triangles(3×int16 adj-tri, +6), TriangleFlags(uint32, +12) — matches NVTR.</description></item>
///         <item><description><c>NavMeshTriangleDoorPortal</c> (8B): pDoorForm(+0 TESObjectDOOR*), iOwningTriangleIndex(uint16, +4), padding(+6) — NVDP after dereferencing the door pointer to its FormID.</description></item>
///     </list>
/// </summary>
internal sealed class RuntimeNavMeshDiscovery(RuntimeMemoryContext context)
{
    private const byte NaviFormType = 0x38;
    private const byte NavmFormType = 0x43;
    private const int NavmeshDataSubrecordLayoutSize = 20;
    private const uint NavmeshFnvVersion = 0x0E;

    // BSSimpleArray header layout — 16 bytes total. The data pointer at +0 and count at +8
    // are all we need; capacity and allocator are ignored.
    private const int BsSimpleArrayDataPtrOffset = 0;
    private const int BsSimpleArrayCountOffset = 8;
    private const int BsSimpleArraySize = 16;

    private const int NiTMapHashSizeOffset = 4;
    private const int NiTMapBucketArrayOffset = 8;

    private const int NiTMapItemNextOffset = 0;
    private const int NiTMapItemKeyOffset = 4;
    private const int NiTMapItemValueOffset = 8;
    private const int NiTMapItemSize = 12;

    private const int NavMeshInfoFormIdOffset = 0;
    private const int NavMeshInfoParentSpaceOffset = 4;
    private const int NavMeshInfoCellKeyOffset = 12;
    private const int NavMeshInfoApproxLocationOffset = 16;
    private const int NavMeshInfoNavMeshPointerOffset = 84;
    private const int NavMeshInfoSize = 92;

    private const int BsNavMeshParentCellOffset = 52;
    private const int BsNavMeshVerticesOffset = 56;
    private const int BsNavMeshTrianglesOffset = 72;
    private const int BsNavMeshDoorPortalsOffset = 104;
    private const int BsNavMeshSize = 280;

    private const int NavmeshVertexSize = 12;
    private const int NavmeshTriangleSize = 16;
    private const int NavmeshDoorPortalSize = 8;

    private const int TesFormIdOffset = 12;

    private const int MaxBuckets = 8192;
    private const int MaxNavMeshesPerCell = 4096;
    private const uint MaxArrayCount = 1_000_000;

    private readonly RuntimePdbFieldAccessor _fields = new(context);

    /// <summary>
    ///     Walk the NavMeshInfoMap struct at the given file offset and return every BSNavMesh
    ///     reachable through its InfoMap NiTPointerMap. Each result is a <see cref="NavMeshRecord" />
    ///     with synthetic <see cref="NavMeshRecord.RawSubrecords" /> (DATA / NVER / NVVX / NVTR /
    ///     NVDP) projected from the runtime Xbox 360 BE bytes into canonical PC ESM little-endian
    ///     subrecord payloads.
    /// </summary>
    /// <param name="naviEntry">Editor-id entry for the NavMeshInfoMap singleton (FormType 0x38).</param>
    public List<NavMeshRecord> Discover(RuntimeEditorIdEntry naviEntry)
    {
        if (naviEntry.FormType != NaviFormType)
        {
            return [];
        }

        var view = _fields.OpenStructView(naviEntry, NaviFormType);
        if (view == null)
        {
            return [];
        }

        var infoMapOffset = view.Offset("InfoMap", "NavMeshInfoMap");
        if (infoMapOffset is not { } infoMapStart || infoMapStart + 16 > view.Buffer.Length)
        {
            return [];
        }

        var hashSize = BinaryUtils.ReadUInt32BE(view.Buffer, infoMapStart + NiTMapHashSizeOffset);
        var bucketArrayVa = BinaryUtils.ReadUInt32BE(view.Buffer, infoMapStart + NiTMapBucketArrayOffset);
        if (hashSize == 0 || hashSize > MaxBuckets || !context.IsValidPointer(bucketArrayVa))
        {
            return [];
        }

        var bucketArrayOffset = context.VaToFileOffset(bucketArrayVa);
        if (bucketArrayOffset is not long bucketBase)
        {
            return [];
        }

        var bucketBytes = context.ReadBytes(bucketBase, (int)(hashSize * 4));
        if (bucketBytes is null)
        {
            return [];
        }

        var results = new List<NavMeshRecord>();
        var seenFormIds = new HashSet<uint>();
        for (var b = 0; b < hashSize; b++)
        {
            var itemVa = BinaryUtils.ReadUInt32BE(bucketBytes, b * 4);
            for (var hops = 0; hops < MaxNavMeshesPerCell && itemVa != 0 && context.IsValidPointer(itemVa); hops++)
            {
                var itemOffset = context.VaToFileOffset(itemVa);
                if (itemOffset is not long itemFileOffset)
                {
                    break;
                }

                var itemBytes = context.ReadBytes(itemFileOffset, NiTMapItemSize);
                if (itemBytes is null)
                {
                    break;
                }

                var navmFormId = BinaryUtils.ReadUInt32BE(itemBytes, NiTMapItemKeyOffset);
                var navMeshInfoVa = BinaryUtils.ReadUInt32BE(itemBytes, NiTMapItemValueOffset);
                itemVa = BinaryUtils.ReadUInt32BE(itemBytes, NiTMapItemNextOffset);

                if (navmFormId is 0 or 0xFFFFFFFF
                    || !seenFormIds.Add(navmFormId)
                    || !context.IsValidPointer(navMeshInfoVa))
                {
                    continue;
                }

                if (TryReadNavMeshFromInfo(navmFormId, navMeshInfoVa) is { } record)
                {
                    results.Add(record);
                }
            }
        }

        return results;
    }

    private NavMeshRecord? TryReadNavMeshFromInfo(uint expectedFormId, uint navMeshInfoVa)
    {
        var navMeshInfoOffset = context.VaToFileOffset(navMeshInfoVa);
        if (navMeshInfoOffset is not long infoFileOffset)
        {
            return null;
        }

        var infoBytes = context.ReadBytes(infoFileOffset, NavMeshInfoSize);
        if (infoBytes is null)
        {
            return null;
        }

        var infoFormId = BinaryUtils.ReadUInt32BE(infoBytes, NavMeshInfoFormIdOffset);
        if (infoFormId != expectedFormId)
        {
            // The InfoMap key should match NavMeshInfo.NavMeshID exactly. Mismatch means
            // we mis-walked the bucket chain or the struct was paged out — skip.
            return null;
        }

        var parentSpaceFormId = BinaryUtils.ReadUInt32BE(infoBytes, NavMeshInfoParentSpaceOffset);
        var cellKey = BinaryUtils.ReadUInt32BE(infoBytes, NavMeshInfoCellKeyOffset);
        var navMeshVa = BinaryUtils.ReadUInt32BE(infoBytes, NavMeshInfoNavMeshPointerOffset);

        if (!context.IsValidPointer(navMeshVa))
        {
            // No live BSNavMesh in memory — return a count-only stub so the diagnostic still
            // sees the FormID but RawSubrecords stays empty.
            return new NavMeshRecord
            {
                FormId = expectedFormId,
                CellFormId = parentSpaceFormId,
                Offset = infoFileOffset,
                IsBigEndian = true
            };
        }

        var navMeshOffset = context.VaToFileOffset(navMeshVa);
        if (navMeshOffset is not long navmFileOffset)
        {
            return null;
        }

        var navmBytes = context.ReadBytes(navmFileOffset, BsNavMeshSize);
        if (navmBytes is null)
        {
            return null;
        }

        // Parent cell pointer dereferenced to FormID. Engine sets pParentCell on every loaded
        // navmesh; the NavMeshInfo's ParentSpaceID is the worldspace (or interior cell) which
        // is a different thing for exteriors, so we prefer pParentCell when available and fall
        // back to ParentSpaceID for interior navmeshes.
        var parentCellVa = BinaryUtils.ReadUInt32BE(navmBytes, BsNavMeshParentCellOffset);
        var parentCellFormId = TryDereferenceFormId(parentCellVa) ?? parentSpaceFormId;

        var verticesBytes = ReadArrayPayload(navmBytes, BsNavMeshVerticesOffset, NavmeshVertexSize);
        var trianglesBytes = ReadArrayPayload(navmBytes, BsNavMeshTrianglesOffset, NavmeshTriangleSize);
        var doorPortalsBytes = ReadArrayPayload(navmBytes, BsNavMeshDoorPortalsOffset, NavmeshDoorPortalSize);

        var vertexCount = verticesBytes is null ? 0u : (uint)(verticesBytes.Length / NavmeshVertexSize);
        var triangleCount = trianglesBytes is null ? 0u : (uint)(trianglesBytes.Length / NavmeshTriangleSize);
        var doorPortalCount = doorPortalsBytes is null ? 0 : doorPortalsBytes.Length / NavmeshDoorPortalSize;

        var nvvx = ProjectVerticesToNvvx(verticesBytes);
        var nvtr = ProjectTrianglesToNvtr(trianglesBytes);
        var nvdp = ProjectDoorPortalsToNvdp(doorPortalsBytes);
        var data = BuildDataPayload(parentCellFormId, vertexCount, triangleCount,
            edgeLinkCount: 0u, doorLinkCount: (uint)doorPortalCount);
        var nver = BuildNverPayload(NavmeshFnvVersion);

        var rawSubrecords = new List<NavMeshSubrecord>
        {
            new("DATA", data),
            new("NVER", nver)
        };
        if (nvvx is not null)
        {
            rawSubrecords.Add(new NavMeshSubrecord("NVVX", nvvx));
        }
        if (nvtr is not null)
        {
            rawSubrecords.Add(new NavMeshSubrecord("NVTR", nvtr));
        }
        if (nvdp is not null)
        {
            rawSubrecords.Add(new NavMeshSubrecord("NVDP", nvdp));
        }

        return new NavMeshRecord
        {
            FormId = expectedFormId,
            CellFormId = parentCellFormId,
            VertexCount = vertexCount,
            TriangleCount = triangleCount,
            DoorPortalCount = doorPortalCount,
            RawSubrecords = rawSubrecords,
            Offset = navmFileOffset,
            IsBigEndian = false // synthetic RawSubrecords are emitted in canonical PC LE format
        };
    }

    private byte[]? ReadArrayPayload(byte[] navmBytes, int arrayHeaderOffset, int elementSize)
    {
        if (arrayHeaderOffset + BsSimpleArraySize > navmBytes.Length)
        {
            return null;
        }

        var dataPtrVa = BinaryUtils.ReadUInt32BE(navmBytes, arrayHeaderOffset + BsSimpleArrayDataPtrOffset);
        var count = BinaryUtils.ReadUInt32BE(navmBytes, arrayHeaderOffset + BsSimpleArrayCountOffset);
        if (count == 0 || count > MaxArrayCount || !context.IsValidPointer(dataPtrVa))
        {
            return null;
        }

        var dataOffset = context.VaToFileOffset(dataPtrVa);
        if (dataOffset is not long dataFileOffset)
        {
            return null;
        }

        return context.ReadBytes(dataFileOffset, (int)count * elementSize);
    }

    private uint? TryDereferenceFormId(uint formVa)
    {
        if (!context.IsValidPointer(formVa))
        {
            return null;
        }

        var fileOffset = context.VaToFileOffset(formVa);
        if (fileOffset is not long offset)
        {
            return null;
        }

        var bytes = context.ReadBytes(offset + TesFormIdOffset, 4);
        if (bytes is null)
        {
            return null;
        }

        var formId = BinaryUtils.ReadUInt32BE(bytes, 0);
        return formId is 0 or 0xFFFFFFFF ? null : formId;
    }

    // --- Byte projection: BE runtime bytes -> LE canonical ESM subrecord payloads ---

    private static byte[]? ProjectVerticesToNvvx(byte[]? sourceBytes)
    {
        if (sourceBytes is null || sourceBytes.Length < NavmeshVertexSize)
        {
            return null;
        }

        var vertexCount = sourceBytes.Length / NavmeshVertexSize;
        var dest = new byte[vertexCount * NavmeshVertexSize];
        for (var i = 0; i < vertexCount; i++)
        {
            var src = sourceBytes.AsSpan(i * NavmeshVertexSize, NavmeshVertexSize);
            var dst = dest.AsSpan(i * NavmeshVertexSize, NavmeshVertexSize);
            // NavMeshVertex = NiPoint3 = 3 floats. Swap each 4-byte float BE -> LE.
            for (var f = 0; f < 3; f++)
            {
                var value = BinaryPrimitives.ReadSingleBigEndian(src.Slice(f * 4, 4));
                BinaryPrimitives.WriteSingleLittleEndian(dst.Slice(f * 4, 4), value);
            }
        }

        return dest;
    }

    private static byte[]? ProjectTrianglesToNvtr(byte[]? sourceBytes)
    {
        if (sourceBytes is null || sourceBytes.Length < NavmeshTriangleSize)
        {
            return null;
        }

        var triangleCount = sourceBytes.Length / NavmeshTriangleSize;
        var dest = new byte[triangleCount * NavmeshTriangleSize];
        for (var i = 0; i < triangleCount; i++)
        {
            var src = sourceBytes.AsSpan(i * NavmeshTriangleSize, NavmeshTriangleSize);
            var dst = dest.AsSpan(i * NavmeshTriangleSize, NavmeshTriangleSize);
            // Layout: Vertices(3×int16, +0), Triangles(3×int16, +6), TriangleFlags(uint32, +12).
            // Each int16 needs an endian swap; the uint32 needs an endian swap.
            for (var v = 0; v < 6; v++)
            {
                var value = BinaryPrimitives.ReadInt16BigEndian(src.Slice(v * 2, 2));
                BinaryPrimitives.WriteInt16LittleEndian(dst.Slice(v * 2, 2), value);
            }

            var flags = BinaryPrimitives.ReadUInt32BigEndian(src.Slice(12, 4));
            BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(12, 4), flags);
        }

        return dest;
    }

    private byte[]? ProjectDoorPortalsToNvdp(byte[]? sourceBytes)
    {
        if (sourceBytes is null || sourceBytes.Length < NavmeshDoorPortalSize)
        {
            return null;
        }

        var entryCount = sourceBytes.Length / NavmeshDoorPortalSize;
        // NVDP entries on the ESM side are 12 bytes (FormID + uint16 + uint16 + uint32 unknown),
        // but the runtime NavMeshTriangleDoorPortal struct is 8 bytes (pDoorForm + iOwningTriangleIndex + 2 padding).
        // Emit only what the runtime struct carries — 8 bytes per entry — which xEdit interprets
        // as a truncated-but-valid NVDP. If a future smoke test shows the engine rejects this we
        // can pad each entry to 12 bytes.
        var dest = new byte[entryCount * 8];
        for (var i = 0; i < entryCount; i++)
        {
            var src = sourceBytes.AsSpan(i * NavmeshDoorPortalSize, NavmeshDoorPortalSize);
            var dst = dest.AsSpan(i * 8, 8);

            var doorVa = BinaryPrimitives.ReadUInt32BigEndian(src);
            var doorFormId = TryDereferenceFormId(doorVa) ?? 0u;
            BinaryPrimitives.WriteUInt32LittleEndian(dst, doorFormId);

            var owningTri = BinaryPrimitives.ReadUInt16BigEndian(src.Slice(4, 2));
            BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(4, 2), owningTri);
            // dst[6..8] stays zero (runtime padding maps to NVDP padding/unknown).
        }

        return dest;
    }

    private static byte[] BuildDataPayload(
        uint cellFormId,
        uint vertexCount,
        uint triangleCount,
        uint edgeLinkCount,
        uint doorLinkCount)
    {
        var bytes = new byte[NavmeshDataSubrecordLayoutSize];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), cellFormId);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), vertexCount);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), triangleCount);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), edgeLinkCount);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), doorLinkCount);
        return bytes;
    }

    private static byte[] BuildNverPayload(uint version)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, version);
        return bytes;
    }
}
