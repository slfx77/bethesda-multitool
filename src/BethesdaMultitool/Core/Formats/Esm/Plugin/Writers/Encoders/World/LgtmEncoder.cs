using BethesdaMultitool.Core.Formats.Esm.Conversion.Schema;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.World;

/// <summary>
///     Encodes a <see cref="LightingTemplateRecord" /> (LGTM) as PC-format subrecord bytes.
///     Lighting templates store the DATA(40B FO3/FNV, 92B Skyrim) typed fields as a
///     schema-parsed dictionary —
///     this encoder re-serializes via the schema.
///     Canonical order: EDID, DATA.
/// </summary>
public sealed class LgtmEncoder : IRecordEncoder
{
    public string RecordType => "LGTM";
    public Type ModelType => typeof(LightingTemplateRecord);

    internal static EncodedRecord EncodeNew(LightingTemplateRecord lgtm)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(lgtm.EditorId))
        {
            warnings.Add($"New LGTM 0x{lgtm.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", lgtm.EditorId ?? string.Empty));

        if (lgtm.LightingData is not null)
        {
            var dataLength = lgtm.LightingData.ContainsKey("FogColorFar") ? 92 : 40;
            var schema = SubrecordSchemaRegistry.GetSchema("DATA", "LGTM", dataLength);
            if (schema is not null)
            {
                subs.Add(new EncodedSubrecord("DATA",
                    SchemaDictionarySerializer.Serialize(schema, lgtm.LightingData)));
            }
            else
            {
                warnings.Add(
                    $"New LGTM 0x{lgtm.FormId:X8} schema lookup failed — emitting zero-filled DATA.");
                subs.Add(new EncodedSubrecord("DATA", new byte[dataLength]));
            }
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
