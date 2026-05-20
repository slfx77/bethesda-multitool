using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Reference;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Item;

/// <summary>
///     Encodes a <see cref="LeveledListRecord" /> as PC-format subrecord bytes.
///     One encoder handles all three record signatures (LVLI/LVLN/LVLC) — the on-disk
///     wire format is identical; only the record-level signature differs. The PluginBuilder
///     switch routes all three signatures to this encoder.
///     fopdoc canonical order: EDID, LVLD?(1B ChanceNone), LVLF?(1B flags), LVLG?(FormID glob),
///     LVLO* (12B entry: uint16 Level + pad(2) + FormID + uint16 Count + pad(2)).
/// </summary>
public sealed class LvliEncoder : IRecordEncoder
{
    public string RecordType => "LVLI"; // Registry key — also handles LVLN/LVLC via dispatch.
    public Type ModelType => typeof(LeveledListRecord);

    public EncodedRecord Encode(object model)
    {
        return new EncodedRecord { Subrecords = [], Warnings = [] };
    }

    /// <summary>
    ///     Encode a new LVLI/LVLN/LVLC record. Drop LVLO entries whose target FormID
    ///     dangles in master ∪ emitted (engine logs "Unable to find Leveled Object Form (X)
    ///     for owner object (Y)" and removes the entry anyway, which silently breaks loot
    ///     tables). Remap via the runtime→emitted alias table when possible.
    /// </summary>
    internal static EncodedRecord EncodeNew(
        LeveledListRecord lvli,
        IReadOnlySet<uint>? validFormIds = null,
        IReadOnlyDictionary<uint, uint>? remapTable = null)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(lvli.EditorId))
        {
            warnings.Add(
                $"New {lvli.ListType} 0x{lvli.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", lvli.EditorId ?? string.Empty));

        if (lvli.ChanceNone != 0)
        {
            subs.Add(NewRecordSubrecords.EncodeByteSubrecord("LVLD", lvli.ChanceNone));
        }

        if (lvli.Flags != 0)
        {
            subs.Add(NewRecordSubrecords.EncodeByteSubrecord("LVLF", lvli.Flags));
        }

        if (lvli.GlobalFormId.HasValue)
        {
            var resolved = FormIdReferenceResolver.Resolve(
                lvli.GlobalFormId.Value, validFormIds, remapTable);
            if (resolved.HasValue)
            {
                subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("LVLG", resolved.Value));
            }
        }

        var droppedEntries = 0;
        foreach (var entry in lvli.Entries)
        {
            var resolvedEntry = FormIdReferenceResolver.Resolve(
                entry.FormId, validFormIds, remapTable);
            if (!resolvedEntry.HasValue)
            {
                droppedEntries++;
                continue;
            }
            subs.Add(new EncodedSubrecord("LVLO", BuildLvloSubrecord(entry, resolvedEntry.Value)));
        }
        if (droppedEntries > 0)
        {
            warnings.Add(
                $"New {lvli.ListType} 0x{lvli.FormId:X8} dropped {droppedEntries} LVLO entry/entries " +
                "with dangling FormID (engine 'Unable to find Leveled Object Form').");
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    private static byte[] BuildLvloSubrecord(LeveledEntry entry, uint resolvedFormId)
    {
        // LVLO (12 bytes): uint16 Level(0) + pad(2..3) + FormID Entry(4..7) +
        //                  uint16 Count(8) + pad(10..11).
        var data = new byte[12];
        SubrecordEncoder.WriteUInt16(data, 0, entry.Level);
        // bytes 2-3 padding
        SubrecordEncoder.WriteFormId(data, 4, resolvedFormId);
        SubrecordEncoder.WriteUInt16(data, 8, entry.Count);
        // bytes 10-11 padding
        return data;
    }
}
