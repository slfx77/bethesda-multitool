using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;

using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Magic;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Item;

/// <summary>
///     Encodes a <see cref="ConsumableRecord" /> (ALCH) as PC-format ALCH subrecord bytes.
///     Override path (<see cref="Encode" />) emits DATA (4 bytes: float weight) only — ENIT
///     and EFID/EFIT effect blocks are retained from the source ESM in that path.
///     New-record path (<see cref="EncodeNew" />) emits the full chain:
///     EDID, OBND?, FULL?, MODL?, MODT?, ICON?, MICO?, DATA, ENIT?, (EFID + EFIT)*.
///     DATA + ENIT byte layouts are driven by <see cref="SchemaModelSerializer" /> against
///     the schemas registered in <c>SubrecordSchemaRegistry</c>; primitive subrecords
///     (EDID/OBND/FULL/MODL/MODT/ICON/MICO/EFID) still go through
///     <see cref="NewRecordSubrecords" />, and EFIT through
///     <see cref="EnchEncoder.BuildEfitSubrecord" />.
/// </summary>
public sealed class AlchEncoder : IRecordEncoder
{
    private static readonly Dictionary<string, Func<ConsumableRecord, object?>> DataExtractors = new(StringComparer.Ordinal)
    {
        ["Weight"] = m => m.Weight,
    };

    private static readonly Dictionary<string, Func<ConsumableRecord, object?>> EnitExtractors = new(StringComparer.Ordinal)
    {
        ["Value"] = m => m.Value,
        ["Flags"] = m => BitConverter.GetBytes(m.Flags),
        ["Addiction"] = m => m.AddictionFormId ?? 0u,
        ["AddictionChance"] = m => m.AddictionChance,
        ["UseSoundOrWithdrawalEffect"] = m => m.WithdrawalEffectFormId ?? 0u,
    };

    public string RecordType => "ALCH";
    public Type ModelType => typeof(ConsumableRecord);

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
    ///     EDID, OBND?, FULL?, MODL?, DATA. ENIT and EFID/EFIT effect blocks are deferred
    ///     because the model's ENIT byte order isn't fully verified.
    /// </summary>
    internal static EncodedRecord EncodeNew(ConsumableRecord alch)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

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

        subs.Add(SchemaModelSerializer.SerializeSubrecord("DATA", "ALCH", 4, alch, DataExtractors));

        // ENIT is only emitted when at least one field is non-default; matches the original
        // encoder's gating to avoid round-tripping empty 20-byte blocks for plain consumables.
        if (alch.Value != 0 || alch.Flags != 0 || alch.AddictionFormId.HasValue
            || Math.Abs(alch.AddictionChance) > float.Epsilon || alch.WithdrawalEffectFormId.HasValue)
        {
            subs.Add(SchemaModelSerializer.SerializeSubrecord("ENIT", "ALCH", 20, alch, EnitExtractors));
        }

        foreach (var effect in alch.Effects)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("EFID", effect.EffectFormId));
            subs.Add(new EncodedSubrecord("EFIT", EnchEncoder.BuildEfitSubrecord(effect)));
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
