using BethesdaMultitool.Core.Formats.Esm.Models.Records.Item;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Item;

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
    private static readonly Dictionary<string, Func<AmmoRecord, object?>> DataExtractors = new(StringComparer.Ordinal)
    {
        ["Speed"] = m => m.Speed,
        ["Flags"] = m => m.Flags,
        ["Value"] = m => m.Value,
        ["ClipRounds"] = m => m.ClipRounds,
    };

    public string RecordType => "AMMO";
    public Type ModelType => typeof(AmmoRecord);

    /// <summary>Produces override subrecords for an existing AMMO (an ammunition record) from its runtime-mutable fields.</summary>
    public EncodedRecord Encode(object model)
    {
        var ammo = (AmmoRecord)model;
        return new EncodedRecord
        {
            Subrecords = [SchemaModelSerializer.SerializeSubrecord("DATA", "AMMO", 13, ammo, DataExtractors)],
            Warnings = []
        };
    }

    /// <summary>
    ///     Encode a new AMMO record from scratch. fopdoc canonical order:
    ///     EDID, OBND?, FULL?, MODL?, DATA, DAT2?. DAT2 (FNV-specific 20 bytes with damage-mult
    ///     and consumed-percentage fields) is deferred — model lacks those fields.
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

        // SCRI/YNAM/ZNAM precede DATA in the fopdoc/xEdit AMMO layout.
        if (ammo.ScriptFormId is > 0)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("SCRI", ammo.ScriptFormId.Value));
        }

        if (ammo.PickupSoundFormId is > 0)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("YNAM", ammo.PickupSoundFormId.Value));
        }

        if (ammo.DropSoundFormId is > 0)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("ZNAM", ammo.DropSoundFormId.Value));
        }

        subs.Add(SchemaModelSerializer.SerializeSubrecord("DATA", "AMMO", 13, ammo, DataExtractors));

        if (ammo.ProjectileFormId.HasValue || ammo.ProjectileFormIds.Count > 0)
        {
            warnings.Add(
                $"New AMMO 0x{ammo.FormId:X8} carries projectile data — DAT2 emission deferred.");
        }

        // ONAM/QNAM/RCIL follow DATA (and the deferred DAT2) in the AMMO layout.
        if (!string.IsNullOrEmpty(ammo.ShortName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("ONAM", ammo.ShortName));
        }

        if (!string.IsNullOrEmpty(ammo.Abbreviation))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("QNAM", ammo.Abbreviation));
        }

        foreach (var ammoEffect in ammo.AmmoEffectFormIds)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("RCIL", ammoEffect));
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
