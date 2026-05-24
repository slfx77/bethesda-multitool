using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;

/// <summary>
///     Encodes a <see cref="StaticRecord" /> (STAT) as PC-format subrecord bytes.
///     Emits the full record from scratch: EDID + OBND? + MODL?. There is no DATA
///     subrecord — STAT is the simplest world-object type. The override path is a no-op
///     (the master ESM's bytes are retained verbatim).
/// </summary>
public sealed class StatEncoder : IRecordEncoder
{
    public string RecordType => "STAT";
    public Type ModelType => typeof(StaticRecord);

    /// <summary>
    ///     Encode a new STAT record from scratch in fopdoc canonical order: EDID, OBND, MODL.
    /// </summary>
    internal static EncodedRecord EncodeNew(StaticRecord stat)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(stat.EditorId))
        {
            warnings.Add($"New STAT 0x{stat.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", stat.EditorId ?? string.Empty));

        if (stat.Bounds is not null)
        {
            subs.Add(NewRecordSubrecords.EncodeObndSubrecord(stat.Bounds));
        }

        if (!string.IsNullOrEmpty(stat.ModelPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MODL", stat.ModelPath));
        }
        else
        {
            warnings.Add($"New STAT 0x{stat.FormId:X8} has no model path — record will not render in-game.");
        }

        if (stat.TextureHashData is { Length: > 0 } modt)
        {
            subs.Add(NewRecordSubrecords.EncodeByteArraySubrecord("MODT", modt));
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
