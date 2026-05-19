using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;

/// <summary>
///     Encodes a <see cref="DoorRecord" /> (DOOR) as PC-format subrecord bytes.
///     Emits the full record from scratch: EDID + OBND? + FULL? + MODL? + SCRI? +
///     SNAM? + ANAM? + BNAM? + FNAM(1B). DOOR has no DATA subrecord; FNAM is a single
///     byte of flags (auto-open, hidden, minimal-use, etc.). The override path is a no-op.
/// </summary>
public sealed class DoorEncoder : IRecordEncoder
{
    public string RecordType => "DOOR";
    public Type ModelType => typeof(DoorRecord);

    public EncodedRecord Encode(object model)
    {
        return new EncodedRecord { Subrecords = [], Warnings = [] };
    }

    /// <summary>
    ///     Encode a new DOOR record from scratch in fopdoc canonical order:
    ///     EDID, OBND, FULL, MODL, SCRI, SNAM, ANAM, BNAM, FNAM.
    /// </summary>
    internal static EncodedRecord EncodeNew(DoorRecord door)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(door.EditorId))
        {
            warnings.Add($"New DOOR 0x{door.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", door.EditorId ?? string.Empty));

        if (door.Bounds is not null)
        {
            subs.Add(NewRecordSubrecords.EncodeObndSubrecord(door.Bounds));
        }

        if (!string.IsNullOrEmpty(door.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", door.FullName));
        }

        if (!string.IsNullOrEmpty(door.ModelPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MODL", door.ModelPath));
        }

        if (door.TextureHashData is { Length: > 0 } modt)
        {
            subs.Add(NewRecordSubrecords.EncodeByteArraySubrecord("MODT", modt));
        }

        if (door.Script.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("SCRI", door.Script.Value));
        }

        if (door.OpenSoundFormId.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("SNAM", door.OpenSoundFormId.Value));
        }

        if (door.CloseSoundFormId.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("ANAM", door.CloseSoundFormId.Value));
        }

        if (door.LoopSoundFormId.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("BNAM", door.LoopSoundFormId.Value));
        }

        // FNAM is a single byte of flags — emit even when 0 since it's the door's behavior switch.
        subs.Add(NewRecordSubrecords.EncodeByteSubrecord("FNAM", door.Flags));

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
