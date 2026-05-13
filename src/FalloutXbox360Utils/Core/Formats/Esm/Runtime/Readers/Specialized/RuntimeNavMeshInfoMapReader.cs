using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for NavMeshInfoMap (NAVI, 80 bytes, FormType 0x38).
///     Single per-ESM record holding cross-cell pathfinding metadata. Skips the
///     NiTPointerMap / NiTMap hash tables (would require map-walking).
/// </summary>
internal sealed class RuntimeNavMeshInfoMapReader(RuntimeMemoryContext context)
{
    public NavMeshInfoMapRecord? ReadRuntimeNavMeshInfoMap(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != NaviFormType)
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

        return new NavMeshInfoMapRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            UpdateAll = buffer[UpdateAllOffset] != 0,
            Initialized = buffer[InitOffset] != 0,
            Offset = offset,
            IsBigEndian = true
        };
    }

    #region Constants

    private const byte NaviFormType = 0x38;
    private const int StructSize = 80;
    private const int FormIdOffset = 12;
    private const int UpdateAllOffset = 40;
    private const int InitOffset = 76;

    #endregion
}
