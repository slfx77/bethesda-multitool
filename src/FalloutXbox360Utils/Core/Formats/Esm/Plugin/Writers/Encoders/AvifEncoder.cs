using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;

/// <summary>
///     Encodes an <see cref="ActorValueInfoRecord" /> (AVIF) as PC-format subrecord bytes.
///     Defines a stat, skill, or attribute (Strength, Guns, Action Points, etc.).
///     fopdoc canonical order: EDID, FULL?, DESC?, ICON?, ANAM? (abbreviation).
/// </summary>
public sealed class AvifEncoder : IRecordEncoder
{
    public string RecordType => "AVIF";
    public Type ModelType => typeof(ActorValueInfoRecord);

    public EncodedRecord Encode(object model)
    {
        return new EncodedRecord { Subrecords = [], Warnings = [] };
    }

    internal static EncodedRecord EncodeNew(ActorValueInfoRecord avif)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(avif.EditorId))
        {
            warnings.Add($"New AVIF 0x{avif.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", avif.EditorId ?? string.Empty));

        if (!string.IsNullOrEmpty(avif.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", avif.FullName));
        }

        if (!string.IsNullOrEmpty(avif.Description))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("DESC", avif.Description));
        }

        if (!string.IsNullOrEmpty(avif.Icon))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("ICON", avif.Icon));
        }

        if (!string.IsNullOrEmpty(avif.Abbreviation))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("ANAM", avif.Abbreviation));
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
