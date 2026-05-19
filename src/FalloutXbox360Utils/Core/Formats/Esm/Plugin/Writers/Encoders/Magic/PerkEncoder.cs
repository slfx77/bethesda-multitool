using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Magic;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Magic;

/// <summary>
///     Encodes a <see cref="PerkRecord" /> (PERK) as PC-format subrecord bytes.
///     FNVEdit canonical order (wbPERK): EDID, OBND, FULL?, DESC?, ICON?, MICO?,
///     CTDA*(top-level conditions), DATA(5B), then per perk entry: PRKE + DATA(type-dependent) +
///     (PRKC + CTDA*)? + EPFT + EPFD? + PRKF.
///     DATA layout (5B): byte Trait + byte MinLevel + byte Ranks + byte Playable + byte HiddenFromPC.
///     Top-level CTDA conditions are emitted before DATA; per-entry chains follow.
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

        // Top-level CTDA conditions (skill/stat requirements, perk prerequisites). PerkCondition
        // has its own structure (not DialogueCondition), so build the 28-byte CTDA directly.
        // Per FNVEdit's wbPERK schema, CTDA precedes DATA — emit conditions first.
        foreach (var condition in perk.Conditions)
        {
            subs.Add(new EncodedSubrecord("CTDA", BuildPerkCtdaSubrecord(condition)));
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

        foreach (var entry in perk.Entries)
        {
            EmitPerkEntry(subs, entry, perk.FormId, warnings);
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    /// <summary>
    ///     Emit one perk-entry chain in FNV canonical order:
    ///     <list type="bullet">
    ///         <item>PRKE (3B): type + rank + priority</item>
    ///         <item>DATA (type-dependent): QuestFormId+QuestStage (type 0, 8B), AbilityFormId
    ///         (type 1, 4B), EntryPoint+FunctionType+RunImmediately (type 2, 3B)</item>
    ///         <item>PRKC (1B): condition-tab count, when conditions are present</item>
    ///         <item>CTDA* (28B each): per-entry conditions</item>
    ///         <item>EPFT (1B): function type (entry type 2 only)</item>
    ///         <item>EPFD (variable): function payload (entry type 2 only)</item>
    ///         <item>PRKF (0B): footer</item>
    ///     </list>
    /// </summary>
    private static void EmitPerkEntry(
        List<EncodedSubrecord> subs,
        PerkEntry entry,
        uint perkFormId,
        List<string> warnings)
    {
        // PRKE — 3 bytes: type + rank + priority.
        var prke = new byte[3];
        prke[0] = entry.Type;
        prke[1] = entry.Rank;
        prke[2] = entry.Priority;
        subs.Add(new EncodedSubrecord("PRKE", prke));

        // DATA — type-dependent body.
        switch (entry.Type)
        {
            case 0:
            {
                // Quest Stage: 8 bytes = QuestFormId(4) + QuestStage(4).
                var data = new byte[8];
                SubrecordEncoder.WriteFormId(data, 0, entry.QuestFormId ?? 0u);
                SubrecordEncoder.WriteInt32(data, 4, entry.QuestStage ?? 0);
                subs.Add(new EncodedSubrecord("DATA", data));
                break;
            }
            case 1:
            {
                // Ability: 4 bytes = AbilityFormId.
                var data = new byte[4];
                SubrecordEncoder.WriteFormId(data, 0, entry.AbilityFormId ?? 0u);
                subs.Add(new EncodedSubrecord("DATA", data));
                break;
            }
            case 2:
            {
                // Entry Point: 3 bytes = EntryPoint + FunctionType + RunImmediately.
                // Model lacks RunImmediately; zero is the safe default (function fires
                // through the engine's normal entry-point dispatch).
                var data = new byte[3];
                data[0] = entry.EntryPoint ?? 0;
                data[1] = entry.FunctionType ?? 0;
                data[2] = 0; // RunImmediately
                subs.Add(new EncodedSubrecord("DATA", data));
                break;
            }
            default:
                warnings.Add(
                    $"PERK 0x{perkFormId:X8} entry has unknown type {entry.Type} — emitting empty DATA.");
                subs.Add(new EncodedSubrecord("DATA", []));
                break;
        }

        // PRKC + CTDA* — per-entry conditions (entry type 2 typically). Master records emit
        // a PRKC tab-count byte before any CTDA block; we emit one PRKC covering all captured
        // conditions, since the model collapses per-tab grouping into a single Conditions list.
        if (entry.Conditions.Count > 0)
        {
            var tabCount = entry.ConditionTabCount ?? (byte)1;
            subs.Add(new EncodedSubrecord("PRKC", [tabCount]));
            foreach (var condition in entry.Conditions)
            {
                subs.Add(new EncodedSubrecord("CTDA", BuildPerkCtdaSubrecord(condition)));
            }
        }

        // EPFT + EPFD — entry type 2 only (Entry Point). FunctionType determines EPFD payload.
        if (entry.Type == 2 && entry.FunctionType is { } functionType)
        {
            subs.Add(new EncodedSubrecord("EPFT", [functionType]));

            var epfd = BuildEpfdPayload(entry, perkFormId, warnings);
            if (epfd is not null)
            {
                subs.Add(new EncodedSubrecord("EPFD", epfd));
            }
        }

        // PRKF — zero-byte footer marking end of entry.
        subs.Add(new EncodedSubrecord("PRKF", []));
    }

    /// <summary>
    ///     Build the EPFD payload for an entry-type-2 PerkEntry. The byte shape depends on
    ///     EPFT (FunctionType):
    ///     <list type="bullet">
    ///         <item>0-3 (Set/Add/Multiply/Add Range): float (4B)</item>
    ///         <item>4 (Add AV Mult): float (4B) — model stores in EffectValue</item>
    ///         <item>5-6 (Absolute / Negative Absolute Value): no payload</item>
    ///         <item>7 (Add Leveled List): FormID (4B)</item>
    ///         <item>8 (Add Activate Choice): zstring</item>
    ///     </list>
    ///     Returns null when no payload should be emitted (function types 5/6).
    /// </summary>
    private static byte[]? BuildEpfdPayload(PerkEntry entry, uint perkFormId, List<string> warnings)
    {
        switch (entry.FunctionType)
        {
            case 0 or 1 or 2 or 3 or 4:
            {
                var payload = new byte[4];
                SubrecordEncoder.WriteFloat(payload, 0, entry.EffectValue ?? 0f);
                return payload;
            }
            case 5 or 6:
                return null;
            case 7:
            {
                var payload = new byte[4];
                SubrecordEncoder.WriteFormId(payload, 0, entry.EffectFormId ?? 0u);
                return payload;
            }
            case 8:
            {
                // zstring; if the model only has EffectData (best-effort text), fall back to it.
                var text = entry.EffectData ?? string.Empty;
                var bytes = System.Text.Encoding.Latin1.GetBytes(text + "\0");
                return bytes;
            }
            default:
                warnings.Add(
                    $"PERK 0x{perkFormId:X8} entry has unknown FunctionType {entry.FunctionType} — omitting EPFD.");
                return null;
        }
    }

    private static byte[] BuildPerkCtdaSubrecord(PerkCondition condition)
    {
        // CTDA (28B) per PDB CONDITION_ITEM_DATA. Operator is the low 5 bits of byte 0
        // (high 3 bits are reserved for run-on / OR flags which we leave zero).
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
