using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for NavMesh structs (FormType 0x43, 280 bytes).
///     Reads pParentCell (followed for cell FormID) and the count fields of the
///     Vertices / Triangles / DoorPortals BSSimpleArrays.
/// </summary>
internal sealed class RuntimeNavMeshReader(RuntimeMemoryContext context)
{
    public NavMeshRecord? ReadRuntimeNavMesh(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != NavmFormType)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + StructSize > context.FileSize)
        {
            return null;
        }

        var buffer = new byte[StructSize];
        try
        {
            context.Accessor.ReadArray(offset, buffer, 0, StructSize);
        }
        catch
        {
            return null;
        }

        var formId = BinaryUtils.ReadUInt32BE(buffer, FormIdOffset);
        if (formId != entry.FormId || formId == 0)
        {
            return null;
        }

        // pParentCell is a 4-byte pointer; follow to TESObjectCELL → FormID at +12.
        var cellFormId = context.FollowPointerToFormId(buffer, ParentCellPointerOffset) ?? 0;

        // BSSimpleArray<T> layout: data ptr (+0), capacity (+4), count (+8), reserved (+12)
        var vertexCount = BinaryUtils.ReadUInt32BE(buffer, VerticesOffset + ArrayCountFieldOffset);
        var triangleCount = BinaryUtils.ReadUInt32BE(buffer, TrianglesOffset + ArrayCountFieldOffset);
        var doorPortalCount = (int)BinaryUtils.ReadUInt32BE(buffer, DoorPortalsOffset + ArrayCountFieldOffset);

        // Sanity guards: discard absurdly large values (likely uninitialized memory).
        if (vertexCount > MaxArrayCount) vertexCount = 0;
        if (triangleCount > MaxArrayCount) triangleCount = 0;
        if (doorPortalCount < 0 || doorPortalCount > MaxArrayCount) doorPortalCount = 0;

        return new NavMeshRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            CellFormId = cellFormId,
            VertexCount = vertexCount,
            TriangleCount = triangleCount,
            DoorPortalCount = doorPortalCount,
            Offset = offset,
            IsBigEndian = true
        };
    }

    #region Constants

    private const byte NavmFormType = 0x43;
    private const int StructSize = 280;
    private const int FormIdOffset = 12;
    private const int ParentCellPointerOffset = 52;
    private const int VerticesOffset = 56;
    private const int TrianglesOffset = 72;
    private const int DoorPortalsOffset = 104;
    private const int ArrayCountFieldOffset = 8;

    /// <summary>Reject array counts beyond this (a single navmesh has at most ~thousands of verts).</summary>
    private const uint MaxArrayCount = 1_000_000;

    #endregion
}
