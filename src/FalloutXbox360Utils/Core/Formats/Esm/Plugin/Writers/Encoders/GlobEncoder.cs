using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;

/// <summary>
///     Encodes a <see cref="GlobalRecord" /> as PC-format GLOB subrecord bytes.
///     For overrides we only emit FLTV (the runtime-mutable value); EDID and FNAM are retained
///     verbatim from the source ESM by the merge engine.
/// </summary>
public sealed class GlobEncoder : IRecordEncoder
{
    public string RecordType => "GLOB";
    public Type ModelType => typeof(GlobalRecord);

    public EncodedRecord Encode(object model)
    {
        var glob = (GlobalRecord)model;

        var fltv = new byte[4];
        SubrecordEncoder.WriteFloat(fltv, 0, glob.Value);

        return new EncodedRecord
        {
            Subrecords = [new EncodedSubrecord("FLTV", fltv)],
            Warnings = []
        };
    }

    /// <summary>
    ///     Encode a new GLOB record from scratch. fopdoc canonical order: EDID, FNAM, FLTV.
    ///     FNAM is a single byte indicating value type ('s'=short, 'l'=long, 'f'=float).
    /// </summary>
    internal static EncodedRecord EncodeNew(GlobalRecord glob)
    {
        var subs = new List<EncodedSubrecord>(3);
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(glob.EditorId))
        {
            warnings.Add($"New GLOB 0x{glob.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", glob.EditorId ?? string.Empty));
        subs.Add(NewRecordSubrecords.EncodeByteSubrecord("FNAM", (byte)glob.ValueType));
        subs.Add(NewRecordSubrecords.EncodeFloatSubrecord("FLTV", glob.Value));

        return new EncodedRecord
        {
            Subrecords = subs,
            Warnings = warnings
        };
    }
}
