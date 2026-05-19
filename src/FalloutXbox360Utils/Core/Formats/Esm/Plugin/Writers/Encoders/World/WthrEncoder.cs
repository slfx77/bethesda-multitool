using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;

/// <summary>
///     Encodes a <see cref="WeatherRecord" /> (WTHR) as PC-format subrecord bytes.
///     The current model is intentionally narrow — sky/fog/cloud color arrays and most of
///     the visual data are not yet parsed. This encoder emits the typed fields that DO
///     exist (EDID, INAM image space modifier, SNAM* sound entries). Records emitted this
///     way will be GECK-loadable but lose all visual configuration; we warn explicitly.
///     fopdoc canonical (full) order: EDID, DNAM/CNAM/ANAM/BNAM colors, FNAM fog distances,
///     PNAM cloud colors, ONAM cloud speeds, NAM0 (4 cloud textures), INAM (image space),
///     DATA (15B wind+trans), SNAM*(sound + type).
/// </summary>
public sealed class WthrEncoder : IRecordEncoder
{
    public string RecordType => "WTHR";
    public Type ModelType => typeof(WeatherRecord);

    public EncodedRecord Encode(object model)
    {
        return new EncodedRecord { Subrecords = [], Warnings = [] };
    }

    internal static EncodedRecord EncodeNew(WeatherRecord wthr)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(wthr.EditorId))
        {
            warnings.Add($"New WTHR 0x{wthr.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        warnings.Add(
            $"New WTHR 0x{wthr.FormId:X8} — visual subrecords (DNAM/CNAM/ANAM/BNAM/FNAM/PNAM/ONAM/NAM0/DATA) " +
            "are not modeled and will be missing. The output record will load but render without " +
            "color/fog/cloud configuration.");

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", wthr.EditorId ?? string.Empty));

        if (wthr.ImageSpaceModifier.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("INAM", wthr.ImageSpaceModifier.Value));
        }

        foreach (var sound in wthr.Sounds)
        {
            // SNAM (8B): FormID Sound + uint32 Type.
            var snam = new byte[8];
            SubrecordEncoder.WriteFormId(snam, 0, sound.SoundFormId);
            SubrecordEncoder.WriteUInt32(snam, 4, sound.Type);
            subs.Add(new EncodedSubrecord("SNAM", snam));
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
