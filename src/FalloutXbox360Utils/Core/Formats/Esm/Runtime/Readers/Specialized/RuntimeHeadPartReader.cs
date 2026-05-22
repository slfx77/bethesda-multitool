using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Generic;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for BGSHeadPart (HDPT, FormType 0x09).
///     Reads FullName, model path, and the BGSHeadPart cFlags byte via the PDB layout.
/// </summary>
internal sealed class RuntimeHeadPartReader(RuntimeMemoryContext context)
{
    private const byte HdptFormType = 0x09;

    private readonly RuntimePdbFieldAccessor _fields = new(context);

    public HeadPartRecord? ReadRuntimeHeadPart(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != HdptFormType)
        {
            return null;
        }

        var view = _fields.OpenStructView(entry);
        if (view == null)
        {
            return null;
        }

        return new HeadPartRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = view.BsString("cFullName", "TESFullName"),
            ModelPath = view.BsString("cModel", "TESModel"),
            Flags = view.Byte("cFlags", "BGSHeadPart"),
            Offset = view.FileOffset,
            IsBigEndian = true
        };
    }
}
