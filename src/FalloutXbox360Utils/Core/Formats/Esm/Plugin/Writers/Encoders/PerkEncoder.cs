using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Magic;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;

/// <summary>
///     Encodes a <see cref="PerkRecord" /> (PERK) as PC-format subrecord bytes.
///     fopdoc canonical order: EDID, FULL?, DESC?, ICON?, DATA(5B), CTDA*(top-level),
///         then per perk entry: PRKE + DATA(small) + (CTDA*) + EPFT + (EPFD or EPF2 or EPF3)? + PRKF.
///     DATA layout (5B): byte Trait + byte MinLevel + byte Ranks + byte Playable + byte HiddenFromPC.
///     Top-level CTDA conditions are emitted; per-PRKE-entry chains are NOT emitted (the model
///     captures them but the on-disk PRKE/PRKC layout is complex enough that v17 defers it).
/// </summary>
public sealed class PerkEncoder : IRecordEncoder
{
    public string RecordType => "PERK";
    public Type ModelType => typeof(PerkRecord);

    public EncodedRecord Encode(object model)
    {
        return new EncodedRecord { Subrecords = [], Warnings = [] };
    }

    internal static EncodedRecord EncodeNew(PerkRecord perk)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(perk.EditorId))
        {
            warnings.Add($"New PERK 0x{perk.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", perk.EditorId ?? string.Empty));

        if (!string.IsNullOrEmpty(perk.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", perk.FullName));
        }

        if (!string.IsNullOrEmpty(perk.Description))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("DESC", perk.Description));
        }

        if (!string.IsNullOrEmpty(perk.IconPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("ICON", perk.IconPath));
        }

        // DATA (5B): Trait + MinLevel + Ranks + Playable + HiddenFromPC.
        // Model lacks HiddenFromPC — zero is the correct default (perk visible to player).
        var data = new byte[5];
        data[0] = perk.Trait;
        data[1] = perk.MinLevel;
        data[2] = perk.Ranks;
        data[3] = perk.Playable;
        // byte 4 = HiddenFromPC, zero
        subs.Add(new EncodedSubrecord("DATA", data));

        // Top-level CTDA conditions (skill/stat requirements, perk prerequisites). PerkCondition
        // has its own structure (not DialogueCondition), so build the 28-byte CTDA directly.
        foreach (var condition in perk.Conditions)
        {
            subs.Add(new EncodedSubrecord("CTDA", BuildPerkCtdaSubrecord(condition)));
        }

        if (perk.Entries.Count > 0)
        {
            warnings.Add(
                $"New PERK 0x{perk.FormId:X8} has {perk.Entries.Count} entry chain(s) — PRKE/PRKC/EPFT/EPFD/PRKF emission deferred.");
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    private static byte[] BuildPerkCtdaSubrecord(Models.PerkCondition condition)
    {
        // CTDA (28B) per PDB CONDITION_ITEM_DATA. Operator is the low 5 bits of byte 0
        // (high 3 bits are reserved for run-on / OR flags which v17 leaves zero).
        var ctda = new byte[28];
        ctda[0] = condition.ComparisonOperator;
        SubrecordEncoder.WriteFloat(ctda, 4, condition.ComparisonValue);
        SubrecordEncoder.WriteUInt16(ctda, 8, condition.FunctionIndex);
        SubrecordEncoder.WriteFormId(ctda, 12, condition.Parameter1);
        SubrecordEncoder.WriteUInt32(ctda, 16, condition.Parameter2);
        // RunOn (bytes 20-23) and Reference (bytes 24-27) left zero
        return ctda;
    }
}
