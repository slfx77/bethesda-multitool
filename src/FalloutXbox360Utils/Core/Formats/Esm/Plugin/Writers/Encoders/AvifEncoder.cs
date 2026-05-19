using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;

/// <summary>
///     Encodes an <see cref="ActorValueInfoRecord" /> (AVIF) as PC-format subrecord bytes.
///     Defines a stat, skill, or attribute (Strength, Guns, Action Points, etc.).
///     fopdoc canonical order: EDID, FULL?, DESC?, ICON?, ANAM? (abbreviation).
/// </summary>
public sealed class AvifEncoder : IRecordEncoder
{
    public string RecordType => "AVIF";
    public Type ModelType => typeof(ActorValueInfoRecord);

    public EncodedRecord Encode(object model)
    {
        return new EncodedRecord { Subrecords = [], Warnings = [] };
    }

    internal static EncodedRecord EncodeNew(ActorValueInfoRecord avif)
    {
        // Actor values in FNV are engine-hardcoded — AVIF records define metadata for
        // built-in stats (Strength, Guns, Health, AVEmpResist, AVVariable02-10, etc.).
        // The vanilla AVIF set already covers every engine AV; new ones can't be created
        // because the engine's AV table is fixed. The DMP parser sees AVIF references in
        // captured memory but their FormIDs don't match the master (these are engine-
        // internal records with no on-disk equivalent), so the converter classifies them
        // as "new" and tries to emit them. Doing so with only an EDID subrecord and no
        // FULL/DESC/ANAM crashes the engine at FalloutNV+0x46025A during plugin load.
        //
        // The safe answer is to never emit AVIF as a new record. Vanilla AVIF records
        // pass through the override path verbatim from master, so we don't lose anything.
        return new EncodedRecord
        {
            Subrecords = [],
            Warnings =
            [
                $"Skipping AVIF 0x{avif.FormId:X8} ({avif.EditorId ?? "no-EditorId"}) — actor values " +
                "are engine-hardcoded; emitting new AVIF crashes the FNV runtime."
            ]
        };
    }
}
