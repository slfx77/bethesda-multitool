using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Generic;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for BGSMenuIcon (MICN, FormType 0x05).
///     Reads TextureName via the PDB layout.
/// </summary>
internal sealed class RuntimeMenuIconReader(RuntimeMemoryContext context)
{
    private const byte MicnFormType = 0x05;

    private readonly RuntimePdbFieldAccessor _fields = new(context);

    public MenuIconRecord? ReadRuntimeMenuIcon(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != MicnFormType)
        {
            return null;
        }

        var view = _fields.OpenStructView(entry);
        if (view == null)
        {
            return null;
        }

        return new MenuIconRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            IconPath = view.BsString("TextureName", "TESTexture"),
            Offset = view.FileOffset,
            IsBigEndian = true
        };
    }
}
