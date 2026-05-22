using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Generic;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for NavMeshInfoMap (NAVI, FormType 0x38).
///     Single per-ESM record holding cross-cell pathfinding metadata. Skips the
///     NiTPointerMap / NiTMap hash tables (would require map-walking).
/// </summary>
internal sealed class RuntimeNavMeshInfoMapReader(RuntimeMemoryContext context)
{
    private const byte NaviFormType = 0x38;

    private readonly RuntimePdbFieldAccessor _fields = new(context);

    public NavMeshInfoMapRecord? ReadRuntimeNavMeshInfoMap(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != NaviFormType)
        {
            return null;
        }

        var view = _fields.OpenStructView(entry);
        if (view == null)
        {
            return null;
        }

        return new NavMeshInfoMapRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            UpdateAll = view.Byte("bUpdateAll", "NavMeshInfoMap") != 0,
            Initialized = view.Byte("bInit", "NavMeshInfoMap") != 0,
            Offset = view.FileOffset,
            IsBigEndian = true
        };
    }
}
