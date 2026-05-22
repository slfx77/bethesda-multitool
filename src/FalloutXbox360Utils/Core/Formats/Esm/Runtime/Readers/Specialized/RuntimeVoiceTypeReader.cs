using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Generic;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for BGSVoiceType (VTYP, FormType 0x5D).
///     Reads the VOICE_TYPE_DATA byte via the PDB layout.
/// </summary>
internal sealed class RuntimeVoiceTypeReader(RuntimeMemoryContext context)
{
    private const byte VtypFormType = 0x5D;

    private readonly RuntimePdbFieldAccessor _fields = new(context);

    public VoiceTypeRecord? ReadRuntimeVoiceType(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != VtypFormType)
        {
            return null;
        }

        var view = _fields.OpenStructView(entry);
        if (view == null)
        {
            return null;
        }

        return new VoiceTypeRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            Flags = view.Byte("Data", "BGSVoiceType"),
            Offset = view.FileOffset,
            IsBigEndian = true
        };
    }
}
