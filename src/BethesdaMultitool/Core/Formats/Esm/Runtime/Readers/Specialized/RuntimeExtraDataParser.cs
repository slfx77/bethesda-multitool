using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Parses BSExtraData linked lists from TESObjectREFR runtime structs.
///     Walks the linked list and extracts placement semantics (ownership, locks,
///     teleport doors, map markers, enable parents, linked refs, etc.).
/// </summary>
internal sealed class RuntimeExtraDataParser(RuntimeMemoryContext context)
{
    private readonly RuntimeMemoryContext _context = context;

    internal RuntimeRefrExtraData ReadExtraData(uint pHead)
    {
        return ReadExtraDataInspection(pHead).Data;
    }

    internal RuntimeRefrExtraDataInspection ReadExtraDataInspection(uint pHead)
    {
        var result = new RuntimeRefrExtraData();
        var typeCounts = new Dictionary<byte, int>();
        if (pHead == 0 || !_context.IsValidPointer(pHead))
        {
            return new RuntimeRefrExtraDataInspection(false, 0, typeCounts, result);
        }

        var visited = new HashSet<uint>();
        var currentVa = pHead;
        var visitedNodeCount = 0;

        for (var i = 0; i < MaxExtraListNodes; i++)
        {
            if (currentVa == 0 || !visited.Add(currentVa))
            {
                break;
            }

            var nodeFileOffset = _context.VaToFileOffset(currentVa);
            if (nodeFileOffset == null)
            {
                break;
            }

            // Read the BSExtraData header first: vfptr(4) + cEtype(1) + pad(3) + pNext(4).
            var nodeBuffer = _context.ReadBytesAtVa(Xbox360MemoryUtils.VaToLong(currentVa), ExtraNodeSize);
            if (nodeBuffer == null)
            {
                break;
            }

            var eType = nodeBuffer[ExtraEtypeOffset];
            var nextVa = BinaryUtils.ReadUInt32BE(nodeBuffer, ExtraNextOffset);
            visitedNodeCount++;
            typeCounts[eType] = typeCounts.GetValueOrDefault(eType) + 1;

            switch (eType)
            {
                case ExtraMapMarkerType:
                {
                    var mapMarker = ReadMapMarkerExtra(currentVa);
                    if (mapMarker.IsMapMarker)
                    {
                        result.IsMapMarker = true;
                        result.MarkerType ??= mapMarker.MarkerType;
                        result.MarkerName ??= mapMarker.MarkerName;
                    }

                    break;
                }
                case ExtraStartingPositionType:
                    result.StartingPosition ??= ReadStartingPosition(currentVa, nodeFileOffset.Value);
                    break;
                case ExtraOwnershipType:
                    result.OwnerFormId ??= ReadOwnerFormId(currentVa);
                    break;
                case ExtraPackageStartLocationType:
                    result.PackageStartLocation ??= ReadPackageStartLocation(currentVa);
                    break;
                case ExtraMerchantContainerType:
                    result.MerchantContainerFormId ??= ReadMerchantContainerFormId(currentVa);
                    break;
                case ExtraPersistentCellType:
                    result.PersistentCellFormId ??= ReadPersistentCellFormId(currentVa);
                    break;
                case ExtraEncounterZoneType:
                    result.EncounterZoneFormId ??= ReadEncounterZoneFormId(currentVa);
                    break;
                case ExtraLockType:
                {
                    var lockData = ReadLockData(currentVa);
                    result.LockLevel ??= lockData.LockLevel;
                    result.LockKeyFormId ??= lockData.LockKeyFormId;
                    result.LockFlags ??= lockData.LockFlags;
                    result.LockNumTries ??= lockData.LockNumTries;
                    result.LockTimesUnlocked ??= lockData.LockTimesUnlocked;
                    break;
                }
                case ExtraTeleportType:
                {
                    var (destinationDoorFormId, teleportPosRot, teleportFlags) =
                        ReadTeleportData(currentVa);
                    if (result.DestinationDoorFormId is null)
                    {
                        result.DestinationDoorFormId = destinationDoorFormId;
                        result.TeleportPosRot = teleportPosRot;
                        result.TeleportFlags = teleportFlags;
                    }

                    break;
                }
                case ExtraEnableStateParentType:
                {
                    var (enableParentFormId, enableParentFlags) = ReadEnableStateParent(currentVa);
                    if (!result.EnableParentFormId.HasValue && enableParentFormId.HasValue)
                    {
                        result.EnableParentFormId = enableParentFormId;
                        result.EnableParentFlags = enableParentFlags;
                    }

                    break;
                }
                case ExtraStartingWorldOrCellType:
                    result.StartingWorldOrCellFormId ??= ReadStartingWorldOrCellFormId(currentVa);
                    break;
                case ExtraLinkedRefType:
                    result.LinkedRefFormId ??= ReadLinkedRefFormId(currentVa);
                    break;
                case ExtraLinkedRefChildrenType:
                {
                    var childFormIds = ReadLinkedRefChildrenFormIds(currentVa);
                    if (result.LinkedRefChildrenFormIds == null && childFormIds.Count > 0)
                    {
                        result.LinkedRefChildrenFormIds = childFormIds;
                    }

                    break;
                }
                case ExtraLeveledCreatureType:
                {
                    var leveledCreatureData = ReadLeveledCreatureData(currentVa);
                    result.LeveledCreatureOriginalBaseFormId ??= leveledCreatureData.OriginalBaseFormId;
                    result.LeveledCreatureTemplateFormId ??= leveledCreatureData.TemplateFormId;
                    break;
                }
                case ExtraRadiusType:
                    result.Radius ??= ReadRadius(currentVa);
                    break;
                case ExtraRadioDataType:
                    result.RadioData ??= ReadRadioData(currentVa);
                    break;
                case ExtraCountType:
                    result.Count ??= ReadCount(currentVa);
                    break;
                case ExtraEditorIDType:
                    result.EditorId ??= ReadEditorId(nodeFileOffset.Value);
                    break;
            }

            currentVa = nextVa;
        }

        return new RuntimeRefrExtraDataInspection(true, visitedNodeCount, typeCounts, result);
    }

    #region Individual Extra Data Type Readers

    private (bool IsMapMarker, ushort? MarkerType, string? MarkerName) ReadMapMarkerExtra(uint nodeVa)
    {
        var nodeBuffer = _context.ReadBytesAtVa(Xbox360MemoryUtils.VaToLong(nodeVa), ExtraPointerNodeSize);
        if (nodeBuffer == null)
        {
            return (true, null, null);
        }

        var pMapData = BinaryUtils.ReadUInt32BE(nodeBuffer, ExtraPayloadPtrOffset);
        if (pMapData == 0 || !_context.IsValidPointer(pMapData))
        {
            return (true, null, null);
        }

        var mapDataFileOffset = _context.VaToFileOffset(pMapData);
        return mapDataFileOffset == null
            ? (true, null, null)
            : ReadMapMarkerData(pMapData, mapDataFileOffset.Value);
    }

    /// <summary>
    ///     Read MapMarkerData struct at the given virtual address.
    ///     Layout: TESFullName(0-11) + cFlags(12) + cOriginalFlags(13) + sType(14, uint16) + pReputation(16)
    /// </summary>
    private (bool IsMapMarker, ushort? MarkerType, string? MarkerName) ReadMapMarkerData(
        uint mapDataVa,
        long mapDataFileOffset)
    {
        var mapBuffer = _context.ReadBytesAtVa(Xbox360MemoryUtils.VaToLong(mapDataVa), 20);
        if (mapBuffer == null)
        {
            return (true, null, null);
        }

        var markerType = BinaryUtils.ReadUInt16BE(mapBuffer, MapMarkerTypeOffset);
        var markerName = _context.ReadBsStringT(mapDataFileOffset, MapMarkerNameFieldOffset);

        return (true, markerType, markerName);
    }

    private uint? ReadOwnerFormId(uint nodeVa)
    {
        var nodeBuffer = _context.ReadBytesAtVa(Xbox360MemoryUtils.VaToLong(nodeVa), ExtraPointerNodeSize);
        if (nodeBuffer == null)
        {
            return null;
        }

        var ownerVa = BinaryUtils.ReadUInt32BE(nodeBuffer, ExtraPayloadPtrOffset);
        return _context.FollowPointerVaToFormId(ownerVa);
    }

    private PositionSubrecord? ReadStartingPosition(uint nodeVa, long nodeFileOffset)
    {
        var nodeBuffer = _context.ReadBytesAtVa(
            Xbox360MemoryUtils.VaToLong(nodeVa), ExtraStartingPositionNodeSize);
        if (nodeBuffer == null)
        {
            return null;
        }

        var x = ReadValidatedWorldCoord(nodeBuffer, ExtraPayloadPtrOffset);
        var y = ReadValidatedWorldCoord(nodeBuffer, ExtraPayloadPtrOffset + 4);
        var z = ReadValidatedWorldCoord(nodeBuffer, ExtraPayloadPtrOffset + 8);
        var rotX = ReadValidatedRotation(nodeBuffer, ExtraPayloadPtrOffset + 12);
        var rotY = ReadValidatedRotation(nodeBuffer, ExtraPayloadPtrOffset + 16);
        var rotZ = ReadValidatedRotation(nodeBuffer, ExtraPayloadPtrOffset + 20);
        if (x == null || y == null || z == null || rotX == null || rotY == null || rotZ == null)
        {
            return null;
        }

        return new PositionSubrecord(
            x.Value, y.Value, z.Value,
            rotX.Value, rotY.Value, rotZ.Value,
            nodeFileOffset + ExtraPayloadPtrOffset, true);
    }

    private RuntimePackageStartLocation? ReadPackageStartLocation(uint nodeVa)
    {
        var nodeBuffer = _context.ReadBytesAtVa(
            Xbox360MemoryUtils.VaToLong(nodeVa), ExtraPackageStartLocationNodeSize);
        if (nodeBuffer == null)
        {
            return null;
        }

        var locationVa = BinaryUtils.ReadUInt32BE(nodeBuffer, ExtraPayloadPtrOffset);
        var locationFormId = _context.FollowPointerVaToFormId(locationVa);
        if (locationFormId is not > 0)
        {
            return null;
        }

        var x = ReadValidatedWorldCoord(nodeBuffer, ExtraPayloadPtrOffset + 4);
        var y = ReadValidatedWorldCoord(nodeBuffer, ExtraPayloadPtrOffset + 8);
        var z = ReadValidatedWorldCoord(nodeBuffer, ExtraPayloadPtrOffset + 12);
        var rotZ = ReadValidatedRotation(nodeBuffer, ExtraPayloadPtrOffset + 16);
        if (x == null || y == null || z == null || rotZ == null)
        {
            return null;
        }

        return new RuntimePackageStartLocation(locationFormId, x.Value, y.Value, z.Value, rotZ.Value);
    }

    private uint? ReadMerchantContainerFormId(uint nodeVa)
    {
        var nodeBuffer = _context.ReadBytesAtVa(Xbox360MemoryUtils.VaToLong(nodeVa), ExtraPointerNodeSize);
        if (nodeBuffer == null)
        {
            return null;
        }

        var containerVa = BinaryUtils.ReadUInt32BE(nodeBuffer, ExtraPayloadPtrOffset);
        return ReadPlacedRefFormId(containerVa);
    }

    private uint? ReadPersistentCellFormId(uint nodeVa)
    {
        var nodeBuffer = _context.ReadBytesAtVa(Xbox360MemoryUtils.VaToLong(nodeVa), ExtraPointerNodeSize);
        if (nodeBuffer == null)
        {
            return null;
        }

        var cellVa = BinaryUtils.ReadUInt32BE(nodeBuffer, ExtraPayloadPtrOffset);
        return _context.FollowPointerVaToFormId(cellVa, 0x39);
    }

    private uint? ReadStartingWorldOrCellFormId(uint nodeVa)
    {
        var nodeBuffer = _context.ReadBytesAtVa(
            Xbox360MemoryUtils.VaToLong(nodeVa), ExtraStartingWorldOrCellNodeSize);
        if (nodeBuffer == null)
        {
            return null;
        }

        var formVa = BinaryUtils.ReadUInt32BE(nodeBuffer, ExtraPayloadPtrOffset);
        return _context.FollowPointerVaToFormId(formVa);
    }

    private uint? ReadEncounterZoneFormId(uint nodeVa)
    {
        var nodeBuffer = _context.ReadBytesAtVa(Xbox360MemoryUtils.VaToLong(nodeVa), ExtraPointerNodeSize);
        if (nodeBuffer == null)
        {
            return null;
        }

        var zoneVa = BinaryUtils.ReadUInt32BE(nodeBuffer, ExtraPayloadPtrOffset);
        return _context.FollowPointerVaToFormId(zoneVa, 0x61);
    }

    private RuntimeLeveledCreatureData ReadLeveledCreatureData(uint nodeVa)
    {
        var nodeBuffer = _context.ReadBytesAtVa(
            Xbox360MemoryUtils.VaToLong(nodeVa), ExtraLeveledCreatureNodeSize);
        if (nodeBuffer == null)
        {
            return new RuntimeLeveledCreatureData();
        }

        return new RuntimeLeveledCreatureData
        {
            OriginalBaseFormId = _context.FollowPointerVaToFormId(
                BinaryUtils.ReadUInt32BE(nodeBuffer, ExtraPayloadPtrOffset)),
            TemplateFormId = _context.FollowPointerVaToFormId(
                BinaryUtils.ReadUInt32BE(nodeBuffer, ExtraPayloadPtrOffset + 4))
        };
    }

    private float? ReadRadius(uint nodeVa)
    {
        var nodeBuffer = _context.ReadBytesAtVa(Xbox360MemoryUtils.VaToLong(nodeVa), ExtraPointerNodeSize);
        if (nodeBuffer == null)
        {
            return null;
        }

        var radius = BinaryUtils.ReadFloatBE(nodeBuffer, ExtraPayloadPtrOffset);
        // ExtraRadius is signed. Keep the corruption guard symmetric so negative retail
        // adjustments survive runtime/DMP extraction just like their on-disk XRDS form.
        return RuntimeMemoryContext.IsNormalFloat(radius) && MathF.Abs(radius) <= 500_000f
            ? radius
            : null;
    }

    /// <summary>
    ///     Reads an <c>ExtraRadioData</c> node (28 bytes: the 12-byte BSExtraData base plus a
    ///     16-byte <c>RADIO_DATA</c>). Returns null when the block fails validation rather than
    ///     handing the encoder a partly-garbage broadcast configuration.
    /// </summary>
    private RadioData? ReadRadioData(uint nodeVa)
    {
        var nodeBuffer = _context.ReadBytesAtVa(Xbox360MemoryUtils.VaToLong(nodeVa), ExtraRadioDataNodeSize);
        if (nodeBuffer == null)
        {
            return null;
        }

        var radius = BinaryUtils.ReadFloatBE(nodeBuffer, RadioDataRadiusOffset);
        var rangeType = BinaryUtils.ReadUInt32BE(nodeBuffer, RadioDataRangeTypeOffset);
        var staticPct = BinaryUtils.ReadFloatBE(nodeBuffer, RadioDataStaticPctOffset);

        // RADIO_RANGE_COUNT is 5, so anything at or above it is not a range type — the surest
        // sign the node is not what we think it is.
        if (rangeType >= RadioRangeTypeCount)
        {
            return null;
        }

#pragma warning disable S1244 // exact bit-pattern zero is the accepted non-normal value when validating carved memory
        if (!RuntimeMemoryContext.IsNormalFloat(radius) && radius != 0f)
        {
            return null;
        }

        if (!RuntimeMemoryContext.IsNormalFloat(staticPct) && staticPct != 0f)
        {
            return null;
        }
#pragma warning restore S1244

        return new RadioData
        {
            Radius = radius,
            RangeType = rangeType,
            StaticPercentage = staticPct,
            PositionRefFormId = _context.FollowPointerVaToFormId(
                BinaryUtils.ReadUInt32BE(nodeBuffer, RadioDataPositionRefOffset))
        };
    }

    private short? ReadCount(uint nodeVa)
    {
        var nodeBuffer = _context.ReadBytesAtVa(Xbox360MemoryUtils.VaToLong(nodeVa), ExtraPointerNodeSize);
        if (nodeBuffer == null)
        {
            return null;
        }

        return BinaryUtils.ReadInt16BE(nodeBuffer, ExtraPayloadPtrOffset);
    }

    private string? ReadEditorId(long nodeFileOffset)
    {
        return _context.ReadBsStringT(nodeFileOffset, ExtraPayloadPtrOffset);
    }

    /// <summary>
    ///     Reads the full <c>DoorTeleportData</c> the ExtraTeleport node points at — 32 bytes per
    ///     the MemDebug PDB: <c>pLinkedDoor</c> @0, <c>position</c> NiPoint3 @4, <c>rotation</c>
    ///     NiPoint3 @16, <c>cFlags</c> @28 — a 1:1 match for the on-disk XTEL subrecord.
    ///     <para>
    ///         Before v143 only the first 4 bytes (the door pointer) were read, so every
    ///         runtime-recovered XTEL shipped with a zeroed arrival transform and the player landed at
    ///         the destination cell's origin instead of in front of the return door. The transform was
    ///         in the dump all along.
    ///     </para>
    /// </summary>
    private (uint? DestinationDoorFormId, PositionSubrecord? PosRot, byte? Flags) ReadTeleportData(
        uint nodeVa)
    {
        var nodeBuffer = _context.ReadBytesAtVa(Xbox360MemoryUtils.VaToLong(nodeVa), ExtraPointerNodeSize);
        if (nodeBuffer == null)
        {
            return (null, null, null);
        }

        var teleportDataVa = BinaryUtils.ReadUInt32BE(nodeBuffer, ExtraPayloadPtrOffset);
        if (teleportDataVa == 0 || !_context.IsValidPointer(teleportDataVa))
        {
            return (null, null, null);
        }

        var teleportDataFileOffset = _context.VaToFileOffset(teleportDataVa);
        if (teleportDataFileOffset == null)
        {
            return (null, null, null);
        }

        var teleportBuffer = _context.ReadBytesAtVa(
            Xbox360MemoryUtils.VaToLong(teleportDataVa), DoorTeleportDataSize);
        if (teleportBuffer == null)
        {
            return (null, null, null);
        }

        var linkedDoorVa = BinaryUtils.ReadUInt32BE(teleportBuffer);
        var destinationDoorFormId = ReadPlacedRefFormId(linkedDoorVa);
        if (destinationDoorFormId is null)
        {
            return (null, null, null);
        }

        // Validate each axis independently (zero is legal — RotX/RotY usually are zero). Any
        // implausible axis means the block is not what we think it is, so keep the door link but
        // decline the transform rather than emit a partly-garbage arrival point.
        var x = ReadValidatedWorldCoord(teleportBuffer, DoorTeleportPositionOffset);
        var y = ReadValidatedWorldCoord(teleportBuffer, DoorTeleportPositionOffset + 4);
        var z = ReadValidatedWorldCoord(teleportBuffer, DoorTeleportPositionOffset + 8);
        var rotX = ReadValidatedRotation(teleportBuffer, DoorTeleportRotationOffset);
        var rotY = ReadValidatedRotation(teleportBuffer, DoorTeleportRotationOffset + 4);
        var rotZ = ReadValidatedRotation(teleportBuffer, DoorTeleportRotationOffset + 8);

        PositionSubrecord? posRot = null;
        if (x.HasValue && y.HasValue && z.HasValue && rotX.HasValue && rotY.HasValue && rotZ.HasValue)
        {
            // An all-zero transform is a sentinel for "never authored", not a real arrival point
            // (retail has 0 of 1,108 XTELs zeroed) — treat it as absent.
#pragma warning disable S1244 // all-zero transform is the authored-absent sentinel; exact comparison intended
            var allZero = x.Value == 0f && y.Value == 0f && z.Value == 0f &&
                          rotX.Value == 0f && rotY.Value == 0f && rotZ.Value == 0f;
#pragma warning restore S1244
            if (!allZero)
            {
                posRot = new PositionSubrecord(
                    x.Value, y.Value, z.Value, rotX.Value, rotY.Value, rotZ.Value,
                    teleportDataFileOffset.Value + DoorTeleportPositionOffset, true);
            }
        }

        return (destinationDoorFormId, posRot, teleportBuffer[DoorTeleportFlagsOffset]);
    }

    private (uint? ParentFormId, byte? Flags) ReadEnableStateParent(uint nodeVa)
    {
        var nodeBuffer = _context.ReadBytesAtVa(
            Xbox360MemoryUtils.VaToLong(nodeVa), ExtraEnableStateParentNodeSize);
        if (nodeBuffer == null)
        {
            return (null, null);
        }

        var parentVa = BinaryUtils.ReadUInt32BE(nodeBuffer, ExtraPayloadPtrOffset);
        var parentFormId = ReadPlacedRefFormId(parentVa);
        return parentFormId.HasValue
            ? (parentFormId, nodeBuffer[ExtraEnableParentFlagsOffset])
            : (null, null);
    }

    private uint? ReadLinkedRefFormId(uint nodeVa)
    {
        var nodeBuffer = _context.ReadBytesAtVa(Xbox360MemoryUtils.VaToLong(nodeVa), ExtraPointerNodeSize);
        if (nodeBuffer == null)
        {
            return null;
        }

        var linkedRefVa = BinaryUtils.ReadUInt32BE(nodeBuffer, ExtraPayloadPtrOffset);
        return ReadPlacedRefFormId(linkedRefVa);
    }

    private List<uint> ReadLinkedRefChildrenFormIds(uint nodeVa)
    {
        var nodeBuffer = _context.ReadBytesAtVa(
            Xbox360MemoryUtils.VaToLong(nodeVa), ExtraLinkedRefChildrenNodeSize);
        if (nodeBuffer == null)
        {
            return [];
        }

        return ReadPlacedRefSimpleList(nodeBuffer, ExtraPayloadPtrOffset);
    }

    private RuntimeLockData ReadLockData(uint nodeVa)
    {
        var nodeBuffer = _context.ReadBytesAtVa(Xbox360MemoryUtils.VaToLong(nodeVa), ExtraPointerNodeSize);
        if (nodeBuffer == null)
        {
            return new RuntimeLockData();
        }

        var lockVa = BinaryUtils.ReadUInt32BE(nodeBuffer, ExtraPayloadPtrOffset);
        if (lockVa == 0 || !_context.IsValidPointer(lockVa))
        {
            return new RuntimeLockData();
        }

        var lockBuffer = _context.ReadBytesAtVa(Xbox360MemoryUtils.VaToLong(lockVa), RefrLockSize);
        if (lockBuffer == null)
        {
            return new RuntimeLockData();
        }

        return new RuntimeLockData
        {
            LockLevel = lockBuffer[RefrLockLevelOffset],
            LockKeyFormId = _context.FollowPointerVaToFormId(
                BinaryUtils.ReadUInt32BE(lockBuffer, RefrLockKeyOffset)),
            LockFlags = lockBuffer[RefrLockFlagsOffset],
            LockNumTries = BinaryUtils.ReadUInt32BE(lockBuffer, RefrLockNumTriesOffset),
            LockTimesUnlocked = BinaryUtils.ReadUInt32BE(lockBuffer, RefrLockTimesUnlockedOffset)
        };
    }

    #endregion

    #region Placed Ref Helpers

    private uint? ReadPlacedRefFormId(uint va)
    {
        if (va == 0)
        {
            return null;
        }

        var formBuffer = _context.ReadBytesAtVa(Xbox360MemoryUtils.VaToLong(va), 16);
        if (formBuffer == null)
        {
            return null;
        }

        var formType = formBuffer[4];
        if (formType < 0x3A || formType > 0x3C)
        {
            return null;
        }

        var formId = BinaryUtils.ReadUInt32BE(formBuffer, 12);
        return formId is 0 or 0xFFFFFFFF ? null : formId;
    }

    private List<uint> ReadPlacedRefSimpleList(byte[] buffer, int listHeadOffset)
    {
        var formIds = new List<uint>();
        if (listHeadOffset + 8 > buffer.Length)
        {
            return formIds;
        }

        var itemPtr = BinaryUtils.ReadUInt32BE(buffer, listHeadOffset);
        var nextPtr = BinaryUtils.ReadUInt32BE(buffer, listHeadOffset + 4);

        AddPlacedRefFormId(formIds, itemPtr);

        var visited = new HashSet<uint>();
        while (nextPtr != 0 &&
               formIds.Count < RuntimeMemoryContext.MaxListItems &&
               _context.IsValidPointer(nextPtr) &&
               visited.Add(nextPtr))
        {
            var nodeBuffer = _context.ReadBytesAtVa(Xbox360MemoryUtils.VaToLong(nextPtr), 8);
            if (nodeBuffer == null)
            {
                break;
            }

            itemPtr = BinaryUtils.ReadUInt32BE(nodeBuffer);
            nextPtr = BinaryUtils.ReadUInt32BE(nodeBuffer, 4);
            AddPlacedRefFormId(formIds, itemPtr);
        }

        return formIds;
    }

    private void AddPlacedRefFormId(List<uint> formIds, uint itemPtr)
    {
        var formId = ReadPlacedRefFormId(itemPtr);
        if (formId is > 0 && !formIds.Contains(formId.Value))
        {
            formIds.Add(formId.Value);
        }
    }

    #endregion

    #region Validation Helpers

    internal static float? ReadValidatedWorldCoord(byte[] buffer, int offset)
    {
        if (offset + 4 > buffer.Length)
        {
            return null;
        }

        var value = BinaryUtils.ReadFloatBE(buffer, offset);
        return RuntimeMemoryContext.IsNormalFloat(value) && Math.Abs(value) <= 500_000f
            ? value
            : null;
    }

    internal static float? ReadValidatedRotation(byte[] buffer, int offset)
    {
        if (offset + 4 > buffer.Length)
        {
            return null;
        }

        var value = BinaryUtils.ReadFloatBE(buffer, offset);
        return RuntimeMemoryContext.IsNormalFloat(value) && value >= -10f && value <= 10f
            ? value
            : null;
    }

    #endregion

    #region BSExtraData Constants

    // BSExtraData node layout (12 bytes): vfptr(0) + cEtype(1) + pad(3) + pNext(4)
    private const int ExtraNodeSize = 12;
    private const int ExtraEtypeOffset = 4;
    private const int ExtraNextOffset = 8;

    // Common extra-data node shapes.
    private const int ExtraPointerNodeSize = 16;
    private const int ExtraEnableStateParentNodeSize = 20;
    private const int ExtraPayloadPtrOffset = 12;
    private const int ExtraEnableParentFlagsOffset = 16;

    // Extra data type IDs from EXTRA_DATA_TYPE in the final debug PDB.
    private const byte ExtraPersistentCellType = 0x0C;
    private const byte ExtraStartingPositionType = 0x0F;
    private const byte ExtraOwnershipType = 0x21;
    private const byte ExtraPackageStartLocationType = 0x18;
    private const byte ExtraMerchantContainerType = 0x3C;
    private const byte ExtraLockType = 0x2A;
    private const byte ExtraTeleportType = 0x2B;
    private const byte ExtraMapMarkerType = 0x2C;
    private const byte ExtraLeveledCreatureType = 0x2E;
    private const byte ExtraEnableStateParentType = 0x37;
    private const byte ExtraRadiusType = 0x5C;
    private const byte ExtraStartingWorldOrCellType = 0x49;
    private const byte ExtraEncounterZoneType = 0x74;
    private const byte ExtraCountType = 0x15;
    private const byte ExtraLinkedRefType = 0x51;
    private const byte ExtraLinkedRefChildrenType = 0x52;
    private const byte ExtraEditorIDType = 0x62;

    /// <summary>
    ///     <c>EXTRA_RADIO_DATA = 104</c> in the MemDebug PDB's EXTRA_DATA_TYPE enum. The node is
    ///     28 bytes: the 12-byte BSExtraData base plus a 16-byte <c>RADIO_DATA</c> at +12, whose
    ///     four members line up exactly with the on-disk XRDO subrecord.
    /// </summary>
    private const byte ExtraRadioDataType = 0x68;

    private const int ExtraRadioDataNodeSize = 28;
    private const int RadioDataRadiusOffset = 12;
    private const int RadioDataRangeTypeOffset = 16;
    private const int RadioDataStaticPctOffset = 20;
    private const int RadioDataPositionRefOffset = 24;

    /// <summary>RADIO_RANGE_COUNT from the PDB's RADIO_RANGE_TYPE enum — the first invalid value.</summary>
    private const uint RadioRangeTypeCount = 5;

    private const int ExtraLinkedRefChildrenNodeSize = 20;
    private const int ExtraStartingPositionNodeSize = 36;
    private const int ExtraPackageStartLocationNodeSize = 32;
    private const int ExtraStartingWorldOrCellNodeSize = 16;
    private const int ExtraLeveledCreatureNodeSize = 20;

    // MapMarkerData (20 bytes)
    private const int MapMarkerNameFieldOffset = 4;
    private const int MapMarkerTypeOffset = 14;

    // DoorTeleportData — 32 bytes per the MemDebug PDB:
    // pLinkedDoor @0, position NiPoint3 @4, rotation NiPoint3 @16, cFlags @28.
    private const int DoorTeleportDataSize = 32;
    private const int DoorTeleportPositionOffset = 4;
    private const int DoorTeleportRotationOffset = 16;
    private const int DoorTeleportFlagsOffset = 28;

    // REFR_LOCK
    private const int RefrLockSize = 20;
    private const int RefrLockLevelOffset = 0;
    private const int RefrLockKeyOffset = 4;
    private const int RefrLockFlagsOffset = 8;
    private const int RefrLockNumTriesOffset = 12;
    private const int RefrLockTimesUnlockedOffset = 16;

    private const int MaxExtraListNodes = 100;

    #endregion

    #region Data Types

    internal struct RuntimeRefrExtraData
    {
        public bool IsMapMarker { get; set; }
        public ushort? MarkerType { get; set; }
        public string? MarkerName { get; set; }
        public uint? OwnerFormId { get; set; }
        public uint? EncounterZoneFormId { get; set; }
        public PositionSubrecord? StartingPosition { get; set; }
        public uint? StartingWorldOrCellFormId { get; set; }
        public RuntimePackageStartLocation? PackageStartLocation { get; set; }
        public uint? MerchantContainerFormId { get; set; }
        public uint? LeveledCreatureOriginalBaseFormId { get; set; }
        public uint? LeveledCreatureTemplateFormId { get; set; }
        public float? Radius { get; set; }
        public byte? LockLevel { get; set; }
        public uint? LockKeyFormId { get; set; }
        public byte? LockFlags { get; set; }
        public uint? LockNumTries { get; set; }
        public uint? LockTimesUnlocked { get; set; }
        public uint? DestinationDoorFormId { get; set; }
        public PositionSubrecord? TeleportPosRot { get; set; }
        public byte? TeleportFlags { get; set; }
        public uint? EnableParentFormId { get; set; }
        public byte? EnableParentFlags { get; set; }
        public uint? PersistentCellFormId { get; set; }
        public uint? LinkedRefFormId { get; set; }
        public List<uint>? LinkedRefChildrenFormIds { get; set; }
        public short? Count { get; set; }
        public string? EditorId { get; set; }
        public RadioData? RadioData { get; set; }
    }

    internal readonly record struct RuntimeLockData
    {
        public byte? LockLevel { get; init; }
        public uint? LockKeyFormId { get; init; }
        public byte? LockFlags { get; init; }
        public uint? LockNumTries { get; init; }
        public uint? LockTimesUnlocked { get; init; }
    }

    internal readonly record struct RuntimeLeveledCreatureData
    {
        public uint? OriginalBaseFormId { get; init; }
        public uint? TemplateFormId { get; init; }
    }

    internal sealed record RuntimeRefrExtraDataInspection(
        bool HasExtraData,
        int VisitedNodeCount,
        IReadOnlyDictionary<byte, int> TypeCounts,
        RuntimeRefrExtraData Data);

    #endregion
}
