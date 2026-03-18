using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Typed runtime reader for TESObjectBOOK (BOOK, 212 bytes, FormType 0x19).
///     Reads full name, model, value, weight, book flags/skill, and enchantment pointer.
/// </summary>
internal sealed class RuntimeBookReader
{
    private readonly RuntimeMemoryContext _context;

    public RuntimeBookReader(RuntimeMemoryContext context)
    {
        _context = context;
    }

    public BookRecord? ReadRuntimeBook(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != BookFormType)
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

        // OBJ_BOOK at +208: flags (1 byte) + skillTaught (1 byte)
        var flags = buffer[BookDataOffset];
        var skillTaught = buffer[BookDataOffset + 1];

        // Follow enchantment pointer to get EnchantmentItem FormID
        var enchantmentFormId = _context.FollowPointerToFormId(buffer, EnchantmentPtrOffset);
        var enchantmentAmount = BinaryUtils.ReadUInt16BE(buffer, EnchantmentAmountOffset);

        return new BookRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = fullName,
            ModelPath = modelPath,
            Value = value,
            Weight = weight,
            Flags = flags,
            SkillTaught = skillTaught,
            EnchantmentFormId = enchantmentFormId,
            EnchantmentAmount = enchantmentAmount,
            Offset = offset,
            IsBigEndian = true
        };
    }

    #region Constants

    private const byte BookFormType = 0x19;
    private const int StructSize = 212;
    private const int FormIdOffset = 12;
    private const int FullNameOffset = 68;           // TESFullName.cFullName BSStringT
    private const int ModelOffset = 80;              // TESModel.cModel BSStringT
    private const int EnchantmentPtrOffset = 136;    // TESEnchantableForm.pFormEnchanting (pointer)
    private const int EnchantmentAmountOffset = 140; // TESEnchantableForm.iAmountofEnchantment (uint16)
    private const int ValueOffset = 152;             // TESValueForm.iValue (uint32)
    private const int WeightOffset = 160;            // TESWeightForm.fWeight (float32)
    private const int BookDataOffset = 208;          // OBJ_BOOK: flags(1) + skillTaught(1)

    #endregion
}
