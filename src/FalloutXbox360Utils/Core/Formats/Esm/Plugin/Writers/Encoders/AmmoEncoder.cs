using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;

/// <summary>
///     Encodes an <see cref="AmmoRecord" /> as PC-format AMMO subrecord bytes.
///     Override path retains DAT2 from the source ESM verbatim. New-record path emits
///     EDID, OBND?, FULL?, MODL?, MODT?, ICON?, MICO?, DATA. DAT2 (FNV-specific 20 bytes
///     with projectiles-per-shot/projectile/damage-mult/consumed-pct/consumed-ammo) emit
///     is still deferred — the model captures only the projectile FormID, and the parser
///     probes multiple offsets to locate it (see
///     <see cref="Parsing.Handlers.ConsumableRecordHandler.TryReadAmmoProjectileFromDat2" />)
///     because the byte layout has never been pinned down. Round-tripping DAT2 requires
///     verifying the layout against master FalloutNV.esm bytes and extending AmmoRecord
///     with the missing fields.
///     DATA layout: float Speed(0) + uint8 Flags(4) + pad(5..7) + uint32 Value(8) + uint8 ClipRounds(12).
/// </summary>
public sealed class AmmoEncoder : IRecordEncoder
{
    public string RecordType => "AMMO";
    public Type ModelType => typeof(AmmoRecord);

    public EncodedRecord Encode(object model)
    {
        var ammo = (AmmoRecord)model;
        return new EncodedRecord
        {
            Subrecords = [new EncodedSubrecord("DATA", BuildDataSubrecord(ammo))],
            Warnings = []
        };
    }

    /// <summary>
    ///     Encode a new AMMO record from scratch. fopdoc canonical order:
    ///     EDID, OBND?, FULL?, MODL?, DATA, DAT2?. DAT2 (FNV-specific 20 bytes with damage-mult
    ///     and consumed-percentage fields) is deferred to v5 — model lacks those fields.
    /// </summary>
    internal static EncodedRecord EncodeNew(AmmoRecord ammo)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(ammo.EditorId))
        {
            warnings.Add($"New AMMO 0x{ammo.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", ammo.EditorId ?? string.Empty));

        if (ammo.Bounds is not null)
        {
            subs.Add(NewRecordSubrecords.EncodeObndSubrecord(ammo.Bounds));
        }

        if (!string.IsNullOrEmpty(ammo.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", ammo.FullName));
        }

        if (!string.IsNullOrEmpty(ammo.ModelPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MODL", ammo.ModelPath));
        }

        if (ammo.TextureHashData is { Length: > 0 } modt)
        {
            subs.Add(NewRecordSubrecords.EncodeByteArraySubrecord("MODT", modt));
        }

        if (!string.IsNullOrEmpty(ammo.IconPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("ICON", ammo.IconPath));
        }

        if (!string.IsNullOrEmpty(ammo.MessageIconPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MICO", ammo.MessageIconPath));
        }

        subs.Add(new EncodedSubrecord("DATA", BuildDataSubrecord(ammo)));

        if (ammo.ProjectileFormId.HasValue || ammo.ProjectileFormIds.Count > 0)
        {
            warnings.Add(
                $"New AMMO 0x{ammo.FormId:X8} carries projectile data — DAT2 emission deferred to v5.");
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    private static byte[] BuildDataSubrecord(AmmoRecord ammo)
    {
        var data = new byte[13];
        SubrecordEncoder.WriteFloat(data, 0, ammo.Speed);
        data[4] = ammo.Flags;
        // Bytes 5..7 are C-struct padding — leave as zero.
        SubrecordEncoder.WriteUInt32(data, 8, ammo.Value);
        data[12] = ammo.ClipRounds;
        return data;
    }
}
