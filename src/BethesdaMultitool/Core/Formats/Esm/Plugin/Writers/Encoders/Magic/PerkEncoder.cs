using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Magic;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Magic;

/// <summary>
///     Encodes a <see cref="PerkRecord" /> (PERK) as PC-format subrecord bytes.
///     FNVEdit canonical order (wbPERK): EDID, OBND, FULL?, DESC?, ICON?, MICO?,
///     CTDA*(top-level conditions), DATA(4B/5B), then per perk entry: PRKE + DATA(type-dependent) +
///     (PRKC + CTDA*)* + EPFT + EPFD? + PRKF.
///     DATA layout: byte Trait + byte MinLevel + byte Ranks + byte Playable + optional byte Hidden.
///     Top-level CTDA conditions are emitted before DATA; per-entry chains follow.
/// </summary>
public sealed class PerkEncoder : IRecordEncoder
{
    // CTDA schema field names: Type, ComparisonValue, FunctionIndex, Parameter1,
    // Parameter2, RunOn, Reference. Type stores the comparison operator in bits 5–7;
    // the unmodeled low-bit flags, RunOn, and Reference currently zero-fill.
    private static readonly Dictionary<string, Func<PerkCondition, object?>> CtdaExtractors =
        new(StringComparer.Ordinal)
        {
            ["Type"] = m => EncodeComparisonType(m.ComparisonOperator),
            ["ComparisonValue"] = m => m.ComparisonValue,
            ["FunctionIndex"] = m => m.FunctionIndex,
            ["Parameter1"] = m => m.Parameter1,
            ["Parameter2"] = m => m.Parameter2
        };

    public string RecordType => "PERK";
    public Type ModelType => typeof(PerkRecord);

    /// <summary>
    ///     Encode a new PERK record. CTDAs across top-level + per-entry are routed through
    ///     <see cref="ConditionSanitizer.FilterPerk" /> so dangling FormID parameters get
    ///     remapped via the runtime→emitted alias table or dropped. PerkCondition
    ///     has Parameter1FormId/Parameter2FormId stamped from the model — when the param is
    ///     FormID-typed (HasPerk, GetEquipped, etc.) the sanitizer applies; for AV/int params
    ///     the sanitizer leaves them alone.
    /// </summary>
    internal static EncodedRecord EncodeNew(
        PerkRecord perk,
        IReadOnlySet<uint>? validFormIds = null,
        IReadOnlyDictionary<uint, uint>? remapTable = null)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();
        var droppedCtdas = 0;
        var remappedCtdaParams = 0;

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
        var sanitizedTopConds = SanitizePerkConditions(perk.Conditions, validFormIds, remapTable,
            ref droppedCtdas, ref remappedCtdaParams);
        foreach (var condition in sanitizedTopConds)
        {
            subs.Add(new EncodedSubrecord("CTDA", BuildPerkCtdaSubrecord(condition)));
        }

        // DATA (4B/5B): preserve whether the optional Hidden byte was serialized.
        // A newly constructed model defaults Hidden to zero and therefore emits canonical 5B DATA.
        var data = new byte[perk.Hidden.HasValue ? 5 : 4];
        data[0] = perk.Trait;
        data[1] = perk.MinLevel;
        data[2] = perk.Ranks;
        data[3] = perk.Playable;
        if (perk.Hidden is { } hidden)
        {
            data[4] = hidden;
        }

        subs.Add(new EncodedSubrecord("DATA", data));

        foreach (var entry in perk.Entries)
        {
            EmitPerkEntry(subs, entry, perk.FormId, warnings, validFormIds, remapTable,
                ref droppedCtdas, ref remappedCtdaParams);
        }

        if (droppedCtdas > 0 || remappedCtdaParams > 0)
        {
            warnings.Add(
                $"New PERK 0x{perk.FormId:X8} CTDA sanitizer: dropped {droppedCtdas} " +
                $"condition(s) with dangling FormID params, remapped {remappedCtdaParams} " +
                "param FormID(s) via the runtime→emitted alias table.");
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    /// <summary>
    ///     Run PerkConditions through <see cref="ConditionSanitizer.FilterPerk" /> when a
    ///     validity set is supplied; otherwise pass through verbatim for legacy callers/tests.
    /// </summary>
    private static List<PerkCondition> SanitizePerkConditions(
        List<PerkCondition> conditions,
        IReadOnlySet<uint>? validFormIds,
        IReadOnlyDictionary<uint, uint>? remapTable,
        ref int droppedCtdas,
        ref int remappedCtdaParams)
    {
        if (validFormIds is null)
        {
            return conditions;
        }

        return ConditionSanitizer.FilterPerk(
            conditions, ToMutableSet(validFormIds), remapTable,
            ref remappedCtdaParams, ref droppedCtdas);
    }

    private static HashSet<uint> ToMutableSet(IReadOnlySet<uint> set)
    {
        return set as HashSet<uint> ?? new HashSet<uint>(set);
    }

    /// <summary>
    ///     Emit one perk-entry chain in FNV canonical order:
    ///     <list type="bullet">
    ///         <item>PRKE (3B): type + rank + priority</item>
    ///         <item>
    ///             DATA (type-dependent): QuestFormId+QuestStage (type 0, 8B), AbilityFormId
    ///             (type 1, 4B), EntryPoint+Function+PerkConditionTabCount (type 2, 3B)
    ///         </item>
    ///         <item>Repeated PRKC (signed 1B selector) + CTDA* condition groups</item>
    ///         <item>EPFT (1B): function type (entry type 2 only)</item>
    ///         <item>EPFD (variable): function payload (entry type 2 only)</item>
    ///         <item>PRKF (0B): footer</item>
    ///     </list>
    /// </summary>
    private static void EmitPerkEntry(
        List<EncodedSubrecord> subs,
        PerkEntry entry,
        uint perkFormId,
        List<string> warnings,
        IReadOnlySet<uint>? validFormIds,
        IReadOnlyDictionary<uint, uint>? remapTable,
        ref int droppedCtdas,
        ref int remappedCtdaParams)
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
                // Entry Point: 3 bytes = EntryPoint + result Function + Perk Condition Tab Count.
                // EPFT is a separate function-parameter type and must not be copied into DATA[1].
                var data = new byte[3];
                data[0] = entry.EntryPoint ?? 0;
                data[1] = entry.EntryPointFunction ?? 0;
                data[2] = ResolvePerkConditionTabCount(entry);
                subs.Add(new EncodedSubrecord("DATA", data));
                break;
            }
            default:
                if (entry.RawEntryData is { Length: > 0 } rawEntryData)
                {
                    subs.Add(new EncodedSubrecord("DATA", rawEntryData));
                }
                else
                {
                    warnings.Add(
                        $"PERK 0x{perkFormId:X8} entry has unknown type {entry.Type} - emitting empty DATA.");
                    subs.Add(new EncodedSubrecord("DATA", []));
                }

                break;
        }

        // Repeated PRKC + CTDA* groups — per-entry conditions (entry type 2 typically). PRKC is a
        // signed selector; DATA[2], not PRKC, stores the tab count. Preserve source group order and
        // two's-complement selector bytes. A null selector represents malformed source CTDAs that
        // lacked a leading PRKC and is intentionally re-emitted without inventing one.
        foreach (var group in entry.ConditionGroups)
        {
            var sanitizedEntryConds = SanitizePerkConditions(group.Conditions,
                validFormIds, remapTable, ref droppedCtdas, ref remappedCtdaParams);

            // If sanitation removed every original CTDA, omit its now-empty selector too. An
            // originally empty group is retained so parse→encode remains lossless.
            if (group.Conditions.Count > 0 && sanitizedEntryConds.Count == 0)
            {
                continue;
            }

            if (group.RunOn is { } runOn)
            {
                subs.Add(new EncodedSubrecord("PRKC", [unchecked((byte)runOn)]));
            }

            foreach (var condition in sanitizedEntryConds)
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

    private static byte ResolvePerkConditionTabCount(PerkEntry entry)
    {
        if (entry.PerkConditionTabCount is { } authoredCount)
        {
            return authoredCount;
        }

        // xEdit derives this byte from the selected entry point's semantic condition-slot count,
        // not simply from the number of serialized groups. Without that 74-entry metadata table,
        // use the smallest internally consistent fallback: cover both the number of groups and
        // the highest non-negative PRKC selector. Negative/raw selectors do not imply a slot.
        var derivedCount = entry.ConditionGroups.Count;
        foreach (var group in entry.ConditionGroups)
        {
            if (group.RunOn is { } runOn && runOn >= 0)
            {
                derivedCount = Math.Max(derivedCount, runOn + 1);
            }
        }

        if (derivedCount > byte.MaxValue)
        {
            throw new InvalidOperationException(
                $"PERK entry has {derivedCount} derived condition tabs; DATA[2] can store at most 255.");
        }

        return (byte)derivedCount;
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
                if (entry.RawFunctionData is { Length: > 0 } rawActivateChoiceData)
                {
                    return rawActivateChoiceData;
                }

                // zstring; if the model only has EffectData (best-effort text), fall back to it.
                var text = entry.EffectData ?? string.Empty;
                var bytes = Encoding.Latin1.GetBytes(text + "\0");
                return bytes;
            }
            default:
                if (entry.RawFunctionData is { Length: > 0 } rawUnknownFunctionData)
                {
                    return rawUnknownFunctionData;
                }

                warnings.Add(
                    $"PERK 0x{perkFormId:X8} entry has unknown FunctionType {entry.FunctionType} - omitting EPFD.");
                return null;
        }
    }

    private static byte[] BuildPerkCtdaSubrecord(PerkCondition condition)
    {
        // CTDA (28B) per PDB CONDITION_ITEM_DATA. Type at offset 0 stores the comparison
        // operator in bits 5–7; low bits are condition flags. PerkCondition does not yet model
        // those flags, RunOn (offset 20), or Reference (offset 24), so they zero-fill.
        return SchemaModelSerializer.Serialize("CTDA", "", 28, condition, CtdaExtractors);
    }

    private static byte EncodeComparisonType(byte comparisonOperator)
    {
        if (comparisonOperator > 5)
        {
            throw new ArgumentOutOfRangeException(
                nameof(comparisonOperator),
                comparisonOperator,
                "A CTDA comparison operator must be one of the six serialized values 0 through 5.");
        }

        return (byte)(comparisonOperator << 5);
    }
}
