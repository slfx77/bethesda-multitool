using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Reader for TESObjectREFR runtime structs (REFR/ACHR/ACRE) from Xbox 360 memory dumps.
///     Extracts placed reference data including position, base object, parent cell, and map markers.
///     Supports both final layout (REFR=120) and early-era layout (REFR=116).
///     Early builds (before March 30, 2010) have TESChildCell = 4B (vtable only, no data field),
///     vs 8B in final builds. This shifts OBJ_REFR and BSExtraList fields by -4.
///     TESForm is 40 bytes in both eras; only TESChildCell size differs.
/// </summary>
internal sealed class RuntimeRefrReader(RuntimeMemoryContext context, bool useProtoOffsets = false)
{
    private readonly RuntimeMemoryContext _context = context;
    private readonly int _shift = RuntimeBuildOffsets.GetRefrFieldShift(useProtoOffsets);

    /// <summary>
    ///     Read a single TESObjectREFR from a runtime memory entry.
    ///     Returns null if the struct is invalid, deleted, or has no base object.
    /// </summary>
    public ExtractedRefrRecord? ReadRuntimeRefr(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        var buffer = ReadRefrStructBuffer(entry, offset);
        if (buffer == null)
        {
            return null;
        }

        // Validate FormType — must be REFR (0x3A), ACHR (0x3B), or ACRE (0x3C)
        var formType = buffer[4];
        if (formType < 0x3A || formType > 0x3C)
        {
            return null;
        }

        // Validate FormID at +12
        var formId = BinaryUtils.ReadUInt32BE(buffer, FormIdOffset);
        if (formId != entry.FormId || formId == 0)
        {
            return null;
        }

        // Skip deleted refs
        var flags = BinaryUtils.ReadUInt32BE(buffer, FormFlagsOffset);
        if ((flags & DeletedFlag) != 0)
        {
            return null;
        }

        // Follow pObjectReference → base FormID (required)
        var baseFormId = _context.FollowPointerToFormId(buffer, BaseObjectPtrOffset);
        if (baseFormId == null)
        {
            return null;
        }

        // Read position (Location: X, Y, Z)
        var locX = BinaryUtils.ReadFloatBE(buffer, LocationXOffset);
        var locY = BinaryUtils.ReadFloatBE(buffer, LocationYOffset);
        var locZ = BinaryUtils.ReadFloatBE(buffer, LocationZOffset);

        // Validate position — reject NaN/Inf and extreme values
        if (!RuntimeMemoryContext.IsNormalFloat(locX) || !RuntimeMemoryContext.IsNormalFloat(locY) ||
            !RuntimeMemoryContext.IsNormalFloat(locZ))
        {
            return null;
        }

        if (Math.Abs(locX) > 500_000 || Math.Abs(locY) > 500_000 || Math.Abs(locZ) > 500_000)
        {
            return null;
        }

        // Read rotation (Angle: X, Y, Z in radians)
        var rotX = RuntimeMemoryContext.ReadValidatedFloat(buffer, AngleXOffset, -10f, 10f);
        var rotY = RuntimeMemoryContext.ReadValidatedFloat(buffer, AngleYOffset, -10f, 10f);
        var rotZ = RuntimeMemoryContext.ReadValidatedFloat(buffer, AngleZOffset, -10f, 10f);

        // Read scale (fRefScale — 1.0 = normal)
        var scale = BinaryUtils.ReadFloatBE(buffer, RefScaleOffset);
        if (!RuntimeMemoryContext.IsNormalFloat(scale) || scale <= 0 || scale > 100)
        {
            scale = 1.0f;
        }

        // Follow pParentCell → cell FormID + interior flag (expected type 0x39 = CELL)
        var (parentCellFormId, parentCellIsInterior) = FollowPointerToCellInfo(buffer, ParentCellPtrOffset);

        // Walk the ExtraDataList once and extract placement semantics we can map into REFR fields.
        var pHead = BinaryUtils.ReadUInt32BE(buffer, ExtraListHeadOffset);
        var extraData = ReadExtraData(pHead);

        // Determine record type from FormType
        var recordType = RuntimeBuildOffsets.GetRecordTypeCode(formType) ?? "REFR";

        var position = new PositionSubrecord(locX, locY, locZ, rotX, rotY, rotZ, offset, true);

        return new ExtractedRefrRecord
        {
            Header = new DetectedMainRecord(recordType, 0, flags, formId, offset, true),
            BaseFormId = baseFormId.Value,
            Position = position,
            Scale = scale,
            ParentCellFormId = parentCellFormId,
            ParentCellIsInterior = parentCellIsInterior,
            PersistentCellFormId = extraData.PersistentCellFormId,
            StartingPosition = extraData.StartingPosition,
            StartingWorldOrCellFormId = extraData.StartingWorldOrCellFormId,
            PackageStartLocation = extraData.PackageStartLocation,
            MerchantContainerFormId = extraData.MerchantContainerFormId,
            LeveledCreatureOriginalBaseFormId = extraData.LeveledCreatureOriginalBaseFormId,
            LeveledCreatureTemplateFormId = extraData.LeveledCreatureTemplateFormId,
            Radius = extraData.Radius,
            Count = extraData.Count,
            OwnerFormId = extraData.OwnerFormId,
            EncounterZoneFormId = extraData.EncounterZoneFormId,
            LockLevel = extraData.LockLevel,
            LockKeyFormId = extraData.LockKeyFormId,
            LockFlags = extraData.LockFlags,
            LockNumTries = extraData.LockNumTries,
            LockTimesUnlocked = extraData.LockTimesUnlocked,
            DestinationDoorFormId = extraData.DestinationDoorFormId,
            EnableParentFormId = extraData.EnableParentFormId,
            EnableParentFlags = extraData.EnableParentFlags,
            IsMapMarker = extraData.IsMapMarker,
            MarkerType = extraData.MarkerType,
            MarkerName = extraData.MarkerName,
            LinkedRefFormId = extraData.LinkedRefFormId,
            LinkedRefChildrenFormIds = extraData.LinkedRefChildrenFormIds ?? [],
            EditorId = extraData.EditorId
        };
    }

    /// <summary>
    ///     Read the TESObjectREFR struct (120 bytes final, 116 bytes early) using VA-based region validation.
    ///     Prevents reading garbage data when a struct spans a gap between non-contiguous memory regions.
    /// </summary>
    private byte[]? ReadRefrStructBuffer(RuntimeEditorIdEntry entry, long fileOffset)
    {
        if (entry.TesFormPointer.HasValue)
        {
            return _context.ReadBytesAtVa(entry.TesFormPointer.Value, RefrStructSize);
        }

        if (fileOffset + RefrStructSize > _context.FileSize)
        {
            return null;
        }

        return _context.ReadBytes(fileOffset, RefrStructSize);
    }

    /// <summary>
    ///     Probes a sample of REFR entries to determine whether the DMP uses early-era
    ///     struct offsets (TESChildCell=4B, REFR=116) or final (TESChildCell=8B, REFR=120).
    ///     Tries reading with both layouts; the one producing more valid REFRs wins.
    ///     Returns true if early-era offsets match better, false for final.
    /// </summary>
    public static bool ProbeIsEarlyBuild(
        RuntimeMemoryContext context,
        IReadOnlyList<RuntimeEditorIdEntry> refrEntries)
    {
        // Sample up to 20 REFR/ACHR/ACRE entries (FormType 0x3A-0x3C), excluding Player (0x14)
        var samples = refrEntries
            .Where(e => e.FormType is >= 0x3A and <= 0x3C && e.FormId != 0x14)
            .Take(20)
            .ToList();

        if (samples.Count == 0)
        {
            return false; // No REFRs to probe → default to final layout
        }

        var earlyReader = new RuntimeRefrReader(context, true);
        var finalReader = new RuntimeRefrReader(context);
        var candidates = new List<RuntimeLayoutProbeCandidate<bool>>
        {
            new("Final", false),
            new("Early", true)
        };

        var result = RuntimeLayoutProbeEngine.Probe(
            samples,
            candidates,
            (entry, candidate) =>
            {
                var reader = candidate.Layout ? earlyReader : finalReader;
                return new RuntimeLayoutProbeScore(reader.ReadRuntimeRefr(entry) != null ? 1 : 0, 1);
            },
            "REFR Probe");

        return result.Winner.Layout;
    }

    /// <summary>
    ///     Read all runtime REFRs from the given entries.
    ///     Returns a dictionary mapping FormID to ExtractedRefrRecord.
    /// </summary>
    public Dictionary<uint, ExtractedRefrRecord> ReadAllRuntimeRefrs(IEnumerable<RuntimeEditorIdEntry> entries)
    {
        var result = new Dictionary<uint, ExtractedRefrRecord>();

        foreach (var entry in entries)
        {
            var refr = ReadRuntimeRefr(entry);
            if (refr != null)
            {
                result[refr.Header.FormId] = refr;
            }
        }

        return result;
    }

    internal RuntimeRefrExtraDataCensus BuildExtraDataCensus(IEnumerable<RuntimeEditorIdEntry> entries,
        int maxEntries = 256)
    {
        var sampleCount = 0;
        var validRefrCount = 0;
        var refsWithExtraData = 0;
        var visitedNodeCount = 0;
        var ownershipCount = 0;
        var lockCount = 0;
        var teleportCount = 0;
        var mapMarkerCount = 0;
        var enableParentCount = 0;
        var linkedRefCount = 0;
        var encounterZoneCount = 0;
        var startingPositionCount = 0;
        var startingWorldOrCellCount = 0;
        var packageStartLocationCount = 0;
        var merchantContainerCount = 0;
        var leveledCreatureCount = 0;
        var radiusCount = 0;
        var countCount = 0;
        var editorIdCount = 0;
        var typeCounts = new Dictionary<byte, int>();

        foreach (var entry in entries)
        {
            if (sampleCount >= maxEntries)
            {
                break;
            }

            if (entry.FormType is < 0x3A or > 0x3C || entry.TesFormOffset == null)
            {
                continue;
            }

            sampleCount++;
            var inspection = InspectExtraData(entry);
            if (inspection == null)
            {
                continue;
            }

            validRefrCount++;
            if (inspection.HasExtraData)
            {
                refsWithExtraData++;
            }

            visitedNodeCount += inspection.VisitedNodeCount;
            foreach (var (type, count) in inspection.TypeCounts)
            {
                typeCounts[type] = typeCounts.GetValueOrDefault(type) + count;
            }

            if (inspection.Data.OwnerFormId.HasValue)
            {
                ownershipCount++;
            }

            if (inspection.Data.LockLevel.HasValue ||
                inspection.Data.LockKeyFormId.HasValue ||
                inspection.Data.LockFlags.HasValue)
            {
                lockCount++;
            }

            if (inspection.Data.DestinationDoorFormId.HasValue)
            {
                teleportCount++;
            }

            if (inspection.Data.IsMapMarker)
            {
                mapMarkerCount++;
            }

            if (inspection.Data.EnableParentFormId.HasValue)
            {
                enableParentCount++;
            }

            if (inspection.Data.LinkedRefFormId.HasValue)
            {
                linkedRefCount++;
            }

            if (inspection.Data.EncounterZoneFormId.HasValue)
            {
                encounterZoneCount++;
            }

            if (inspection.Data.StartingPosition != null)
            {
                startingPositionCount++;
            }

            if (inspection.Data.StartingWorldOrCellFormId.HasValue)
            {
                startingWorldOrCellCount++;
            }

            if (inspection.Data.PackageStartLocation != null)
            {
                packageStartLocationCount++;
            }

            if (inspection.Data.MerchantContainerFormId.HasValue)
            {
                merchantContainerCount++;
            }

            if (inspection.Data.LeveledCreatureOriginalBaseFormId.HasValue ||
                inspection.Data.LeveledCreatureTemplateFormId.HasValue)
            {
                leveledCreatureCount++;
            }

            if (inspection.Data.Radius is > 0)
            {
                radiusCount++;
            }

            if (inspection.Data.Count.HasValue)
            {
                countCount++;
            }

            if (inspection.Data.EditorId != null)
            {
                editorIdCount++;
            }
        }

        return new RuntimeRefrExtraDataCensus(
            sampleCount,
            validRefrCount,
            refsWithExtraData,
            visitedNodeCount,
            typeCounts,
            ownershipCount,
            lockCount,
            teleportCount,
            mapMarkerCount,
            enableParentCount,
            linkedRefCount,
            encounterZoneCount,
            startingPositionCount,
            startingWorldOrCellCount,
            packageStartLocationCount,
            merchantContainerCount,
            leveledCreatureCount,
            radiusCount,
            countCount,
            editorIdCount);
    }

    /// <summary>
    ///     Walk the BSExtraData linked list once and extract the subset of placement semantics
    ///     that map cleanly onto REFR/XESP/XTEL/XLKR/XOWN-style fields.
    /// </summary>
    private RuntimeRefrExtraData ReadExtraData(uint pHead)
    {
        return ReadExtraDataInspection(pHead).Data;
    }

    private RuntimeRefrExtraDataInspection? InspectExtraData(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null)
        {
            return null;
        }

        var buffer = ReadRefrStructBuffer(entry, entry.TesFormOffset.Value);
        if (buffer == null)
        {
            return null;
        }

        var formType = buffer[4];
        if (formType < 0x3A || formType > 0x3C)
        {
            return null;
        }

        var formId = BinaryUtils.ReadUInt32BE(buffer, FormIdOffset);
        if (formId != entry.FormId || formId == 0)
        {
            return null;
        }

        var flags = BinaryUtils.ReadUInt32BE(buffer, FormFlagsOffset);
        if ((flags & DeletedFlag) != 0)
        {
            return null;
        }

        var baseFormId = _context.FollowPointerToFormId(buffer, BaseObjectPtrOffset);
        if (baseFormId == null)
        {
            return null;
        }

        var pHead = BinaryUtils.ReadUInt32BE(buffer, ExtraListHeadOffset);
        return ReadExtraDataInspection(pHead);
    }

    private RuntimeRefrExtraDataInspection ReadExtraDataInspection(uint pHead)
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
            x.Value,
            y.Value,
            z.Value,
            rotX.Value,
            rotY.Value,
            rotZ.Value,
            nodeFileOffset + ExtraPayloadPtrOffset,
            true);
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
        // ExtraCount: BSExtraData(12) + iCount(int16 at +12) = 16 bytes total.
        var nodeBuffer = _context.ReadBytes(nodeFileOffset, ExtraPointerNodeSize);
        if (nodeBuffer == null)
        {
            return null;
        }

        return BinaryUtils.ReadInt16BE(nodeBuffer, ExtraPayloadPtrOffset);
    }

    private string? ReadEditorId(long nodeFileOffset)
    {
        // ExtraEditorID: BSExtraData(12) + BSStringT<char>(8) at +12 = 20 bytes total.
        // BSStringT layout: char* pString(4) + uint16 sLen(2) + uint16 maxLen(2).
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

    private RuntimeRefrLockData ReadLockData(long nodeFileOffset)
    {
        var nodeBuffer = _context.ReadBytes(nodeFileOffset, ExtraPointerNodeSize);
        if (nodeBuffer == null)
        {
            return new RuntimeRefrLockData();
        }

        var lockVa = BinaryUtils.ReadUInt32BE(nodeBuffer, ExtraPayloadPtrOffset);
        if (lockVa == 0 || !_context.IsValidPointer(lockVa))
        {
            return new RuntimeRefrLockData();
        }

        var lockFileOffset = _context.VaToFileOffset(lockVa);
        if (lockFileOffset == null)
        {
            return new RuntimeRefrLockData();
        }

        var lockBuffer = _context.ReadBytes(lockFileOffset.Value, RefrLockSize);
        if (lockBuffer == null)
        {
            return new RuntimeRefrLockData();
        }

        return new RuntimeRefrLockData
        {
            LockLevel = lockBuffer[RefrLockLevelOffset],
            LockKeyFormId = _context.FollowPointerVaToFormId(
                BinaryUtils.ReadUInt32BE(lockBuffer, RefrLockKeyOffset)),
            LockFlags = lockBuffer[RefrLockFlagsOffset],
            LockNumTries = BinaryUtils.ReadUInt32BE(lockBuffer, RefrLockNumTriesOffset),
            LockTimesUnlocked = BinaryUtils.ReadUInt32BE(lockBuffer, RefrLockTimesUnlockedOffset)
        };
    }

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

    private static float? ReadValidatedWorldCoord(byte[] buffer, int offset)
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

    private static float? ReadValidatedRotation(byte[] buffer, int offset)
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

    /// <summary>
    ///     Read MapMarkerData struct at the given file offset.
    ///     Layout: TESFullName(0-11) + cFlags(12) + cOriginalFlags(13) + sType(14, uint16) + pReputation(16)
    /// </summary>
    private (bool IsMapMarker, ushort? MarkerType, string? MarkerName) ReadMapMarkerData(long mapDataFileOffset)
    {
        // Read enough for the MapMarkerData struct (20 bytes)
        var mapBuffer = _context.ReadBytes(mapDataFileOffset, 20);
        if (mapBuffer == null)
        {
            return (true, null, null);
        }

        var markerType = BinaryUtils.ReadUInt16BE(mapBuffer, MapMarkerTypeOffset);

        // Read marker name via BSFixedString at TESFullName+4 (same layout as BSStringT)
        var markerName = _context.ReadBSStringT(mapDataFileOffset, MapMarkerNameFieldOffset);

        return (true, markerType, markerName);
    }

    /// <summary>
    ///     Follow a pointer to a TESObjectCELL and return both FormID and IsInterior flag.
    ///     Reads 53 bytes of the cell struct: TESForm header (24B) + enough for cCellFlags at offset 52.
    /// </summary>
    private (uint? FormId, bool? IsInterior) FollowPointerToCellInfo(byte[] buffer, int pointerOffset)
    {
        if (pointerOffset + 4 > buffer.Length)
        {
            return (null, null);
        }

        var pointer = BinaryUtils.ReadUInt32BE(buffer, pointerOffset);
        if (pointer == 0 || !_context.IsValidPointer(pointer))
        {
            return (null, null);
        }

        // Read enough of the cell struct to get cCellFlags at offset 52
        // CELL does NOT inherit TESChildCell, so no shift applies
        const int cellReadSize = 53; // Need byte at offset 52
        var cellBuffer = _context.ReadBytesAtVa(Xbox360MemoryUtils.VaToLong(pointer), cellReadSize);
        if (cellBuffer == null)
        {
            return (null, null);
        }

        // Validate FormType (0x39 = CELL)
        var formType = cellBuffer[4];
        if (formType != 0x39)
        {
            return (null, null);
        }

        var formId = BinaryUtils.ReadUInt32BE(cellBuffer, 12);
        if (formId == 0 || formId == 0xFFFFFFFF)
        {
            return (null, null);
        }

        // cCellFlags at offset 52; bit 0 = IsInterior
        var cellFlags = cellBuffer[52];
        var isInterior = (cellFlags & 0x01) != 0;

        return (formId, isInterior);
    }

    #region TESObjectREFR Struct Layout

    // Final: TESObjectREFR = 120 bytes. TESForm(40) + TESChildCell(8) + OBJ_REFR(28) + more.
    // Early: TESObjectREFR = 116 bytes. TESForm(40) + TESChildCell(4) + OBJ_REFR(28) + more.
    // Shift is -4 for all fields after offset 40 (TESChildCell data field absent in early builds).
    private const int FinalRefrStructSize = 120;
    private const int EarlyRefrStructSize = 116;
    private const int FormFlagsOffset = 8;
    private const int FormIdOffset = 12;

    // OBJ_REFR data: Final at +48, Early at +44 (delta = _shift = -4)
    private const int FinalBaseObjectPtrOffset = 48;
    private const int FinalAngleXOffset = 52;
    private const int FinalAngleYOffset = 56;
    private const int FinalAngleZOffset = 60;
    private const int FinalLocationXOffset = 64;
    private const int FinalLocationYOffset = 68;
    private const int FinalLocationZOffset = 72;
    private const int FinalRefScaleOffset = 76;
    private const int FinalParentCellPtrOffset = 80;

    // BaseExtraList m_Extra: Final pHead at +88, Early at +84
    private const int FinalExtraListHeadOffset = 88;

    // Computed offsets (apply early-era shift)
    private int RefrStructSize => useProtoOffsets ? EarlyRefrStructSize : FinalRefrStructSize;
    private int BaseObjectPtrOffset => FinalBaseObjectPtrOffset + _shift;
    private int AngleXOffset => FinalAngleXOffset + _shift;
    private int AngleYOffset => FinalAngleYOffset + _shift;
    private int AngleZOffset => FinalAngleZOffset + _shift;
    private int LocationXOffset => FinalLocationXOffset + _shift;
    private int LocationYOffset => FinalLocationYOffset + _shift;
    private int LocationZOffset => FinalLocationZOffset + _shift;
    private int RefScaleOffset => FinalRefScaleOffset + _shift;
    private int ParentCellPtrOffset => FinalParentCellPtrOffset + _shift;
    private int ExtraListHeadOffset => FinalExtraListHeadOffset + _shift;

    // TESForm flags
    private const uint DeletedFlag = 0x20;

    // BSExtraData node layout (12 bytes): vfptr(0) + cEtype(4) + pad(5-7) + pNext(8)
    private const int ExtraNodeSize = 12;
    private const int ExtraEtypeOffset = 4;
    private const int ExtraNextOffset = 8;

    // Common extra-data node shapes.
    private const int ExtraPointerNodeSize = 16;
    private const int ExtraEnableStateParentNodeSize = 20;
    private const int ExtraPayloadPtrOffset = 12;
    private const int ExtraEnableParentFlagsOffset = 16;

    // Extra data type IDs from EXTRA_DATA_TYPE in the final debug PDB.
    private const byte ExtraPersistentCellType = 0x0C; // 12 decimal
    private const byte ExtraStartingPositionType = 0x0F; // 15 decimal
    private const byte ExtraOwnershipType = 0x21; // 33 decimal
    private const byte ExtraPackageStartLocationType = 0x18; // 24 decimal
    private const byte ExtraMerchantContainerType = 0x3C; // 60 decimal
    private const byte ExtraLockType = 0x2A; // 42 decimal
    private const byte ExtraTeleportType = 0x2B; // 43 decimal
    private const byte ExtraMapMarkerType = 0x2C; // 44 decimal
    private const byte ExtraLeveledCreatureType = 0x2E; // 46 decimal
    private const byte ExtraEnableStateParentType = 0x37; // 55 decimal
    private const byte ExtraRadiusType = 0x5C; // 92 decimal
    private const byte ExtraStartingWorldOrCellType = 0x49; // 73 decimal
    private const byte ExtraEncounterZoneType = 0x74; // 116 decimal
    private const byte ExtraCountType = 0x15; // 21 decimal
    private const byte ExtraLinkedRefType = 0x51; // 81 decimal
    private const byte ExtraLinkedRefChildrenType = 0x52; // 82 decimal
    private const byte ExtraEditorIDType = 0x62; // 98 decimal
    private const int ExtraLinkedRefChildrenNodeSize = 20;
    private const int ExtraStartingPositionNodeSize = 36;
    private const int ExtraPackageStartLocationNodeSize = 32;
    private const int ExtraStartingWorldOrCellNodeSize = 16;
    private const int ExtraLeveledCreatureNodeSize = 20;

    // MapMarkerData (20 bytes): TESFullName(0-11) + cFlags(12) + cOriginalFlags(13) + sType(14, uint16) + pReputation(16)
    // TESFullName: vfptr(0) + cFullName(4, BSFixedString 8B)
    private const int MapMarkerNameFieldOffset = 4; // BSFixedString at TESFullName+4
    private const int MapMarkerTypeOffset = 14;

    // DoorTeleportData: pLinkedDoor(0) + position/rotation/flags...
    private const int DoorTeleportLinkedDoorPtrSize = 4;

    // REFR_LOCK: cBaseLevel(0) + pKey(4) + cFlags(8) + uiNumTries(12) + uiTimesUnlocked(16)
    private const int RefrLockSize = 20;
    private const int RefrLockLevelOffset = 0;
    private const int RefrLockKeyOffset = 4;
    private const int RefrLockFlagsOffset = 8;
    private const int RefrLockNumTriesOffset = 12;
    private const int RefrLockTimesUnlockedOffset = 16;

    private const int MaxExtraListNodes = 100;

    private struct RuntimeRefrExtraData
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

    private readonly record struct RuntimeRefrLockData
    {
        public byte? LockLevel { get; init; }

        public uint? LockKeyFormId { get; init; }

        public byte? LockFlags { get; init; }

        public uint? LockNumTries { get; init; }

        public uint? LockTimesUnlocked { get; init; }
    }

    private readonly record struct RuntimeLeveledCreatureData
    {
        public uint? OriginalBaseFormId { get; init; }

        public uint? TemplateFormId { get; init; }
    }

    private sealed record RuntimeRefrExtraDataInspection(
        bool HasExtraData,
        int VisitedNodeCount,
        IReadOnlyDictionary<byte, int> TypeCounts,
        RuntimeRefrExtraData Data);

    #endregion
}
