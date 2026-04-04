using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for TESObjectBOOK (BOOK, ~212 bytes, FormType 0x19).
///     Reads full name, model, value, weight, book flags/skill, and enchantment pointer.
///     Supports auto-detected layouts via <see cref="RuntimeBookProbe" />.
/// </summary>
internal sealed class RuntimeBookReader
{
    private const byte BookFormType = 0x19;
    private const int FormIdOffset = 12;
    private const int MinProbeMargin = 3;
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
}
