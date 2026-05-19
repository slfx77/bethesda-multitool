using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

/// <summary>
///     Encodes an <see cref="ImpactDataRecord" /> (IPCT) as PC-format subrecord bytes.
///     fopdoc canonical order: EDID, MODL?, MODT?, DATA(16B: effect+angle+placementRadius+
///     soundLevel+flags), DODT?(36B decal data), DNAM?(decal texture set FormID),
///     SNAM?(primary sound), NAM1?(secondary sound).
///     Our model only captures MODL + decal texture set + 2 sound FormIDs; DATA/DODT details
///     aren't modeled. Emit minimal subrecords + warn that DATA/DODT are zeroed.
///     Override path is a no-op; master ESM bytes retained verbatim.
/// </summary>
public sealed class IpctEncoder : IRecordEncoder
{
    public string RecordType => "IPCT";
    public Type ModelType => typeof(ImpactDataRecord);

    public EncodedRecord Encode(object model)
    {
        return new EncodedRecord { Subrecords = [], Warnings = [] };
    }

    internal static EncodedRecord EncodeNew(ImpactDataRecord ipct)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(ipct.EditorId))
        {
            warnings.Add($"New IPCT 0x{ipct.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", ipct.EditorId ?? string.Empty));

        if (!string.IsNullOrEmpty(ipct.ModelPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MODL", ipct.ModelPath));
        }

        // DATA: 16B (effect dur + angle + placement-radius + sound-level + flags). Not modeled — zero.
        subs.Add(new EncodedSubrecord("DATA", new byte[16]));
        warnings.Add(
            $"New IPCT 0x{ipct.FormId:X8}: DATA/DODT detailed fields not modeled — emitted with zeroes.");

        if (ipct.DecalTextureSetFormId != 0)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("DNAM", ipct.DecalTextureSetFormId));
        }

        if (ipct.Sound1FormId != 0)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("SNAM", ipct.Sound1FormId));
        }

        if (ipct.Sound2FormId != 0)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("NAM1", ipct.Sound2FormId));
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
