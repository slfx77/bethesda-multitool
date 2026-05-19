using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;

/// <summary>
///     Encodes a <see cref="ConsumableRecord" /> (ALCH) as PC-format ALCH subrecord bytes.
///     Override path (<see cref="Encode" />) emits DATA (4 bytes: float weight) only — ENIT
///     and EFID/EFIT effect blocks are retained from the source ESM in that path.
///     New-record path (<see cref="EncodeNew" />) emits the full chain:
///     EDID, OBND?, FULL?, MODL?, MODT?, ICON?, MICO?, DATA, ENIT?, (EFID + EFIT)*.
///     ENIT layout (20 bytes): uint32 Value + bytes Flags(4..7) + FormID Addiction(8..11) +
///     float AddictionChance(12..15) + FormID UseSoundOrWithdrawalEffect(16..19).
/// </summary>
public sealed class AlchEncoder : IRecordEncoder
{
    public string RecordType => "ALCH";
    public Type ModelType => typeof(ConsumableRecord);

    public EncodedRecord Encode(object model)
    {
        var alch = (ConsumableRecord)model;
        return new EncodedRecord
        {
            Subrecords = [new EncodedSubrecord("DATA", BuildDataSubrecord(alch))],
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

        subs.Add(new EncodedSubrecord("DATA", BuildDataSubrecord(alch)));

        // ENIT — 20 bytes per FNV ALCH ENIT schema:
        //   uint32 Value(0) + bytes Flags(4..7) + FormID Addiction(8..11) +
        //   float AddictionChance(12..15) + FormID UseSoundOrWithdrawalEffect(16..19).
        // Always emit when we have any of the four ENIT fields populated; missing fields zero.
        if (alch.Value != 0 || alch.Flags != 0 || alch.AddictionFormId.HasValue
            || Math.Abs(alch.AddictionChance) > float.Epsilon || alch.WithdrawalEffectFormId.HasValue)
        {
            var enit = new byte[20];
            SubrecordEncoder.WriteUInt32(enit, 0, alch.Value);
            SubrecordEncoder.WriteUInt32(enit, 4, alch.Flags);
            SubrecordEncoder.WriteFormId(enit, 8, alch.AddictionFormId ?? 0);
            SubrecordEncoder.WriteFloat(enit, 12, alch.AddictionChance);
            SubrecordEncoder.WriteFormId(enit, 16, alch.WithdrawalEffectFormId ?? 0);
            subs.Add(new EncodedSubrecord("ENIT", enit));
        }

        foreach (var effect in alch.Effects)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("EFID", effect.EffectFormId));
            subs.Add(new EncodedSubrecord("EFIT", EnchEncoder.BuildEfitSubrecord(effect)));
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    private static byte[] BuildDataSubrecord(ConsumableRecord alch)
    {
        var data = new byte[4];
        SubrecordEncoder.WriteFloat(data, 0, alch.Weight);
        return data;
    }
}
