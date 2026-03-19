using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm;

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
    public static RuntimeBookLayout CreateDefault() => new(
        FullNameOffset: 68,
        ModelOffset: 80,
        EnchantmentPtrOffset: 136,
        EnchantmentAmountOffset: 140,
        ValueOffset: 152,
        WeightOffset: 160,
        BookDataOffset: 208,
        StructSize: 212);

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
            FullNameOffset: d.FullNameOffset + s1,
            ModelOffset: d.ModelOffset + s1,
            EnchantmentPtrOffset: d.EnchantmentPtrOffset + s1,
            EnchantmentAmountOffset: d.EnchantmentAmountOffset + s1,
            ValueOffset: d.ValueOffset + s2,
            WeightOffset: d.WeightOffset + s2,
            BookDataOffset: d.BookDataOffset + s2,
            StructSize: d.StructSize + Math.Max(s1, s2));
    }
}

/// <summary>
///     Typed runtime reader for TESObjectBOOK (BOOK, ~212 bytes, FormType 0x19).
///     Reads full name, model, value, weight, book flags/skill, and enchantment pointer.
///     Supports auto-detected layouts via <see cref="RuntimeBookProbe" />.
/// </summary>
internal sealed class RuntimeBookReader
{
    private readonly RuntimeMemoryContext _context;
    private readonly RuntimeBookLayout _layout;

    public RuntimeBookReader(RuntimeMemoryContext context, RuntimeLayoutProbeResult<int[]>? probeResult = null)
    {
        _context = context;
        _layout = probeResult is { Margin: >= MinProbeMargin }
            ? RuntimeBookLayout.FromShifts(probeResult.Winner.Layout)
            : RuntimeBookLayout.CreateDefault();
    }

    public BookRecord? ReadRuntimeBook(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != BookFormType)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + _layout.StructSize > _context.FileSize)
        {
            return null;
        }

        var buffer = new byte[_layout.StructSize];
        try
        {
            _context.Accessor.ReadArray(offset, buffer, 0, _layout.StructSize);
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

        var fullName = entry.DisplayName ?? _context.ReadBSStringT(offset, _layout.FullNameOffset);
        var modelPath = _context.ReadBSStringT(offset, _layout.ModelOffset);

        var value = RuntimeMemoryContext.ReadInt32BE(buffer, _layout.ValueOffset);
        if (value < 0 || value > 1_000_000)
        {
            value = 0;
        }

        var weight = RuntimeMemoryContext.ReadValidatedFloat(buffer, _layout.WeightOffset, 0, 500);

        // OBJ_BOOK: flags (1 byte) + skillTaught (1 byte)
        var flags = buffer[_layout.BookDataOffset];
        var skillTaught = buffer[_layout.BookDataOffset + 1];

        // Follow enchantment pointer to get EnchantmentItem FormID
        var enchantmentFormId = _context.FollowPointerToFormId(buffer, _layout.EnchantmentPtrOffset);
        var enchantmentAmount = BinaryUtils.ReadUInt16BE(buffer, _layout.EnchantmentAmountOffset);

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

    private const byte BookFormType = 0x19;
    private const int FormIdOffset = 12;
    private const int MinProbeMargin = 3;
}

/// <summary>
///     Probe for TESObjectBOOK layout auto-detection across build versions.
///     Uses pointer validation, float checks, and string resolution to score candidates.
/// </summary>
internal static class RuntimeBookProbe
{
    private static readonly RuntimeReaderFieldProbe.FieldSpec[] ProbeFields =
    [
        // Group 1: TESFullName through TESEnchantableForm
        new("FullName", 68, 1, RuntimeReaderFieldProbe.FieldCheck.BSStringT, Weight: 2),
        new("Model", 80, 1, RuntimeReaderFieldProbe.FieldCheck.BSStringT, Weight: 2),
        new("Enchantment", 136, 1, RuntimeReaderFieldProbe.FieldCheck.PointerToFormType, Weight: 2,
            CheckArg: (byte)0x13), // ENCH
        // Group 2: TESValueForm through OBJ_BOOK
        new("Value", 152, 2, RuntimeReaderFieldProbe.FieldCheck.Int32Range, CheckArg: (0, 1_000_000)),
        new("Weight", 160, 2, RuntimeReaderFieldProbe.FieldCheck.RangedFloat, CheckArg: (0f, 500f)),
        new("SkillTaught", 209, 2, RuntimeReaderFieldProbe.FieldCheck.ByteRange, CheckArg: ((byte)0, (byte)80))
    ];

    private static readonly int[] ShiftOptions = [-8, -4, 0, 4, 8];

    public static RuntimeLayoutProbeResult<int[]>? Probe(
        RuntimeMemoryContext context,
        IReadOnlyList<RuntimeEditorIdEntry> entries,
        Action<string>? log = null)
    {
        var bookEntries = entries.Where(e => e.FormType == 0x19).ToList();
        if (bookEntries.Count == 0)
        {
            return null;
        }

        return RuntimeReaderFieldProbe.Probe(
            context,
            bookEntries,
            ProbeFields,
            groupCount: 2,
            ShiftOptions,
            baseStructSize: 212,
            "BookLayout",
            log: log);
    }
}
