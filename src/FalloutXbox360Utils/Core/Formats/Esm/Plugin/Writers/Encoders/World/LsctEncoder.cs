using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.AI;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;

/// <summary>
///     Encodes a <see cref="LoadScreenTypeRecord" /> (LSCT) as PC-format subrecord bytes.
///     LSCT carries a single 88-byte DATA block whose typed fields the parser stores as
///     <see cref="Dictionary{TKey,TValue}" />. This encoder re-serializes that dictionary
///     back to bytes via <see cref="SchemaDictionarySerializer" />, mirroring how
///     <see cref="CstyEncoder" />/<see cref="LgtmEncoder" /> handle their schema-dict subrecords.
///     fopdoc canonical order: EDID, DATA(88B schema dict).
///     Override path is a no-op; master ESM bytes retained verbatim.
/// </summary>
public sealed class LsctEncoder : IRecordEncoder
{
    public string RecordType => "LSCT";
    public Type ModelType => typeof(LoadScreenTypeRecord);

    public EncodedRecord Encode(object model)
    {
        return new EncodedRecord { Subrecords = [], Warnings = [] };
    }

    internal static EncodedRecord EncodeNew(LoadScreenTypeRecord lsct)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(lsct.EditorId))
        {
            warnings.Add($"New LSCT 0x{lsct.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", lsct.EditorId ?? string.Empty));

        if (lsct.LayoutData is not null)
        {
            var schema = SubrecordSchemaRegistry.GetSchema("DATA", "LSCT", 88);
            if (schema is not null)
            {
                subs.Add(new EncodedSubrecord("DATA",
                    SchemaDictionarySerializer.Serialize(schema, lsct.LayoutData)));
            }
            else
            {
                warnings.Add(
                    $"New LSCT 0x{lsct.FormId:X8}: DATA schema not registered — DATA subrecord omitted.");
            }
        }
        else
        {
            warnings.Add(
                $"New LSCT 0x{lsct.FormId:X8} has no layout data — DATA subrecord omitted.");
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
