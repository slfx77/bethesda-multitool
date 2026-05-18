using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;

/// <summary>
///     Encodes an <see cref="AudioLocationControllerRecord" /> (ALOC) as PC-format subrecord bytes.
///     fopdoc canonical order: EDID, FULL?, NAM1?(flags), NAM2?(media flags), NAM3(location delay),
///     NAM4(layer time), NAM5(loop time), NAM6(media start time).
///     Our model captures the four uint32 timing fields; flags subrecords are not modeled.
///     Override path is a no-op; master ESM bytes retained verbatim.
/// </summary>
public sealed class AlocEncoder : IRecordEncoder
{
    public string RecordType => "ALOC";
    public Type ModelType => typeof(AudioLocationControllerRecord);

    public EncodedRecord Encode(object model)
    {
        return new EncodedRecord { Subrecords = [], Warnings = [] };
    }

    internal static EncodedRecord EncodeNew(AudioLocationControllerRecord aloc)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(aloc.EditorId))
        {
            warnings.Add($"New ALOC 0x{aloc.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", aloc.EditorId ?? string.Empty));

        if (!string.IsNullOrEmpty(aloc.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", aloc.FullName));
        }

        subs.Add(NewRecordSubrecords.EncodeUInt32Subrecord("NAM3", aloc.LocationDelay));
        subs.Add(NewRecordSubrecords.EncodeUInt32Subrecord("NAM4", aloc.LayerTime));
        subs.Add(NewRecordSubrecords.EncodeUInt32Subrecord("NAM5", aloc.LoopTime));
        subs.Add(NewRecordSubrecords.EncodeUInt32Subrecord("NAM6", aloc.MediaStartTime));

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
