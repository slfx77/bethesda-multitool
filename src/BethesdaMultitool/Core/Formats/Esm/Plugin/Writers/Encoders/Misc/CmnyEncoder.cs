using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

/// <summary>
///     Encodes a <see cref="CaravanMoneyRecord" /> (CMNY) as PC-format subrecord bytes.
/// </summary>
public sealed class CmnyEncoder : IRecordEncoder
{
    public string RecordType => "CMNY";
    public Type ModelType => typeof(CaravanMoneyRecord);

    /// <summary>Produces override subrecords for an existing CMNY (a caravan-money record) from its runtime-mutable fields.</summary>
    public EncodedRecord Encode(object model)
    {
        return EncodeNew((CaravanMoneyRecord)model);
    }

    internal static EncodedRecord EncodeNew(CaravanMoneyRecord cmny)
    {
        var subs = new List<EncodedSubrecord>
        {
            NewRecordSubrecords.EncodeStringSubrecord("EDID", cmny.EditorId ?? string.Empty),
            NewRecordSubrecords.EncodeUInt32Subrecord("DATA", cmny.Value)
        };

        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(cmny.EditorId))
        {
            warnings.Add($"New CMNY 0x{cmny.FormId:X8} has no EditorId - emitting empty EDID.");
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
