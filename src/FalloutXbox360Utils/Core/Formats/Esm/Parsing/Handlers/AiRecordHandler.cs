using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.AI;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing.Handlers;

internal sealed class AiRecordHandler(RecordParserContext context) : RecordHandlerBase(context)
{
    /// <summary>
    ///     Parse all AI Package (PACK) records from the scan result.
    ///     Only extracts location data (PLDT) needed for NPC spawn resolution.
    /// </summary>
    internal List<PackageRecord> ParsePackages()
    {
        var packages = ParseRecordList("PACK", 16384,
            (record, buffer) => ParsePackageFromAccessor(record, buffer),
            record => new PackageRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            });

        Context.MergeRuntimeRecords(packages, 0x49, p => p.FormId,
            (reader, entry) => reader.ReadRuntimePackage(entry), "packages");

        return packages;
    }

    private PackageRecord ParsePackageFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new PackageRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        PackageData? packageData = null;
        PackageSchedule? schedule = null;
        PackageUseWeaponData? useWeaponData = null;
        PackageLocation? location = null;
        PackageLocation? location2 = null;
        PackageTarget? target = null;
        PackageTarget? target2 = null;
        var isRepeatable = false;
        var isStartingLocationLinkedRef = false;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "PKDT" when sub.DataLength >= 10:
                    packageData = ParsePackageData(subData, record.IsBigEndian);
                    break;
                case "PSDT" when sub.DataLength >= 8:
                    schedule = ParsePackageSchedule(subData, record.IsBigEndian);
                    break;
                case "PKW3" when sub.DataLength >= 24:
                    useWeaponData = ParsePackageUseWeaponData(subData, record.IsBigEndian);
                    break;
                case "PLDT" when sub.DataLength >= 12:
                    location ??= ParsePackageLocation(subData, record.IsBigEndian);
                    break;
                case "PLD2" when sub.DataLength >= 12:
                    location2 ??= ParsePackageLocation(subData, record.IsBigEndian);
                    break;
                case "PTDT" when sub.DataLength >= 16:
                    target ??= ParsePackageTarget(subData, record.IsBigEndian);
                    break;
                case "PTD2" when sub.DataLength >= 16:
                    target2 ??= ParsePackageTarget(subData, record.IsBigEndian);
                    break;
                case "PKPT" when sub.DataLength >= 2:
                    (isRepeatable, isStartingLocationLinkedRef) = ParsePatrolData(subData);
                    break;
            }
        }

        return new PackageRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            Data = packageData,
            Schedule = schedule,
            UseWeaponData = useWeaponData,
            Location = location,
            Location2 = location2,
            Target = target,
            Target2 = target2,
            IsRepeatable = isRepeatable,
            IsStartingLocationLinkedRef = isStartingLocationLinkedRef,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    /// <summary>
    ///     Parse PKDT subrecord (12 bytes). Layout from PDB PACKAGE_DATA:
    ///     [0-3] iPackFlags (uint32) — endian-dependent
    ///     [4]   cPackType (byte) — no endian swap
    ///     [5]   unused
    ///     [6-7] iFOBehaviorFlags (uint16) — endian-dependent
    ///     [8-9] iPackageSpecificFlags (uint16) — endian-dependent
    ///     [10-11] unknown
    /// </summary>
    internal static PackageData ParsePackageData(ReadOnlySpan<byte> data, bool isBigEndian)
    {
        var generalFlags = isBigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data)
            : BinaryPrimitives.ReadUInt32LittleEndian(data);

        var type = data[4];

        var foBehavior = isBigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(data[6..])
            : BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);

        var typeSpecific = isBigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(data[8..])
            : BinaryPrimitives.ReadUInt16LittleEndian(data[8..]);

        return new PackageData
        {
            Type = type,
            GeneralFlags = generalFlags,
            FalloutBehaviorFlags = foBehavior,
            TypeSpecificFlags = typeSpecific
        };
    }

    /// <summary>Detect and zero out uninitialized memory patterns in 32-bit flags.</summary>
    internal static uint SanitizeFlags32(uint value)
    {
        // 0xCDCDCDCD = MS CRT debug heap uninitialized fill
        if (value == 0xCDCDCDCD)
        {
            return 0;
        }

        return value;
    }

    /// <summary>Detect and zero out uninitialized memory patterns in 16-bit flags.</summary>
    internal static ushort SanitizeFlags16(ushort value)
    {
        // High byte 0xCD or 0xCC indicates uninitialized/partially-overwritten heap memory.
        // Patterns seen: 0xCDCD, 0xCDC5, 0xCDD1, 0xCDF5, 0xCC21, 0xCC31
        var highByte = value >> 8;
        if (highByte is 0xCD or 0xCC)
        {
            return 0;
        }

        return value;
    }

    /// <summary>
    ///     Parse PKPT subrecord (2 bytes). Layout from PDB PACK_PATROL_DATA:
    ///     [0] bRepeatable (bool byte)
    ///     [1] bStartingLocationAtLinkedRef (bool byte)
    /// </summary>
    internal static (bool IsRepeatable, bool IsStartingLocationLinkedRef) ParsePatrolData(
        ReadOnlySpan<byte> data)
    {
        return (data[0] != 0, data[1] != 0);
    }

    /// <summary>
    ///     Parse PSDT subrecord (8 bytes). Bytes 0-3 are single bytes (no endian swap).
    ///     Duration at [4..8] is int32 (endian-dependent, in hours).
    /// </summary>
    internal static PackageSchedule ParsePackageSchedule(ReadOnlySpan<byte> data, bool isBigEndian)
    {
        var duration = isBigEndian
            ? BinaryPrimitives.ReadInt32BigEndian(data[4..])
            : BinaryPrimitives.ReadInt32LittleEndian(data[4..]);

        return new PackageSchedule
        {
            Month = (sbyte)data[0],
            DayOfWeek = (sbyte)data[1],
            Date = (sbyte)data[2],
            Time = (sbyte)data[3],
            Duration = duration
        };
    }

    internal static PackageUseWeaponData ParsePackageUseWeaponData(
        ReadOnlySpan<byte> data,
        bool isBigEndian)
    {
        var burstCount = isBigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(data[6..])
            : BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
        var volleyShotsMin = isBigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(data[8..])
            : BinaryPrimitives.ReadUInt16LittleEndian(data[8..]);
        var volleyShotsMax = isBigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(data[10..])
            : BinaryPrimitives.ReadUInt16LittleEndian(data[10..]);
        var volleyWaitMin = isBigEndian
            ? BinaryPrimitives.ReadSingleBigEndian(data[12..])
            : BinaryPrimitives.ReadSingleLittleEndian(data[12..]);
        var volleyWaitMax = isBigEndian
            ? BinaryPrimitives.ReadSingleBigEndian(data[16..])
            : BinaryPrimitives.ReadSingleLittleEndian(data[16..]);
        var weaponFormId = isBigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data[20..])
            : BinaryPrimitives.ReadUInt32LittleEndian(data[20..]);

        return new PackageUseWeaponData
        {
            AlwaysHit = data[0] != 0,
            DoNoDamage = data[1] != 0,
            Crouch = data[2] != 0,
            HoldFire = data[3] != 0,
            VolleyFire = data[4] != 0,
            RepeatFire = data[5] != 0,
            BurstCount = burstCount,
            VolleyShotsMin = volleyShotsMin,
            VolleyShotsMax = volleyShotsMax,
            VolleyWaitMin = volleyWaitMin,
            VolleyWaitMax = volleyWaitMax,
            WeaponFormId = weaponFormId != 0 ? weaponFormId : null
        };
    }

    /// <summary>
    ///     Parse PTDT/PTD2 subrecord (16 bytes).
    ///     [0]=Type, [1-3]=pad, [4-7]=FormID/Union, [8-11]=CountDistance, [12-15]=AcquireRadius.
    /// </summary>
    internal static PackageTarget ParsePackageTarget(ReadOnlySpan<byte> data, bool isBigEndian)
    {
        var type = data[0];

        var formIdOrType = isBigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data[4..])
            : BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);

        var countDistance = isBigEndian
            ? BinaryPrimitives.ReadInt32BigEndian(data[8..])
            : BinaryPrimitives.ReadInt32LittleEndian(data[8..]);

        var acquireRadius = isBigEndian
            ? BinaryPrimitives.ReadSingleBigEndian(data[12..])
            : BinaryPrimitives.ReadSingleLittleEndian(data[12..]);

        return new PackageTarget
        {
            Type = type,
            FormIdOrType = formIdOrType,
            CountDistance = countDistance,
            AcquireRadius = acquireRadius
        };
    }

    private static PackageLocation ParsePackageLocation(ReadOnlySpan<byte> data, bool isBigEndian)
    {
        var type = data[0];
        var union = isBigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data[4..])
            : BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        var radius = isBigEndian
            ? BinaryPrimitives.ReadInt32BigEndian(data[8..])
            : BinaryPrimitives.ReadInt32LittleEndian(data[8..]);

        return new PackageLocation
        {
            Type = type,
            Union = union,
            Radius = radius
        };
    }
}
