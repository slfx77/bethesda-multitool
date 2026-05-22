using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Generic;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for BGSCameraPath (CPTH, FormType 0x5C).
///     Reads PATH_DATA flag + parent/previous path pointers via the PDB layout.
/// </summary>
internal sealed class RuntimeCameraPathReader(RuntimeMemoryContext context)
{
    private const byte CpthFormType = 0x5C;

    private readonly RuntimePdbFieldAccessor _fields = new(context);

    public CameraPathRecord? ReadRuntimeCameraPath(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != CpthFormType)
        {
            return null;
        }

        var view = _fields.OpenStructView(entry);
        if (view == null)
        {
            return null;
        }

        return new CameraPathRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            Flags = view.Byte("data", "BGSCameraPath"),
            ParentPathFormId = view.FormIdPointer("pParentPath", "BGSCameraPath") ?? 0,
            PreviousPathFormId = view.FormIdPointer("pPrevPath", "BGSCameraPath") ?? 0,
            Offset = view.FileOffset,
            IsBigEndian = true
        };
    }
}
