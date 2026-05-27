using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.AI;
using FalloutXbox360Utils.Core.Formats.Esm.Parsing.Handlers;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

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

        // FormType-drift guard. When RuntimeBuildOffsets.DetectFormTypeDrift fails
        // to remap entries (e.g. release_dump, where the ESM has no PACKs at 0x49
        // to cross-reference), IDLE animation TESForms reach this reader at the
        // PACK FormType byte. Their TESForm header passes the FormID + packType
        // gates above, producing an all-null PackageRecord stub that downstream
        // (PackEncoder.EncodeNew) emits as a zero-filled PLDT/PTDT record.
        // Real PACKs have pPackLoc that is either NULL or a valid Xbox 360 heap
        // pointer (0x40000000-0x7FFFFFFF); IDLE structs uniformly read
        // 0x00CBCB17 (uninit fill) at +60. (pPackTarg @ +64 isn't a reliable
        // discriminator — IDLE structs happen to have a heap pointer there too,
        // overlapping with the next field in their own layout.)
        var pLocPtr = BinaryUtils.ReadUInt32BE(buffer, PackLocPtrOffset);
        if (pLocPtr != 0 && !_context.IsValidPointer(pLocPtr))
        {
            return null;
        }

        // Follow pPackLoc pointer to read PackageLocation
        var location = ReadLocation(buffer);

        // Follow pPackTarg pointer to read PackageTarget
        var target = ReadTarget(buffer);

        // pIdleCollection @ PDB +68. BGSIdleCollection's inner fields are also present in
        // the PDB layout: cIdleFlags(+68), cIdleCount(+69), pIdleArray(+72), timer(+76).
        var idleCollection = ReadIdleCollection(buffer);

        // pCombatStyle @ PDB +88 (TESCombatStyle*, FormType 0x4A). Constrain by FormType so a
        // stale pointer that resolves to a non-CSTY form is dropped rather than emitted as a
        // bogus CNAM. The CombatStylePtrOffset already includes the build-specific _s shift.
        var combatStyleFormId = _context.FollowPointerToFormId(
            buffer, CombatStylePtrOffset, CstyFormType);

        var conditions = new RuntimeDialogueConditionReader(_context)
            .ReadConditions(offset, PackConditionsOffset)
            .Conditions;

        return new PackageRecord
        {
            FormId = formId,
            EditorId = entry.EditorId,
            Data = packageData,
            Schedule = schedule,
            Location = location,
            Target = target,
            Conditions = conditions,
            IdleCollection = idleCollection,
            CombatStyleFormId = combatStyleFormId,
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

        // Sanitize uninitialized heap memory (0xCD/0xCC fill from MS CRT debug allocator).
        // The game engine allocates TESPackage structs but doesn't always initialize all fields.
        packFlags = AiRecordHandler.SanitizeFlags32(packFlags);
        foBehavior = AiRecordHandler.SanitizeFlags16(foBehavior);
        typeSpecific = AiRecordHandler.SanitizeFlags16(typeSpecific);

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

    private PackageIdleCollection? ReadIdleCollection(byte[] buffer)
    {
        if (IdleCollectionPtrOffset + 4 > buffer.Length)
        {
            return null;
        }

        var collectionPtr = BinaryUtils.ReadUInt32BE(buffer, IdleCollectionPtrOffset);
        if (collectionPtr == 0 || !_context.IsValidPointer(collectionPtr))
        {
            return null;
        }

        var collectionOffset = _context.VaToFileOffset(collectionPtr);
        if (collectionOffset == null)
        {
            return null;
        }

        var collectionBuf = _context.ReadBytes(collectionOffset.Value, IdleCollectionStructMinSize);
        if (collectionBuf == null)
        {
            return null;
        }

        var flags = collectionBuf[IdleFlagsOffset];
        var count = collectionBuf[IdleCountOffset];
        if (count > MaxIdleCollectionCount)
        {
            return null;
        }

        var timer = BinaryUtils.ReadFloatBE(collectionBuf, IdleTimerOffset);
        if (!RuntimeMemoryContext.IsNormalFloat(timer))
        {
            timer = 0;
        }

        var idleFormIds = ReadIdleArrayFormIds(collectionBuf, count);
        return new PackageIdleCollection
        {
            Flags = flags,
            Count = (byte)idleFormIds.Count,
            TimerCheckForIdle = timer,
            IdleAnimationFormIds = idleFormIds
        };
    }

    private List<uint> ReadIdleArrayFormIds(byte[] collectionBuf, byte count)
    {
        if (count == 0)
        {
            return [];
        }

        var arrayPtr = BinaryUtils.ReadUInt32BE(collectionBuf, IdleArrayPtrOffset);
        if (arrayPtr == 0 || !_context.IsValidPointer(arrayPtr))
        {
            return [];
        }

        var arrayOffset = _context.VaToFileOffset(arrayPtr);
        if (arrayOffset == null)
        {
            return [];
        }

        var arrayBuf = _context.ReadBytes(arrayOffset.Value, count * 4);
        if (arrayBuf == null)
        {
            return [];
        }

        var ids = new List<uint>(count);
        for (var i = 0; i < count; i++)
        {
            var idlePtr = BinaryUtils.ReadUInt32BE(arrayBuf, i * 4);
            if (_context.FollowPointerVaToFormId(idlePtr, IdleFormType) is { } idleFormId)
            {
                ids.Add(idleFormId);
            }
        }

        return ids;
    }

    #region TESPackage Struct Layout (Proto Debug PDB base + _s)

    // TESPackage: PDB size 128 (proto), +_s shift on later builds. CombatStyle ptr at +88
    // pushes the minimum read to 92 bytes — within the 128-byte default but explicit here.
    private int PackStructSize => 128 + _s;
    private int PackDataOffset => 28 + _s; // PACKAGE_DATA (12 bytes inline)
    private int PackLocPtrOffset => 44 + _s; // PackageLocation* pPackLoc
    private int PackTargPtrOffset => 48 + _s; // PackageTarget* pPackTarg
    private int IdleCollectionPtrOffset => 52 + _s; // BGSIdleCollection* pIdleCollection (PDB +68)
    private int PackSchedOffset => 56 + _s; // PackageSchedule (8 bytes inline)
    private int PackConditionsOffset => 64 + _s; // TESCondition packConditions (PDB +80)
    // pCombatStyle: PDB +88. The constant is `pdb - _s = 88 - 16 = 72`. Was
    // mistakenly `88 + _s = +104` until the Phase 1B.6 follow-up — that landed inside
    // the OnBegin PackageEventAction struct (+92..+107), so the typed-pointer gate
    // (FollowPointerToFormId expecting FormType 0x4A=CSTY) rejected every read and
    // PackageRecord.CombatStyleFormId was always null in production. Pinned by
    // PackageTerminalOffsetInvestigationTests.PACK_pCombatStyle_offset_groundtruth.
    private int CombatStylePtrOffset => 72 + _s; // TESCombatStyle* pCombatStyle (PDB +88)

    private const byte CstyFormType = 0x4A;

    // PackageLocation (12 bytes)
    private const int LocTypeOffset = 0; // eLocType (char)
    private const int LocRadiusOffset = 4; // iRad (uint32)
    private const int LocUnionOffset = 8; // union: pObject/pRef/pCell/eObjType (pointer)

    // PackageTarget (16 bytes)
    private const int TargTypeOffset = 0; // eTargType (uchar)
    private const int TargUnionOffset = 4; // union: pointer/enum
    private const int TargValueOffset = 8; // iValue (int32)
    private const int TargRadiusOffset = 12; // fAcquireRadius (float)

    // BGSIdleCollection (PDB field offsets in pointed-to object)
    private const int IdleCollectionStructMinSize = 80;
    private const int IdleFlagsOffset = 68;
    private const int IdleCountOffset = 69;
    private const int IdleArrayPtrOffset = 72;
    private const int IdleTimerOffset = 76;
    private const int MaxIdleCollectionCount = 64;
    private const byte IdleFormType = 0x48;

    #endregion
}
