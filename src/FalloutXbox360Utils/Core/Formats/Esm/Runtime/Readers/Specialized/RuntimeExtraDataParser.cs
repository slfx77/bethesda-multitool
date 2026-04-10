using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

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
            var nodeBuffer = _context.ReadBytes(nodeFileOffset.Value, ExtraNodeSize);
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
                    var mapMarker = ReadMapMarkerExtra(nodeFileOffset.Value);
                    if (mapMarker.IsMapMarker)
                    {
                        result.IsMapMarker = true;
                        result.MarkerType ??= mapMarker.MarkerType;
                        result.MarkerName ??= mapMarker.MarkerName;
                    }

                    break;
                }
                case ExtraStartingPositionType:
                    result.StartingPosition ??= ReadStartingPosition(nodeFileOffset.Value);
                    break;
                case ExtraOwnershipType:
                    result.OwnerFormId ??= ReadOwnerFormId(nodeFileOffset.Value);
                    break;
                case ExtraPackageStartLocationType:
                    result.PackageStartLocation ??= ReadPackageStartLocation(nodeFileOffset.Value);
                    break;
                case ExtraMerchantContainerType:
                    result.MerchantContainerFormId ??= ReadMerchantContainerFormId(nodeFileOffset.Value);
                    break;
                case ExtraPersistentCellType:
                    result.PersistentCellFormId ??= ReadPersistentCellFormId(nodeFileOffset.Value);
                    break;
                case ExtraEncounterZoneType:
                    result.EncounterZoneFormId ??= ReadEncounterZoneFormId(nodeFileOffset.Value);
                    break;
                case ExtraLockType:
                {
                    var lockData = ReadLockData(nodeFileOffset.Value);
                    result.LockLevel ??= lockData.LockLevel;
                    result.LockKeyFormId ??= lockData.LockKeyFormId;
                    result.LockFlags ??= lockData.LockFlags;
                    result.LockNumTries ??= lockData.LockNumTries;
                    result.LockTimesUnlocked ??= lockData.LockTimesUnlocked;
                    break;
                }
                case ExtraTeleportType:
                    result.DestinationDoorFormId ??= ReadTeleportDestinationDoorFormId(nodeFileOffset.Value);
                    break;
                case ExtraEnableStateParentType:
                {
                    var (enableParentFormId, enableParentFlags) = ReadEnableStateParent(nodeFileOffset.Value);
                    if (!result.EnableParentFormId.HasValue && enableParentFormId.HasValue)
                    {
                        result.EnableParentFormId = enableParentFormId;
                        result.EnableParentFlags = enableParentFlags;
                    }

                    break;
                }
                case ExtraStartingWorldOrCellType:
                    result.StartingWorldOrCellFormId ??= ReadStartingWorldOrCellFormId(nodeFileOffset.Value);
                    break;
                case ExtraLinkedRefType:
                    result.LinkedRefFormId ??= ReadLinkedRefFormId(nodeFileOffset.Value);
                    break;
                case ExtraLinkedRefChildrenType:
                {
                    var childFormIds = ReadLinkedRefChildrenFormIds(nodeFileOffset.Value);
                    if (result.LinkedRefChildrenFormIds == null && childFormIds.Count > 0)
                    {
                        result.LinkedRefChildrenFormIds = childFormIds;
                    }

                    break;
                }
                case ExtraLeveledCreatureType:
                {
                    var leveledCreatureData = ReadLeveledCreatureData(nodeFileOffset.Value);
                    result.LeveledCreatureOriginalBaseFormId ??= leveledCreatureData.OriginalBaseFormId;
                    result.LeveledCreatureTemplateFormId ??= leveledCreatureData.TemplateFormId;
                    break;
                }
                case ExtraRadiusType:
                    result.Radius ??= ReadRadius(nodeFileOffset.Value);
                    break;
                case ExtraCountType:
                    result.Count ??= ReadCount(nodeFileOffset.Value);
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

    private (bool IsMapMarker, ushort? MarkerType, string? MarkerName) ReadMapMarkerExtra(long nodeFileOffset)
    {
        var nodeBuffer = _context.ReadBytes(nodeFileOffset, ExtraPointerNodeSize);
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
            : ReadMapMarkerData(mapDataFileOffset.Value);
    }

    /// <summary>
    ///     Read MapMarkerData struct at the given file offset.
    ///     Layout: TESFullName(0-11) + cFlags(12) + cOriginalFlags(13) + sType(14, uint16) + pReputation(16)
    /// </summary>
    private (bool IsMapMarker, ushort? MarkerType, string? MarkerName) ReadMapMarkerData(long mapDataFileOffset)
    {
        var mapBuffer = _context.ReadBytes(mapDataFileOffset, 20);
        if (mapBuffer == null)
        {
            return (true, null, null);
        }

        var markerType = BinaryUtils.ReadUInt16BE(mapBuffer, MapMarkerTypeOffset);
        var markerName = _context.ReadBSStringT(mapDataFileOffset, MapMarkerNameFieldOffset);

        return (true, markerType, markerName);
    }

    private uint? ReadOwnerFormId(long nodeFileOffset)
    {
        var nodeBuffer = _context.ReadBytes(nodeFileOffset, ExtraPointerNodeSize);
        if (nodeBuffer == null)
        {
            return null;
        }

        var ownerVa = BinaryUtils.ReadUInt32BE(nodeBuffer, ExtraPayloadPtrOffset);
        return _context.FollowPointerVaToFormId(ownerVa);
    }

    private PositionSubrecord? ReadStartingPosition(long nodeFileOffset)
    {
        var nodeBuffer = _context.ReadBytes(nodeFileOffset, ExtraStartingPositionNodeSize);
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

    private RuntimePackageStartLocation? ReadPackageStartLocation(long nodeFileOffset)
    {
        var nodeBuffer = _context.ReadBytes(nodeFileOffset, ExtraPackageStartLocationNodeSize);
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

    private uint? ReadMerchantContainerFormId(long nodeFileOffset)
    {
        var nodeBuffer = _context.ReadBytes(nodeFileOffset, ExtraPointerNodeSize);
        if (nodeBuffer == null)
        {
            return null;
        }

        var containerVa = BinaryUtils.ReadUInt32BE(nodeBuffer, ExtraPayloadPtrOffset);
        return ReadPlacedRefFormId(containerVa);
    }

    private uint? ReadPersistentCellFormId(long nodeFileOffset)
    {
        var nodeBuffer = _context.ReadBytes(nodeFileOffset, ExtraPointerNodeSize);
        if (nodeBuffer == null)
        {
            return null;
        }

        var cellVa = BinaryUtils.ReadUInt32BE(nodeBuffer, ExtraPayloadPtrOffset);
        return _context.FollowPointerVaToFormId(cellVa, 0x39);
    }

    private uint? ReadStartingWorldOrCellFormId(long nodeFileOffset)
    {
        var nodeBuffer = _context.ReadBytes(nodeFileOffset, ExtraStartingWorldOrCellNodeSize);
        if (nodeBuffer == null)
        {
            return null;
        }

        var formVa = BinaryUtils.ReadUInt32BE(nodeBuffer, ExtraPayloadPtrOffset);
        return _context.FollowPointerVaToFormId(formVa);
    }

    private uint? ReadEncounterZoneFormId(long nodeFileOffset)
    {
        var nodeBuffer = _context.ReadBytes(nodeFileOffset, ExtraPointerNodeSize);
        if (nodeBuffer == null)
        {
            return null;
        }

        var zoneVa = BinaryUtils.ReadUInt32BE(nodeBuffer, ExtraPayloadPtrOffset);
        return _context.FollowPointerVaToFormId(zoneVa, 0x61);
    }

    private RuntimeLeveledCreatureData ReadLeveledCreatureData(long nodeFileOffset)
    {
        var nodeBuffer = _context.ReadBytes(nodeFileOffset, ExtraLeveledCreatureNodeSize);
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

    private float? ReadRadius(long nodeFileOffset)
    {
        var nodeBuffer = _context.ReadBytes(nodeFileOffset, ExtraPointerNodeSize);
        if (nodeBuffer == null)
        {
            return null;
        }

        var radius = BinaryUtils.ReadFloatBE(nodeBuffer, ExtraPayloadPtrOffset);
        return RuntimeMemoryContext.IsNormalFloat(radius) && radius > 0f && radius <= 500_000f
            ? radius
            : null;
    }

    private short? ReadCount(long nodeFileOffset)
    {
        var nodeBuffer = _context.ReadBytes(nodeFileOffset, ExtraPointerNodeSize);
        if (nodeBuffer == null)
        {
            return null;
        }

        return BinaryUtils.ReadInt16BE(nodeBuffer, ExtraPayloadPtrOffset);
    }

    private string? ReadEditorId(long nodeFileOffset)
    {
        return _context.ReadBSStringT(nodeFileOffset, ExtraPayloadPtrOffset);
    }

    private uint? ReadTeleportDestinationDoorFormId(long nodeFileOffset)
    {
        var nodeBuffer = _context.ReadBytes(nodeFileOffset, ExtraPointerNodeSize);
        if (nodeBuffer == null)
        {
            return null;
        }

        var teleportDataVa = BinaryUtils.ReadUInt32BE(nodeBuffer, ExtraPayloadPtrOffset);
        if (teleportDataVa == 0 || !_context.IsValidPointer(teleportDataVa))
        {
            return null;
        }

        var teleportDataFileOffset = _context.VaToFileOffset(teleportDataVa);
        if (teleportDataFileOffset == null)
        {
            return null;
        }

        var teleportBuffer = _context.ReadBytes(teleportDataFileOffset.Value, DoorTeleportLinkedDoorPtrSize);
        if (teleportBuffer == null)
        {
            return null;
        }

        var linkedDoorVa = BinaryUtils.ReadUInt32BE(teleportBuffer);
        return ReadPlacedRefFormId(linkedDoorVa);
    }

    private (uint? ParentFormId, byte? Flags) ReadEnableStateParent(long nodeFileOffset)
    {
        var nodeBuffer = _context.ReadBytes(nodeFileOffset, ExtraEnableStateParentNodeSize);
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

    private uint? ReadLinkedRefFormId(long nodeFileOffset)
    {
        var nodeBuffer = _context.ReadBytes(nodeFileOffset, ExtraPointerNodeSize);
        if (nodeBuffer == null)
        {
            return null;
        }

        var linkedRefVa = BinaryUtils.ReadUInt32BE(nodeBuffer, ExtraPayloadPtrOffset);
        return ReadPlacedRefFormId(linkedRefVa);
    }

    private List<uint> ReadLinkedRefChildrenFormIds(long nodeFileOffset)
    {
        var nodeBuffer = _context.ReadBytes(nodeFileOffset, ExtraLinkedRefChildrenNodeSize);
        if (nodeBuffer == null)
        {
            return [];
        }

        return ReadPlacedRefSimpleList(nodeBuffer, ExtraPayloadPtrOffset);
    }

    private RuntimeLockData ReadLockData(long nodeFileOffset)
    {
        var nodeBuffer = _context.ReadBytes(nodeFileOffset, ExtraPointerNodeSize);
        if (nodeBuffer == null)
        {
            return new RuntimeLockData();
        }

        var lockVa = BinaryUtils.ReadUInt32BE(nodeBuffer, ExtraPayloadPtrOffset);
        if (lockVa == 0 || !_context.IsValidPointer(lockVa))
        {
            return new RuntimeLockData();
        }

        var lockFileOffset = _context.VaToFileOffset(lockVa);
        if (lockFileOffset == null)
        {
            return new RuntimeLockData();
        }

        var lockBuffer = _context.ReadBytes(lockFileOffset.Value, RefrLockSize);
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

        var fileOffset = _context.VaToFileOffset(va);
        if (fileOffset == null)
        {
            return null;
        }

        var formBuffer = _context.ReadBytes(fileOffset.Value, 16);
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
            var nodeFileOffset = _context.VaToFileOffset(nextPtr);
            if (nodeFileOffset == null)
            {
                break;
            }

            var nodeBuffer = _context.ReadBytes(nodeFileOffset.Value, 8);
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
    private const int ExtraLinkedRefChildrenNodeSize = 20;
    private const int ExtraStartingPositionNodeSize = 36;
    private const int ExtraPackageStartLocationNodeSize = 32;
    private const int ExtraStartingWorldOrCellNodeSize = 16;
    private const int ExtraLeveledCreatureNodeSize = 20;

    // MapMarkerData (20 bytes)
    private const int MapMarkerNameFieldOffset = 4;
    private const int MapMarkerTypeOffset = 14;

    // DoorTeleportData
    private const int DoorTeleportLinkedDoorPtrSize = 4;

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
        public uint? EnableParentFormId { get; set; }
        public byte? EnableParentFlags { get; set; }
        public uint? PersistentCellFormId { get; set; }
        public uint? LinkedRefFormId { get; set; }
        public List<uint>? LinkedRefChildrenFormIds { get; set; }
        public short? Count { get; set; }
        public string? EditorId { get; set; }
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
