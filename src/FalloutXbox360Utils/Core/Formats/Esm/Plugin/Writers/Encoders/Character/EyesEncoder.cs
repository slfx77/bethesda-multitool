using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Character;

/// <summary>
///     Encodes an <see cref="EyesRecord" /> (EYES) as PC-format subrecord bytes.
///     Eye types available for NPC character generation.
///     fopdoc canonical order: EDID, FULL?, ICON?, DATA(1B flags — bit 0 = Playable).
/// </summary>
public sealed class EyesEncoder : IRecordEncoder
{
    public string RecordType => "EYES";
    public Type ModelType => typeof(EyesRecord);

    internal static EncodedRecord EncodeNew(EyesRecord eyes)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(eyes.EditorId))
        {
            warnings.Add($"New EYES 0x{eyes.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", eyes.EditorId ?? string.Empty));

        if (!string.IsNullOrEmpty(eyes.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", eyes.FullName));
        }

        if (!string.IsNullOrEmpty(eyes.TexturePath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("ICON", eyes.TexturePath));
        }

        subs.Add(NewRecordSubrecords.EncodeByteSubrecord("DATA", eyes.Flags));

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
