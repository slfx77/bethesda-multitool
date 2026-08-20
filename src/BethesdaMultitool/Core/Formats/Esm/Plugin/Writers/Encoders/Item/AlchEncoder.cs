using BethesdaMultitool.Core.Formats.Esm.Enums;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Item;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Magic;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Quest;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Item;

/// <summary>
///     Encodes a <see cref="ConsumableRecord" /> (ALCH) as PC-format ALCH subrecord bytes.
///     Override path (<see cref="Encode" />) emits DATA (4 bytes: float weight) only — ENIT
///     and EFID/EFIT/CTDA effect groups are retained from the source ESM in that path.
///     New-record path (<see cref="EncodeNew" />) emits the full chain:
///     EDID, OBND?, FULL?, MODL?, MODT?, ICON?, MICO?, DATA, ENIT?,
///     (EFID + EFIT + CTDA*)*.
///     DATA + ENIT byte layouts are driven by <see cref="SchemaModelSerializer" /> against
///     the schemas registered in <c>SubrecordSchemaRegistry</c>; primitive subrecords
///     (EDID/OBND/FULL/MODL/MODT/ICON/MICO/EFID) still go through
///     <see cref="NewRecordSubrecords" />, and EFIT through
///     <see cref="EnchEncoder.BuildEfitSubrecord" />.
/// </summary>
public sealed class AlchEncoder : IRecordEncoder
{
    private static readonly Dictionary<string, Func<ConsumableRecord, object?>> DataExtractors =
        new(StringComparer.Ordinal)
        {
            ["Weight"] = m => m.Weight
        };

    private static readonly Dictionary<string, Func<ConsumableRecord, object?>> EnitExtractors =
        new(StringComparer.Ordinal)
        {
            ["Value"] = m => m.Value,
            ["Flags"] = m => BitConverter.GetBytes(m.Flags),
            ["WithdrawalEffect"] = m => m.WithdrawalEffectFormId ?? 0u,
            ["AddictionChance"] = m => m.AddictionChance,
            ["ConsumeSound"] = m => m.ConsumeSoundFormId ?? 0u
        };

    public string RecordType => "ALCH";
    public Type ModelType => typeof(ConsumableRecord);

    /// <summary>Produces override subrecords for an existing ALCH (a consumable) from its runtime-mutable fields.</summary>
    public EncodedRecord Encode(object model)
    {
        var alch = (ConsumableRecord)model;
        return new EncodedRecord
        {
            Subrecords = [SchemaModelSerializer.SerializeSubrecord("DATA", "ALCH", 4, alch, DataExtractors)],
            Warnings = []
        };
    }

    /// <summary>
    ///     Encode a new ALCH record from scratch. fopdoc canonical order:
    ///     EDID, OBND?, FULL?, MODL?, DATA, ENIT?, and per-effect EFID/EFIT/CTDA blocks.
    ///     When a validity set is supplied, every effect condition list is sanitized atomically:
    ///     one unresolved FormID replaces the whole list with a standalone never-fire CTDA so
    ///     dropping one member cannot widen an AND/OR expression. A null validity set preserves
    ///     the direct-call behavior and emits captured conditions verbatim. Production planning
    ///     applies per-field RecordPlan decisions in PlannedAlchEncoder before using that direct form.
    /// </summary>
    internal static EncodedRecord EncodeNew(
        ConsumableRecord alch,
        IReadOnlySet<uint>? validFormIds = null,
        IReadOnlyDictionary<uint, uint>? remapTable = null)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();
        var mutableValidFormIds = validFormIds switch
        {
            null => null,
            HashSet<uint> hashSet => hashSet,
            _ => new HashSet<uint>(validFormIds)
        };
        var emittedRemappedConditionFields = 0;

        if (string.IsNullOrEmpty(alch.EditorId))
        {
            warnings.Add($"New ALCH 0x{alch.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", alch.EditorId ?? string.Empty));

        if (alch.Bounds is not null)
        {
            subs.Add(NewRecordSubrecords.EncodeObndSubrecord(alch.Bounds));
        }

        if (!string.IsNullOrEmpty(alch.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", alch.FullName));
        }

        if (!string.IsNullOrEmpty(alch.ModelPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MODL", alch.ModelPath));
        }

        if (alch.TextureHashData is { Length: > 0 } modt)
        {
            subs.Add(NewRecordSubrecords.EncodeByteArraySubrecord("MODT", modt));
        }

        if (!string.IsNullOrEmpty(alch.IconPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("ICON", alch.IconPath));
        }

        if (!string.IsNullOrEmpty(alch.MessageIconPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MICO", alch.MessageIconPath));
        }

        // SCRI/YNAM/ZNAM/ETYP precede DATA in the fopdoc/xEdit ALCH layout.
        if (alch.ScriptFormId is > 0)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("SCRI", alch.ScriptFormId.Value));
        }

        if (alch.PickupSoundFormId is > 0)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("YNAM", alch.PickupSoundFormId.Value));
        }

        if (alch.DropSoundFormId is > 0)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("ZNAM", alch.DropSoundFormId.Value));
        }

        // ETYP is an int32 enum (FNV: -1..13); emit only when a real equipment type is present.
        if (alch.EquipmentType != EquipmentType.None)
        {
            subs.Add(NewRecordSubrecords.EncodeInt32Subrecord("ETYP", (int)alch.EquipmentType));
        }

        subs.Add(SchemaModelSerializer.SerializeSubrecord("DATA", "ALCH", 4, alch, DataExtractors));

        // ENIT is only emitted when at least one field is non-default; matches the original
        // encoder's gating to avoid round-tripping empty 20-byte blocks for plain consumables.
        if (alch.Value != 0 || alch.Flags != 0 || alch.WithdrawalEffectFormId.HasValue
            || Math.Abs(alch.AddictionChance) > float.Epsilon || alch.ConsumeSoundFormId.HasValue)
        {
            subs.Add(SchemaModelSerializer.SerializeSubrecord("ENIT", "ALCH", 20, alch, EnitExtractors));
        }

        for (var effectIndex = 0; effectIndex < alch.Effects.Count; effectIndex++)
        {
            var effect = alch.Effects[effectIndex];
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("EFID", effect.EffectFormId));
            subs.Add(new EncodedSubrecord("EFIT", EnchEncoder.BuildEfitSubrecord(effect)));

            IReadOnlyList<DialogueCondition> conditionsToEmit = effect.Conditions;
            if (mutableValidFormIds is not null && effect.Conditions.Count > 0)
            {
                var effectRemappedFields = 0;
                var rejectedConditions = 0;
                var sanitizedConditions = ConditionSanitizer.Filter(
                    effect.Conditions,
                    mutableValidFormIds,
                    remapTable,
                    ref effectRemappedFields,
                    ref rejectedConditions);

                if (rejectedConditions > 0)
                {
                    conditionsToEmit = [EffectConditionFailClosedPolicy.NeverFire];
                    warnings.Add(
                        $"New ALCH 0x{alch.FormId:X8} effect[{effectIndex}] " +
                        $"0x{effect.EffectFormId:X8} CTDA sanitizer: rejected {rejectedConditions} of " +
                        $"{effect.Conditions.Count} condition(s) with unresolved FormID fields; replaced " +
                        "the effect's entire condition list with one standalone never-fire CTDA " +
                        "(GetIsID Player base == 2). No individual condition was dropped or widened.");
                }
                else
                {
                    conditionsToEmit = sanitizedConditions;
                    emittedRemappedConditionFields += effectRemappedFields;
                }
            }

            foreach (var condition in conditionsToEmit)
            {
                subs.Add(new EncodedSubrecord("CTDA", InfoEncoder.BuildCtdaSubrecord(condition)));
            }
        }

        if (emittedRemappedConditionFields > 0)
        {
            warnings.Add(
                $"New ALCH 0x{alch.FormId:X8} CTDA sanitizer: remapped " +
                $"{emittedRemappedConditionFields} FormID field(s) in retained effect conditions " +
                "to emitted identities.");
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
