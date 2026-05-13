using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for BGSCameraPath (CPTH, 72 bytes, FormType 0x5C).
///     Reads PATH_DATA flag + parent/previous path pointers.
/// </summary>
internal sealed class RuntimeCameraPathReader(RuntimeMemoryContext context)
{
    public CameraPathRecord? ReadRuntimeCameraPath(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != CpthFormType)
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

        var flags = buffer[DataOffset];
        var parentPathFormId = context.FollowPointerToFormId(buffer, ParentPathPointerOffset) ?? 0;
        var prevPathFormId = context.FollowPointerToFormId(buffer, PrevPathPointerOffset) ?? 0;

        return new CameraPathRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            Flags = flags,
            ParentPathFormId = parentPathFormId,
            PreviousPathFormId = prevPathFormId,
            Offset = offset,
            IsBigEndian = true
        };
    }

    #region Constants

    private const byte CpthFormType = 0x5C;
    private const int StructSize = 72;
    private const int FormIdOffset = 12;
    private const int DataOffset = 56;
    private const int ParentPathPointerOffset = 64;
    private const int PrevPathPointerOffset = 68;

    #endregion
}
