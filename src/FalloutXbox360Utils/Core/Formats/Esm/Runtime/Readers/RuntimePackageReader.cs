using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Reader for TESPackage runtime structs from Xbox 360 memory dumps.
///     Extracts AI package data (type, flags, location, target, schedule) using PDB-verified offsets.
/// </summary>
internal sealed class RuntimePackageReader(RuntimeMemoryContext context)
{
    private readonly RuntimeMemoryContext _context = context;

    // Build-specific offset shift: Proto Debug PDB + _s = actual dump offset.
    private readonly int _s = RuntimeBuildOffsets.GetPdbShift(
        MinidumpAnalyzer.DetectBuildType(context.MinidumpInfo));

    #region TESPackage Struct Layout (Proto Debug PDB base + _s)

    // TESPackage: PDB size 128
    private int PackStructSize => 128 + _s;
    private int PackDataOffset => 28 + _s;       // PACKAGE_DATA (12 bytes inline)
    private int PackLocPtrOffset => 44 + _s;     // PackageLocation* pPackLoc
    private int PackTargPtrOffset => 48 + _s;    // PackageTarget* pPackTarg
    private int PackSchedOffset => 56 + _s;      // PackageSchedule (8 bytes inline)

    // PackageLocation (12 bytes)
    private const int LocTypeOffset = 0;         // eLocType (char)
    private const int LocRadiusOffset = 4;        // iRad (uint32)
    private const int LocUnionOffset = 8;         // union: pObject/pRef/pCell/eObjType (pointer)

    // PackageTarget (16 bytes)
    private const int TargTypeOffset = 0;         // eTargType (uchar)
    private const int TargUnionOffset = 4;        // union: pointer/enum
    private const int TargValueOffset = 8;        // iValue (int32)
    private const int TargRadiusOffset = 12;      // fAcquireRadius (float)

    #endregion

    /// <summary>
    ///     Read a TESPackage runtime struct and return a PackageRecord.
    ///     Returns null if the struct cannot be read or validation fails.
    /// </summary>
    public PackageRecord? ReadRuntimePackage(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + PackStructSize > _context.FileSize)
        {
            return null;
        }

        var buffer = new byte[PackStructSize];
        try
        {
            _context.Accessor.ReadArray(offset, buffer, 0, PackStructSize);
        }
        catch
        {
            return null;
        }

        // Validate FormID at standard TESForm offset +12
        var formId = BinaryUtils.ReadUInt32BE(buffer, 12);
        if (formId != entry.FormId)
        {
            return null;
        }

        // Read inline PACKAGE_DATA at +28+_s (12 bytes)
        var packageData = ReadPackageData(buffer);

        // Read inline PackageSchedule at +56+_s (8 bytes)
        var schedule = ReadSchedule(buffer);

        // Follow pPackLoc pointer to read PackageLocation
        var location = ReadLocation(buffer);

        // Follow pPackTarg pointer to read PackageTarget
        var target = ReadTarget(buffer);

        return new PackageRecord
        {
            FormId = formId,
            EditorId = entry.EditorId,
            Data = packageData,
            Schedule = schedule,
            Location = location,
            Target = target,
            Offset = offset,
            IsBigEndian = true
        };
    }

    private PackageData? ReadPackageData(byte[] buffer)
    {
        if (PackDataOffset + 12 > buffer.Length)
        {
            return null;
        }

        // PACKAGE_DATA layout: iPackFlags (uint32), cPackType (char), pad, iFOBehaviorFlags (ushort), iPackageSpecificFlags (ushort)
        var packFlags = BinaryUtils.ReadUInt32BE(buffer, PackDataOffset);
        var packType = buffer[PackDataOffset + 4];
        var foBehavior = BinaryUtils.ReadUInt16BE(buffer, PackDataOffset + 6);
        var typeSpecific = BinaryUtils.ReadUInt16BE(buffer, PackDataOffset + 8);

        // Basic validation: pack type should be in known range
        if (packType > 20)
        {
            return null;
        }

        return new PackageData
        {
            Type = packType,
            GeneralFlags = packFlags,
            FalloutBehaviorFlags = foBehavior,
            TypeSpecificFlags = typeSpecific
        };
    }

    private PackageSchedule? ReadSchedule(byte[] buffer)
    {
        if (PackSchedOffset + 8 > buffer.Length)
        {
            return null;
        }

        // PSData layout: month (sbyte), dayOfWeek (sbyte), date (sbyte), time (sbyte), duration (int32)
        var month = (sbyte)buffer[PackSchedOffset];
        var dayOfWeek = (sbyte)buffer[PackSchedOffset + 1];
        var date = (sbyte)buffer[PackSchedOffset + 2];
        var time = (sbyte)buffer[PackSchedOffset + 3];
        var duration = RuntimeMemoryContext.ReadInt32BE(buffer, PackSchedOffset + 4);

        // Validate: month -1..11, dayOfWeek -1..6, time -1..23, duration 0..744
        if (month is < -1 or > 11 || dayOfWeek is < -1 or > 6 || time is < -1 or > 23 ||
            duration is < 0 or > 744)
        {
            return null;
        }

        return new PackageSchedule
        {
            Month = month,
            DayOfWeek = dayOfWeek,
            Date = date,
            Time = time,
            Duration = duration
        };
    }

    private PackageLocation? ReadLocation(byte[] buffer)
    {
        if (PackLocPtrOffset + 4 > buffer.Length)
        {
            return null;
        }

        var locPtr = BinaryUtils.ReadUInt32BE(buffer, PackLocPtrOffset);
        if (locPtr == 0 || !_context.IsValidPointer(locPtr))
        {
            return null;
        }

        var locFileOffset = _context.VaToFileOffset(locPtr);
        if (locFileOffset == null)
        {
            return null;
        }

        var locBuf = _context.ReadBytes(locFileOffset.Value, 12);
        if (locBuf == null)
        {
            return null;
        }

        var locType = locBuf[LocTypeOffset];
        var radius = RuntimeMemoryContext.ReadInt32BE(locBuf, LocRadiusOffset);

        // The union at +8 is a pointer to a TESForm for types 0 (NearRef), 1 (InCell), 4 (ObjectID).
        // For other types it may be a raw enum value.
        uint unionValue = 0;
        var unionPtr = BinaryUtils.ReadUInt32BE(locBuf, LocUnionOffset);
        if (locType is 0 or 1 or 4 && unionPtr != 0 && _context.IsValidPointer(unionPtr))
        {
            // Follow pointer to get FormID at +12 of the target TESForm
            var targetFileOffset = _context.VaToFileOffset(unionPtr);
            if (targetFileOffset != null)
            {
                var targetBuf = _context.ReadBytes(targetFileOffset.Value, 16);
                if (targetBuf != null)
                {
                    unionValue = BinaryUtils.ReadUInt32BE(targetBuf, 12);
                }
            }
        }
        else
        {
            // Raw enum value (e.g., ObjectType)
            unionValue = unionPtr;
        }

        return new PackageLocation
        {
            Type = locType,
            Union = unionValue,
            Radius = radius
        };
    }

    private PackageTarget? ReadTarget(byte[] buffer)
    {
        if (PackTargPtrOffset + 4 > buffer.Length)
        {
            return null;
        }

        var targPtr = BinaryUtils.ReadUInt32BE(buffer, PackTargPtrOffset);
        if (targPtr == 0 || !_context.IsValidPointer(targPtr))
        {
            return null;
        }

        var targFileOffset = _context.VaToFileOffset(targPtr);
        if (targFileOffset == null)
        {
            return null;
        }

        var targBuf = _context.ReadBytes(targFileOffset.Value, 16);
        if (targBuf == null)
        {
            return null;
        }

        var targType = targBuf[TargTypeOffset];
        var value = RuntimeMemoryContext.ReadInt32BE(targBuf, TargValueOffset);
        var acquireRadius = BinaryUtils.ReadFloatBE(targBuf, TargRadiusOffset);

        // The union at +4 is a pointer to a TESForm for types 0 (SpecificRef), 1 (ObjectID).
        uint formIdOrType = 0;
        var unionPtr = BinaryUtils.ReadUInt32BE(targBuf, TargUnionOffset);
        if (targType is 0 or 1 && unionPtr != 0 && _context.IsValidPointer(unionPtr))
        {
            var targetFileOffset = _context.VaToFileOffset(unionPtr);
            if (targetFileOffset != null)
            {
                var targetBuf = _context.ReadBytes(targetFileOffset.Value, 16);
                if (targetBuf != null)
                {
                    formIdOrType = BinaryUtils.ReadUInt32BE(targetBuf, 12);
                }
            }
        }
        else
        {
            formIdOrType = unionPtr;
        }

        if (!RuntimeMemoryContext.IsNormalFloat(acquireRadius))
        {
            acquireRadius = 0;
        }

        return new PackageTarget
        {
            Type = targType,
            FormIdOrType = formIdOrType,
            CountDistance = value,
            AcquireRadius = acquireRadius
        };
    }
}
