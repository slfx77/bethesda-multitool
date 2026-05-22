using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Generic;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for BGSMovableStatic (MSTT, FormType 0x22).
///     Surfaces FullName, model path, and the two sound pointers (pRandomSound,
///     pSoundLoop) — the fields that connect MSTT base forms to the audio markers
///     placed in cells.
///
///     MSTT uses an unusual multi-inheritance order in BGSMovableStatic where
///     TESFullName + BGSDestructibleObjectForm are laid out BEFORE TESForm in the
///     C++ class. That puts <c>cFormType</c> at offset +24 and <c>iFormID</c> at
///     offset +32 within the object. <see cref="RuntimePdbFieldAccessor.ReadStruct" />
///     resolves these offsets from the PDB layout itself, so the regular
///     OpenStructView flow handles MSTT without reader-side gymnastics — but the
///     reader still depends on
///     <see cref="FalloutXbox360Utils.Core.Formats.Esm.Records.TesFormHeaderProbe" />
///     having populated <c>entry.FormId</c> from the FLOR/MSTT-specific iFormID
///     offset before invocation.
/// </summary>
internal sealed class RuntimeMsttReader(RuntimeMemoryContext context)
{
    private const byte MsttFormType = 0x22;
    private const byte SounFormType = 0x0D;

    private readonly RuntimePdbFieldAccessor _fields = new(context);

    public GenericEsmRecord? ReadRuntimeMstt(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != MsttFormType)
        {
            return null;
        }

        var view = _fields.OpenStructView(entry);
        if (view == null)
        {
            return null;
        }

        var fullName = view.BsString("cFullName", "TESFullName");
        var modelPath = view.BsString("cModel", "TESModel");
        // pRandomSound + pSoundLoop are TESSound* (SOUN = FormType 0x0D). Constrain
        // with the FormType overload so a stale pointer that resolves to a nearby
        // form of a different type isn't surfaced as a "sound".
        var randomSoundFormId = view.FormIdPointer("pRandomSound", "TESObjectSTAT", SounFormType);
        var soundLoopFormId = view.FormIdPointer("pSoundLoop", "BGSMovableStatic", SounFormType);

        var fields = new Dictionary<string, object?>();
        if (randomSoundFormId.HasValue)
        {
            fields["pRandomSound"] = randomSoundFormId.Value;
        }

        if (soundLoopFormId.HasValue)
        {
            fields["pSoundLoop"] = soundLoopFormId.Value;
        }

        return new GenericEsmRecord
        {
            FormId = entry.FormId,
            RecordType = "MSTT",
            EditorId = entry.EditorId,
            FullName = fullName,
            ModelPath = modelPath,
            Fields = fields,
            Offset = view.FileOffset,
            IsBigEndian = true
        };
    }
}
