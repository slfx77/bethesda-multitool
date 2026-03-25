namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Layouts;

/// <summary>
///     Layout for TESObjectBOOK runtime struct. Offsets are organized by inheritance group:
///     Group 0: TESForm (anchored, never shifts)
///     Group 1: TESFullName through TESEnchantableForm (model, enchantment, etc.)
///     Group 2: TESValueForm through OBJ_BOOK (value, weight, book data)
/// </summary>
internal readonly record struct RuntimeBookLayout(
    int FullNameOffset,
    int ModelOffset,
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
            68,
            80,
            136,
            140,
            152,
            160,
            208,
            212);
    }

    /// <summary>
    ///     Create a layout from a shift array produced by the probe engine.
    ///     Group 0 = TESForm (anchor), Group 1 = mid-chain, Group 2 = late fields.
    /// </summary>
    public static RuntimeBookLayout FromShifts(int[] shifts)
    {
        var d = CreateDefault();
        var s1 = shifts.Length > 1 ? shifts[1] : 0;
        var s2 = shifts.Length > 2 ? shifts[2] : 0;
        return new RuntimeBookLayout(
            d.FullNameOffset + s1,
            d.ModelOffset + s1,
            d.EnchantmentPtrOffset + s1,
            d.EnchantmentAmountOffset + s1,
            d.ValueOffset + s2,
            d.WeightOffset + s2,
            d.BookDataOffset + s2,
            d.StructSize + Math.Max(s1, s2));
    }
}