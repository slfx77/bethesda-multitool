using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for TESObjectIMOD (IMOD, 192 bytes, FormType 0x67).
///     Reads full name, model, value, weight.
/// </summary>
internal sealed class RuntimeWeaponModReader
{
    private readonly RuntimeMemoryContext _context;

    public RuntimeWeaponModReader(RuntimeMemoryContext context)
    {
        _context = context;
    }

    public WeaponModRecord? ReadRuntimeWeaponMod(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != ImodFormType)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + StructSize > _context.FileSize)
        {
            return null;
        }

        var buffer = new byte[StructSize];
        try
        {
            _context.Accessor.ReadArray(offset, buffer, 0, StructSize);
        }
        catch
        {
            return null;
        }

        var formId = BinaryUtils.ReadUInt32BE(buffer, FormIdOffset);
        if (formId != entry.FormId || formId == 0)
        {
            return null;
        }

        var fullName = entry.DisplayName ?? _context.ReadBSStringT(offset, FullNameOffset);
        var modelPath = _context.ReadBSStringT(offset, ModelOffset);

        var value = RuntimeMemoryContext.ReadInt32BE(buffer, ValueOffset);
        if (value < 0 || value > 1_000_000)
        {
            value = 0;
        }

        var weight = RuntimeMemoryContext.ReadValidatedFloat(buffer, WeightOffset, 0, 500);

        return new WeaponModRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = fullName,
            ModelPath = modelPath,
            Value = value,
            Weight = weight,
            Offset = offset,
            IsBigEndian = true
        };
    }

    #region Constants

    private const byte ImodFormType = 0x67;
    private const int StructSize = 192;
    private const int FormIdOffset = 12;
    private const int FullNameOffset = 68; // TESFullName.cFullName BSStringT
    private const int ModelOffset = 80; // TESModel.cModel BSStringT
    private const int ValueOffset = 144; // TESValueForm.iValue (uint32)
    private const int WeightOffset = 152; // TESWeightForm.fWeight (float32)

    #endregion
}
