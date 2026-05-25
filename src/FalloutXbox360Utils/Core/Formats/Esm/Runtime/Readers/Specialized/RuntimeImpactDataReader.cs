using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Generic;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for BGSImpactData (IPCT, FormType 0x5E).
///     Reads model path + decal texture / sound FormID pointers via the PDB layout.
/// </summary>
internal sealed class RuntimeImpactDataReader(RuntimeMemoryContext context)
{
    private const byte IpctFormType = 0x5E;

    private readonly RuntimePdbFieldAccessor _fields = new(context);

    public ImpactDataRecord? ReadRuntimeImpactData(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != IpctFormType)
        {
            return null;
        }

        var view = _fields.OpenStructView(entry, IpctFormType);
        if (view == null)
        {
            return null;
        }

        return new ImpactDataRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            ModelPath = view.BsString("cModel", "TESModel"),
            DecalTextureSetFormId = view.FormIdPointer("pDecalTextureSet", "BGSImpactData") ?? 0,
            Sound1FormId = view.FormIdPointer("pSound1", "BGSImpactData") ?? 0,
            Sound2FormId = view.FormIdPointer("pSound2", "BGSImpactData") ?? 0,
            Offset = view.FileOffset,
            IsBigEndian = true
        };
    }
}
