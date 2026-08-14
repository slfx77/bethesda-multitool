using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Generic;

namespace BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for BGSMovableStatic (MSTT, FormType 0x22).
///     Surfaces FullName, model path, and the two sound pointers (pRandomSound,
///     pSoundLoop) — the fields that connect MSTT base forms to the audio markers
///     placed in cells.
///
///     MSTT uses an unusual multiple-inheritance order in BGSMovableStatic where
///     its TESForm subobject begins at +20 in the complete object. Thus the identity
///     fields are at complete-object offsets +24/+32, but a runtime form-map value is
///     already <c>TESForm*</c> and reads them at canonical subobject offsets +4/+12.
///     <c>RuntimePdbFieldAccessor.ReadStruct</c> uses the PDB layout to rebase that
///     retained subobject pointer to the complete-object base before applying fields.
/// </summary>
internal sealed class RuntimeMsttReader(RuntimeMemoryContext context)
{
    private const byte MsttFormType = 0x22;
    private const byte SounFormType = 0x0D;

    private readonly RuntimePdbFieldAccessor _fields = new(context);

    /// <summary>Reads a runtime MSTT (movable static) base form for the given DMP entry as a generic record.</summary>
    public GenericEsmRecord? ReadRuntimeMstt(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != MsttFormType)
        {
            return null;
        }

        var view = _fields.OpenStructView(entry, MsttFormType);
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
