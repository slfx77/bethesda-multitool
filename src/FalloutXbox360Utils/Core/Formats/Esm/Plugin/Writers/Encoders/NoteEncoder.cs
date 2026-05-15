using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;

/// <summary>
///     Encodes a <see cref="NoteRecord" /> (NOTE) as PC-format subrecord bytes.
///     Holotapes / written notes / recordings / images. NoteType decides which content
///     subrecord is meaningful.
///     fopdoc canonical order: EDID, FULL?, MODL?, ICON?, MICO?, YNAM?, DATA(1B note type),
///     TNAM (variable: 4B FormID when NoteType=Voice/topic ref, else string body),
///     SNAM?(sound FormID), ONAM?(linked object FormID).
/// </summary>
public sealed class NoteEncoder : IRecordEncoder
{
    public string RecordType => "NOTE";
    public Type ModelType => typeof(NoteRecord);

    public EncodedRecord Encode(object model)
    {
        return new EncodedRecord { Subrecords = [], Warnings = [] };
    }

    internal static EncodedRecord EncodeNew(NoteRecord note)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(note.EditorId))
        {
            warnings.Add($"New NOTE 0x{note.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", note.EditorId ?? string.Empty));

        if (!string.IsNullOrEmpty(note.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", note.FullName));
        }

        if (!string.IsNullOrEmpty(note.ModelPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MODL", note.ModelPath));
        }

        if (!string.IsNullOrEmpty(note.IconPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("ICON", note.IconPath));
        }

        if (!string.IsNullOrEmpty(note.TexturePath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MICO", note.TexturePath));
        }

        subs.Add(NewRecordSubrecords.EncodeByteSubrecord("DATA", note.NoteType));

        // TNAM is type-dependent: Voice notes (type 3) and audio-linked content point to a
        // DIAL topic via FormID; Text/Image notes carry the body string directly. Prefer
        // FormID when the model carries a TopicFormId; fall back to the Text body otherwise.
        if (note.TopicFormId.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("TNAM", note.TopicFormId.Value));
        }
        else if (!string.IsNullOrEmpty(note.Text))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("TNAM", note.Text));
        }

        if (note.SoundFormId.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("SNAM", note.SoundFormId.Value));
        }

        if (note.ObjectFormId.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("ONAM", note.ObjectFormId.Value));
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
