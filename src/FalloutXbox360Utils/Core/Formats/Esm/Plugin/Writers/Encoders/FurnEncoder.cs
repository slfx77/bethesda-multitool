using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;

/// <summary>
///     Encodes a <see cref="FurnitureRecord" /> (FURN) as PC-format subrecord bytes.
///     v7 emits the full record from scratch: EDID + OBND? + FULL? + MODL? + SCRI? + MNAM(4B).
///     The override path is a no-op — FURN carries no runtime-mutable bytes that we mirror.
///     MNAM is the marker-flags bitmask indicating which sit-positions are active.
/// </summary>
public sealed class FurnEncoder : IRecordEncoder
{
    public string RecordType => "FURN";
    public Type ModelType => typeof(FurnitureRecord);

    public EncodedRecord Encode(object model)
    {
        return new EncodedRecord { Subrecords = [], Warnings = [] };
    }

    /// <summary>
    ///     Encode a new FURN record from scratch in fopdoc canonical order:
    ///     EDID, OBND, FULL, MODL, SCRI, MNAM.
    /// </summary>
    internal static EncodedRecord EncodeNew(FurnitureRecord furn)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(furn.EditorId))
        {
            warnings.Add($"New FURN 0x{furn.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", furn.EditorId ?? string.Empty));

        if (furn.Bounds is not null)
        {
            subs.Add(NewRecordSubrecords.EncodeObndSubrecord(furn.Bounds));
        }

        if (!string.IsNullOrEmpty(furn.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", furn.FullName));
        }

        if (!string.IsNullOrEmpty(furn.ModelPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MODL", furn.ModelPath));
        }

        if (furn.Script.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("SCRI", furn.Script.Value));
        }

        // MNAM is always emitted — even MarkerFlags=0 is a meaningful FURN attribute.
        subs.Add(NewRecordSubrecords.EncodeUInt32Subrecord("MNAM", furn.MarkerFlags));

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
