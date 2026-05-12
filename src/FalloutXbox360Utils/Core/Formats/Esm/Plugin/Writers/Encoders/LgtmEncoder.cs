using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;

/// <summary>
///     Encodes a <see cref="LightingTemplateRecord" /> (LGTM) as PC-format subrecord bytes.
///     Lighting templates store the DATA(40B) typed fields as a schema-parsed dictionary —
///     this encoder re-serializes via the schema.
///     fopdoc canonical order: EDID, DATA(40B: ambient/directional/fog colors + fog near/far +
///     directional rotation + fog clip distance + fog power).
/// </summary>
public sealed class LgtmEncoder : IRecordEncoder
{
    public string RecordType => "LGTM";
    public Type ModelType => typeof(LightingTemplateRecord);

    public EncodedRecord Encode(object model)
    {
        return new EncodedRecord { Subrecords = [], Warnings = [] };
    }

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
            var schema = SubrecordSchemaRegistry.GetSchema("DATA", "LGTM", 40);
            if (schema is not null)
            {
                subs.Add(new EncodedSubrecord("DATA",
                    SchemaDictionarySerializer.Serialize(schema, lgtm.LightingData)));
            }
            else
            {
                warnings.Add(
                    $"New LGTM 0x{lgtm.FormId:X8} schema lookup failed — emitting zero-filled DATA.");
                subs.Add(new EncodedSubrecord("DATA", new byte[40]));
            }
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
