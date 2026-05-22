using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Generic;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for NavMesh structs (FormType 0x43).
///     Reads pParentCell (followed for cell FormID) and the count fields of the
///     Vertices / Triangles / DoorPortals BSSimpleArrays via the PDB layout.
/// </summary>
internal sealed class RuntimeNavMeshReader(RuntimeMemoryContext context)
{
    private const byte NavmFormType = 0x43;
    private const int ArrayCountFieldOffset = 8;

    /// <summary>Reject array counts beyond this (a single navmesh has at most ~thousands of verts).</summary>
    private const uint MaxArrayCount = 1_000_000;

    private readonly RuntimePdbFieldAccessor _fields = new(context);

    public NavMeshRecord? ReadRuntimeNavMesh(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != NavmFormType)
        {
            return null;
        }

        var view = _fields.OpenStructView(entry);
        if (view == null)
        {
            return null;
        }

        return new NavMeshRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            CellFormId = view.FormIdPointer("pParentCell", "NavMesh") ?? 0,
            VertexCount = ReadArrayCount(view, "Vertices"),
            TriangleCount = ReadArrayCount(view, "Triangles"),
            DoorPortalCount = (int)ReadArrayCount(view, "DoorPortals"),
            Offset = view.FileOffset,
            IsBigEndian = true
        };
    }

    /// <summary>
    ///     BSSimpleArray<T> layout: data ptr (+0), capacity (+4), count (+8), reserved (+12).
    ///     Returns 0 if the array offset can't be resolved or the count looks like uninitialized memory.
    /// </summary>
    private static uint ReadArrayCount(PdbStructView view, string fieldName)
    {
        var off = view.Offset(fieldName, "NavMesh");
        if (off is not { } o || o + ArrayCountFieldOffset + 4 > view.Buffer.Length)
        {
            return 0;
        }

        var count = BinaryUtils.ReadUInt32BE(view.Buffer, o + ArrayCountFieldOffset);
        return count > MaxArrayCount ? 0 : count;
    }
}
