namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Layouts;

/// <summary>
///     Layout for TESObjectBOOK runtime struct. Offsets are organized by inheritance group:
///     Group 0: TESForm (anchored, never shifts)
///     Group 1: TESFullName through TESEnchantableForm (model, enchantment, icons, etc.)
///     Group 2: TESValueForm through OBJ_BOOK (value, weight, book data)
///
///     The Group 2 fields (Value/Weight/BookData) are at offsets 8 bytes earlier than
///     the PDB-reported values: PDB says Value=152/Weight=160/BookData=208, but every
///     observed runtime dump (32/32 in the Phase 1B.5 probe sweep) has them at
///     144/152/200. The previous probe-driven `FromShifts` path was deleted in Phase
///     1B.6 once the constant was confirmed across all builds in scope.
/// </summary>
internal readonly record struct RuntimeBookLayout(
    int FullNameOffset,
    int ModelOffset,
    int InventoryIconPathOffset,
    int MessageIconPathOffset,
    int EnchantmentPtrOffset,
    int EnchantmentAmountOffset,
    int ValueOffset,
    int WeightOffset,
    int BookDataOffset,
    int StructSize)
{
    public static RuntimeBookLayout CreateDefault()
    {
        return new RuntimeBookLayout(
            FullNameOffset: 68,
            ModelOffset: 80,
            InventoryIconPathOffset: 112, // TESTexture.TextureName (BSStringT) — ICON
            MessageIconPathOffset: 184,   // BGSMessageIcon.Icon (TESIcon→BSStringT) — MICO
            EnchantmentPtrOffset: 136,
            EnchantmentAmountOffset: 140,
            ValueOffset: 144,             // PDB says 152; runtime sits 8 bytes earlier (G2=-8)
            WeightOffset: 152,            // PDB says 160
            BookDataOffset: 200,          // PDB says 208
            StructSize: 212);
    }
}
