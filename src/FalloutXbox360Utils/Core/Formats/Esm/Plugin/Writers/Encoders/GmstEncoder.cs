using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;

/// <summary>
///     Encodes a <see cref="GameSettingRecord" /> as PC-format GMST subrecord bytes.
///     For overrides we only emit DATA (the runtime-mutable value); EDID is retained verbatim
///     from the source ESM by the merge engine. String GMSTs are skipped — the runtime model
///     does not always carry the string value, so we conservatively retain the ESM's DATA.
/// </summary>
public sealed class GmstEncoder : IRecordEncoder
{
    public string RecordType => "GMST";
    public Type ModelType => typeof(GameSettingRecord);

    public EncodedRecord Encode(object model)
    {
        var gmst = (GameSettingRecord)model;
        var warnings = new List<string>();

        switch (gmst.ValueType)
        {
            case GameSettingType.Float when gmst.FloatValue.HasValue:
            {
                var data = new byte[4];
                SubrecordEncoder.WriteFloat(data, 0, gmst.FloatValue.Value);
                return new EncodedRecord
                {
                    Subrecords = [new EncodedSubrecord("DATA", data)],
                    Warnings = warnings
                };
            }

            case GameSettingType.Integer when gmst.IntValue.HasValue:
            case GameSettingType.Boolean when gmst.IntValue.HasValue:
            {
                var data = new byte[4];
                SubrecordEncoder.WriteInt32(data, 0, gmst.IntValue.Value);
                return new EncodedRecord
                {
                    Subrecords = [new EncodedSubrecord("DATA", data)],
                    Warnings = warnings
                };
            }

            case GameSettingType.String:
                warnings.Add($"GMST {gmst.EditorId} is a string — DATA retained from ESM in v1.");
                break;

            default:
                warnings.Add($"GMST {gmst.EditorId} has no usable value of type {gmst.ValueType}.");
                break;
        }

        return new EncodedRecord
        {
            Subrecords = [],
            Warnings = warnings
        };
    }

    /// <summary>
    ///     Encode a new GMST record from scratch (not an override). Subrecord order is EDID,
    ///     DATA per fopdoc. String GMSTs are skipped (model inconsistency carries over from
    ///     v1; defer to v5).
    /// </summary>
    internal static EncodedRecord EncodeNew(GameSettingRecord gmst)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(gmst.EditorId))
        {
            warnings.Add($"New GMST 0x{gmst.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", gmst.EditorId ?? string.Empty));

        switch (gmst.ValueType)
        {
            case GameSettingType.Float when gmst.FloatValue.HasValue:
                subs.Add(NewRecordSubrecords.EncodeFloatSubrecord("DATA", gmst.FloatValue.Value));
                break;

            case GameSettingType.Integer when gmst.IntValue.HasValue:
            case GameSettingType.Boolean when gmst.IntValue.HasValue:
                subs.Add(NewRecordSubrecords.EncodeInt32Subrecord("DATA", gmst.IntValue.Value));
                break;

            case GameSettingType.String:
                warnings.Add($"New GMST {gmst.EditorId} is a string type — DATA omitted in v4.");
                break;

            default:
                warnings.Add($"New GMST {gmst.EditorId} has no usable value of type {gmst.ValueType} — DATA omitted.");
                break;
        }

        return new EncodedRecord
        {
            Subrecords = subs,
            Warnings = warnings
        };
    }
}
