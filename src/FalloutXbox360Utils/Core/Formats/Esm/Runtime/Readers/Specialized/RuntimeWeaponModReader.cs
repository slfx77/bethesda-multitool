using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Generic;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for TESObjectIMOD (IMOD, FormType 0x67).
///     Reads full name, model, icon, value, weight via the PDB struct layout — no
///     hardcoded offsets.
/// </summary>
internal sealed class RuntimeWeaponModReader
{
    private const byte ImodFormType = 0x67;

    private readonly RuntimePdbFieldAccessor _fields;

    public RuntimeWeaponModReader(RuntimeMemoryContext context)
    {
        _fields = new RuntimePdbFieldAccessor(context);
    }

    public WeaponModRecord? ReadRuntimeWeaponMod(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != ImodFormType)
        {
            return null;
        }

        var view = _fields.OpenStructView(entry, ImodFormType);
        if (view == null)
        {
            return null;
        }

        return new WeaponModRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = entry.DisplayName ?? view.BsString("cFullName", "TESFullName"),
            ModelPath = view.BsString("cModel", "TESModel"),
            Icon = view.BsString("TextureName", "TESTexture"),
            Value = view.Int32Range("iValue", "TESValueForm", 0, 1_000_000),
            Weight = view.FloatRange("fWeight", "TESWeightForm", 0, 500),
            Offset = view.FileOffset,
            IsBigEndian = true
        };
    }
}
