using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Generic;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for BGSMessage (MESG, FormType 0x62).
///     Reads full name, iFlags, and iDisplayTime via the PDB layout.
///     Description and button list are ESM-only.
/// </summary>
internal sealed class RuntimeMessageReader(RuntimeMemoryContext context)
{
    private const byte MesgFormType = 0x62;

    private readonly RuntimePdbFieldAccessor _fields = new(context);

    public MessageRecord? ReadRuntimeMessage(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != MesgFormType)
        {
            return null;
        }

        var view = _fields.OpenStructView(entry);
        if (view == null)
        {
            return null;
        }

        return new MessageRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = entry.DisplayName ?? view.BsString("cFullName", "TESFullName"),
            Flags = view.UInt32("iFlags", "BGSMessage"),
            DisplayTime = view.UInt32("iDisplayTime", "BGSMessage"),
            Offset = view.FileOffset,
            IsBigEndian = true
        };
    }
}
