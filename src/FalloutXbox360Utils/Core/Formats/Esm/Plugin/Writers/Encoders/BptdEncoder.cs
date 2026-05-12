using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;

/// <summary>
///     Encodes a <see cref="BodyPartDataRecord" /> (BPTD) as PC-format subrecord bytes.
///     Defines body part hit zones for dismemberment / VATS targeting.
///     fopdoc canonical order: EDID, MODL?, (BPTN + BPNN)*, NAM5?(uint32 texture count).
///     Per-part data (BPDT, BPND, BNAM, NAM1, etc.) is not in the current model — deferred.
/// </summary>
public sealed class BptdEncoder : IRecordEncoder
{
    public string RecordType => "BPTD";
    public Type ModelType => typeof(BodyPartDataRecord);

    public EncodedRecord Encode(object model)
    {
        return new EncodedRecord { Subrecords = [], Warnings = [] };
    }

    internal static EncodedRecord EncodeNew(BodyPartDataRecord bptd)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(bptd.EditorId))
        {
            warnings.Add($"New BPTD 0x{bptd.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", bptd.EditorId ?? string.Empty));

        if (!string.IsNullOrEmpty(bptd.ModelPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MODL", bptd.ModelPath));
        }

        // BPTN (part name) and BPNN (node name) come in matched pairs per body part.
        // Iterate the shorter list to be safe; warn if they're mismatched.
        var partCount = Math.Min(bptd.PartNames.Count, bptd.NodeNames.Count);
        if (bptd.PartNames.Count != bptd.NodeNames.Count)
        {
            warnings.Add(
                $"New BPTD 0x{bptd.FormId:X8} has {bptd.PartNames.Count} part names vs {bptd.NodeNames.Count} node names — emitting matched prefix only.");
        }

        for (var i = 0; i < partCount; i++)
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("BPTN", bptd.PartNames[i]));
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("BPNN", bptd.NodeNames[i]));
        }

        if (bptd.TextureCount != 0)
        {
            subs.Add(NewRecordSubrecords.EncodeUInt32Subrecord("NAM5", bptd.TextureCount));
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
