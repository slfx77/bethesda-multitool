using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Character;
using BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Generic;

namespace BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Specialized.Actor;

/// <summary>
///     Typed runtime reader for BGSHeadPart (HDPT, FormType 0x09).
///     Reads FullName, model path, and the BGSHeadPart cFlags byte via the PDB layout.
/// </summary>
internal sealed class RuntimeHeadPartReader(RuntimeMemoryContext context)
{
    private const byte HdptFormType = 0x09;

    private readonly RuntimePdbFieldAccessor _fields = new(context);

    /// <summary>Reads the runtime head-part record for the given DMP entry, or null if it can't be read.</summary>
    public HeadPartRecord? ReadRuntimeHeadPart(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != HdptFormType)
        {
            return null;
        }

        var view = _fields.OpenStructView(entry, HdptFormType);
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
