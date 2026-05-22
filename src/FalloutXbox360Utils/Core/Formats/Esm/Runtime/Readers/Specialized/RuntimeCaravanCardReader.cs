using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Generic;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for TESCaravanCard (CCRD, FormType 0x73).
///     Reads FullName, model path, value, script + pickup/putdown sound FormIDs via
///     the PDB layout. Skips the CARAVANCARDDATA suit/rank tuple (variable per-build encoding).
/// </summary>
internal sealed class RuntimeCaravanCardReader(RuntimeMemoryContext context)
{
    private const byte CcrdFormType = 0x73;

    private readonly RuntimePdbFieldAccessor _fields = new(context);

    public CaravanCardRecord? ReadRuntimeCaravanCard(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != CcrdFormType)
        {
            return null;
        }

        var view = _fields.OpenStructView(entry);
        if (view == null)
        {
            return null;
        }

        return new CaravanCardRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = view.BsString("cFullName", "TESFullName"),
            ModelPath = view.BsString("cModel", "TESModel"),
            Value = view.UInt32("iValue", "TESValueForm"),
            ScriptFormId = view.FormIdPointer("pFormScript", "TESScriptableForm") ?? 0,
            PickupSoundFormId = view.FormIdPointer("pPickupSound", "BGSPickupPutdownSounds") ?? 0,
            PutdownSoundFormId = view.FormIdPointer("pPutdownSound", "BGSPickupPutdownSounds") ?? 0,
            Offset = view.FileOffset,
            IsBigEndian = true
        };
    }
}
