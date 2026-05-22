using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Item;

/// <summary>
///     Encodes a <see cref="BookRecord" /> as PC-format BOOK subrecord bytes.
///     Emits DATA (10 bytes) only. DESC (book text) is retained from the source ESM since
///     book content is largely static.
///     DATA layout: uint8 Flags(0) + uint8 SkillTaught(1) + int32 Value(2..5) + float Weight(6..9).
/// </summary>
public sealed class BookEncoder : IRecordEncoder
{
    private static readonly Dictionary<string, Func<BookRecord, object?>> DataExtractors = new(StringComparer.Ordinal)
    {
        ["Flags"] = m => m.Flags,
        ["Skill"] = m => (sbyte)m.SkillTaught,
        ["Value"] = m => m.Value,
        ["Weight"] = m => m.Weight,
    };

    public string RecordType => "BOOK";
    public Type ModelType => typeof(BookRecord);

    public EncodedRecord Encode(object model)
    {
        var book = (BookRecord)model;
        return new EncodedRecord
        {
            Subrecords = [SchemaModelSerializer.SerializeSubrecord("DATA", "BOOK", 10, book, DataExtractors)],
            Warnings = []
        };
    }

    /// <summary>
    ///     Encode a new BOOK record from scratch. fopdoc canonical order:
    ///     EDID, OBND?, FULL?, MODL?, DESC?, DATA, ENAM? (enchantment).
    /// </summary>
    internal static EncodedRecord EncodeNew(BookRecord book)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(book.EditorId))
        {
            warnings.Add($"New BOOK 0x{book.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", book.EditorId ?? string.Empty));

        if (book.Bounds is not null)
        {
            subs.Add(NewRecordSubrecords.EncodeObndSubrecord(book.Bounds));
        }

        if (!string.IsNullOrEmpty(book.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", book.FullName));
        }

        if (!string.IsNullOrEmpty(book.ModelPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MODL", book.ModelPath));
        }

        if (book.TextureHashData is { Length: > 0 } modt)
        {
            subs.Add(NewRecordSubrecords.EncodeByteArraySubrecord("MODT", modt));
        }

        if (!string.IsNullOrEmpty(book.IconPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("ICON", book.IconPath));
        }

        if (!string.IsNullOrEmpty(book.MessageIconPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MICO", book.MessageIconPath));
        }

        if (!string.IsNullOrEmpty(book.Text))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("DESC", book.Text));
        }

        subs.Add(SchemaModelSerializer.SerializeSubrecord("DATA", "BOOK", 10, book, DataExtractors));

        if (book.EnchantmentFormId.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("ENAM", book.EnchantmentFormId.Value));
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
