using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.AI;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;

/// <summary>
///     Encodes a <see cref="PackageRecord" /> (PACK) as PC-format subrecord bytes.
///     v6 emits the full record from scratch: EDID + PKDT + PLDT? + PLD2? + PSDT? + PTDT? +
///     PTD2? + PKPT? + PKW3? + CNAM (combat style FormID, deferred — not in model).
///     Override path is a no-op.
///     PKDT (12 bytes) per PDB PACKAGE_DATA:
///     uint32 iPackFlags(0) + uint8 cPackType(4) + uint8 unused(5) +
///     uint16 iFOBehaviorFlags(6) + uint16 iPackageSpecificFlags(8) + 2 unknown bytes(10,11).
///     PSDT (8 bytes): int8 Month(0) + int8 DayOfWeek(1) + int8 Date(2) + int8 Time(3) +
///     int32 Duration(4).
///     PLDT/PLD2 (12 bytes): byte Type + pad(3) + uint32 Union + int32 Radius.
///     PTDT/PTD2 (16 bytes): byte Type + pad(3) + uint32 Union + int32 CountDistance + float Unknown.
///     PKW3 (24 bytes): 6 bool bytes(0..5) + uint16 BurstCount(6) + uint16 VolleyShotsMin(8) +
///     uint16 VolleyShotsMax(10) + float VolleyWaitMin(12) + float VolleyWaitMax(16) +
///     uint32 Weapon(20).
///     PKPT (2 bytes): bool Repeatable(0) + bool StartingLocationLinkedRef(1).
/// </summary>
public sealed class PackEncoder : IRecordEncoder
{
    public string RecordType => "PACK";
    public Type ModelType => typeof(PackageRecord);

    public EncodedRecord Encode(object model)
    {
        return new EncodedRecord { Subrecords = [], Warnings = [] };
    }

    /// <summary>
    ///     Encode a new PACK record from scratch in fopdoc canonical order:
    ///     EDID, PKDT, PLDT?, PSDT?, PTDT?, PLD2?, PTD2?, PKW3?, PKPT?.
    /// </summary>
    internal static EncodedRecord EncodeNew(PackageRecord pack)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(pack.EditorId))
        {
            warnings.Add($"New PACK 0x{pack.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", pack.EditorId ?? string.Empty));

        if (pack.Data is not null)
        {
            subs.Add(new EncodedSubrecord("PKDT", BuildPkdtSubrecord(pack.Data)));
        }
        else
        {
            warnings.Add($"New PACK 0x{pack.FormId:X8} has no PKDT data — emitting zero-filled PKDT.");
            subs.Add(new EncodedSubrecord("PKDT", new byte[12]));
        }

        if (pack.Location is not null)
        {
            subs.Add(new EncodedSubrecord("PLDT", BuildPlocSubrecord(pack.Location)));
        }

        if (pack.Schedule is not null)
        {
            subs.Add(new EncodedSubrecord("PSDT", BuildPsdtSubrecord(pack.Schedule)));
        }

        if (pack.Target is not null)
        {
            subs.Add(new EncodedSubrecord("PTDT", BuildPtdtSubrecord(pack.Target)));
        }

        if (pack.Location2 is not null)
        {
            subs.Add(new EncodedSubrecord("PLD2", BuildPlocSubrecord(pack.Location2)));
        }

        if (pack.Target2 is not null)
        {
            subs.Add(new EncodedSubrecord("PTD2", BuildPtdtSubrecord(pack.Target2)));
        }

        if (pack.UseWeaponData is not null)
        {
            subs.Add(new EncodedSubrecord("PKW3", BuildPkw3Subrecord(pack.UseWeaponData)));
        }

        // PKPT is emitted only for patrol packages (TypeName "Patrol", cPackType 13). For
        // other types the model's IsRepeatable/IsStartingLocationLinkedRef are always false,
        // so guard by checking that either flag is set.
        if (pack.IsRepeatable || pack.IsStartingLocationLinkedRef)
        {
            var pkpt = new byte[2];
            pkpt[0] = pack.IsRepeatable ? (byte)1 : (byte)0;
            pkpt[1] = pack.IsStartingLocationLinkedRef ? (byte)1 : (byte)0;
            subs.Add(new EncodedSubrecord("PKPT", pkpt));
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    private static byte[] BuildPkdtSubrecord(PackageData pkdt)
    {
        var data = new byte[12];
        SubrecordEncoder.WriteUInt32(data, 0, pkdt.GeneralFlags);
        data[4] = pkdt.Type;
        // byte 5 unused
        SubrecordEncoder.WriteUInt16(data, 6, pkdt.FalloutBehaviorFlags);
        SubrecordEncoder.WriteUInt16(data, 8, pkdt.TypeSpecificFlags);
        // bytes 10-11 unknown (zero)
        return data;
    }

    private static byte[] BuildPsdtSubrecord(PackageSchedule psdt)
    {
        var data = new byte[8];
        data[0] = (byte)psdt.Month;
        data[1] = (byte)psdt.DayOfWeek;
        data[2] = (byte)psdt.Date;
        data[3] = (byte)psdt.Time;
        SubrecordEncoder.WriteInt32(data, 4, psdt.Duration);
        return data;
    }

    private static byte[] BuildPlocSubrecord(PackageLocation loc)
    {
        var data = new byte[12];
        data[0] = loc.Type;
        // bytes 1-3 padding
        SubrecordEncoder.WriteUInt32(data, 4, loc.Union);
        SubrecordEncoder.WriteInt32(data, 8, loc.Radius);
        return data;
    }

    private static byte[] BuildPtdtSubrecord(PackageTarget target)
    {
        var data = new byte[16];
        data[0] = target.Type;
        // bytes 1-3 padding
        SubrecordEncoder.WriteUInt32(data, 4, target.FormIdOrType);
        SubrecordEncoder.WriteInt32(data, 8, target.CountDistance);
        SubrecordEncoder.WriteFloat(data, 12, target.AcquireRadius);
        return data;
    }

    private static byte[] BuildPkw3Subrecord(PackageUseWeaponData pkw3)
    {
        var data = new byte[24];
        data[0] = pkw3.AlwaysHit ? (byte)1 : (byte)0;
        data[1] = pkw3.DoNoDamage ? (byte)1 : (byte)0;
        data[2] = pkw3.Crouch ? (byte)1 : (byte)0;
        data[3] = pkw3.HoldFire ? (byte)1 : (byte)0;
        data[4] = pkw3.VolleyFire ? (byte)1 : (byte)0;
        data[5] = pkw3.RepeatFire ? (byte)1 : (byte)0;
        SubrecordEncoder.WriteUInt16(data, 6, pkw3.BurstCount);
        SubrecordEncoder.WriteUInt16(data, 8, pkw3.VolleyShotsMin);
        SubrecordEncoder.WriteUInt16(data, 10, pkw3.VolleyShotsMax);
        SubrecordEncoder.WriteFloat(data, 12, pkw3.VolleyWaitMin);
        SubrecordEncoder.WriteFloat(data, 16, pkw3.VolleyWaitMax);
        SubrecordEncoder.WriteUInt32(data, 20, pkw3.WeaponFormId ?? 0);
        return data;
    }
}
