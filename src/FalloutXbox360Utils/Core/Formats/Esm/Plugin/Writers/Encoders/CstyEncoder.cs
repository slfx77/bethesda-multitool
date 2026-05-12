using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.AI;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;

/// <summary>
///     Encodes a <see cref="CombatStyleRecord" /> (CSTY) as PC-format subrecord bytes.
///     Combat style parsers store the typed fields as `Dictionary&lt;string, object?&gt;` —
///     this encoder re-serializes those dictionaries back to bytes via the schema definitions.
///     fopdoc canonical order: EDID, CSTD(92B), CSAD(84B = 21 floats), CSSD(64B).
///     Missing dictionary fields are zero-filled.
/// </summary>
public sealed class CstyEncoder : IRecordEncoder
{
    public string RecordType => "CSTY";
    public Type ModelType => typeof(CombatStyleRecord);

    public EncodedRecord Encode(object model)
    {
        return new EncodedRecord { Subrecords = [], Warnings = [] };
    }

    internal static EncodedRecord EncodeNew(CombatStyleRecord csty)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(csty.EditorId))
        {
            warnings.Add($"New CSTY 0x{csty.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", csty.EditorId ?? string.Empty));

        if (csty.StyleData is not null)
        {
            var cstdSchema = SubrecordSchemaRegistry.GetSchema("CSTD", "", 92);
            if (cstdSchema is not null)
            {
                subs.Add(new EncodedSubrecord("CSTD",
                    SchemaDictionarySerializer.Serialize(cstdSchema, csty.StyleData)));
            }
        }

        if (csty.AdvancedData is not null)
        {
            // CSAD is registered as `SubrecordSchema.FloatArray` — 21 floats per fopdoc, 84B total.
            // FloatArray schemas have ExpectedSize=-1 (repeating), so build manually from the
            // dictionary's "Value" entries.
            subs.Add(new EncodedSubrecord("CSAD", BuildFloatArrayFromDict(csty.AdvancedData, 21)));
        }

        if (csty.SimpleData is not null)
        {
            var cssdSchema = SubrecordSchemaRegistry.GetSchema("CSSD", "", 64);
            if (cssdSchema is not null)
            {
                subs.Add(new EncodedSubrecord("CSSD",
                    SchemaDictionarySerializer.Serialize(cssdSchema, csty.SimpleData)));
            }
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    /// <summary>
    ///     CSAD is a repeating float array (schema marks it as <see cref="SubrecordSchema.FloatArray" />).
    ///     The parser stores the parsed floats keyed as "Value0", "Value1", ... or as a single
    ///     "Value" entry holding a float[]. Handle both shapes; pad with zeros to the expected
    ///     count.
    /// </summary>
    private static byte[] BuildFloatArrayFromDict(IReadOnlyDictionary<string, object?> values, int floatCount)
    {
        var bytes = new byte[floatCount * 4];

        // Some parsers store the whole array under a single "Value" key.
        if (values.TryGetValue("Value", out var maybeArr) && maybeArr is float[] arr)
        {
            for (var i = 0; i < Math.Min(arr.Length, floatCount); i++)
            {
                SubrecordEncoder.WriteFloat(bytes, i * 4, arr[i]);
            }

            return bytes;
        }

        // Otherwise look for "Value0", "Value1", ... keys.
        for (var i = 0; i < floatCount; i++)
        {
            if (values.TryGetValue($"Value{i}", out var v) && v is float f)
            {
                SubrecordEncoder.WriteFloat(bytes, i * 4, f);
            }
        }

        return bytes;
    }
}
