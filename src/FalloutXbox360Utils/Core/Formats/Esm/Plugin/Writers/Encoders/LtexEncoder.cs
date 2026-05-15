using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;

/// <summary>
///     Encodes a Landscape Texture (LTEX) record for LAND texture-layer references.
/// </summary>
public sealed class LtexEncoder : IRecordEncoder
{
    public string RecordType => "LTEX";

    public Type ModelType => typeof(LandscapeTextureRecord);

    public EncodedRecord Encode(object model)
    {
        return new EncodedRecord { Subrecords = [], Warnings = [] };
    }

    internal static EncodedRecord EncodeNew(LandscapeTextureRecord ltex)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(ltex.EditorId))
        {
            warnings.Add($"New LTEX 0x{ltex.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", ltex.EditorId ?? string.Empty));

        if (!string.IsNullOrEmpty(ltex.IconPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("ICON", ltex.IconPath));
        }

        if (!string.IsNullOrEmpty(ltex.SmallIconPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MICO", ltex.SmallIconPath));
        }

        if (ltex.TextureSetFormId is > 0)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("TNAM", ltex.TextureSetFormId.Value));
        }

        if (ltex.HavokData is { Length: > 0 })
        {
            subs.Add(NewRecordSubrecords.EncodeByteArraySubrecord("HNAM", ltex.HavokData));
        }

        if (ltex.SpecularData is { Length: > 0 })
        {
            subs.Add(NewRecordSubrecords.EncodeByteArraySubrecord("SNAM", ltex.SpecularData));
        }

        foreach (var grassFormId in ltex.GrassFormIds)
        {
            if (grassFormId != 0)
            {
                subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("GNAM", grassFormId));
            }
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
