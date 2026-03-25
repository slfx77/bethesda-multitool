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
    private readonly RuntimeExtraDataParser _extraParser = new(context);
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

        // Follow pObjectReference -> base FormID (required)
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

        // Follow pParentCell -> cell FormID + interior flag (expected type 0x39 = CELL)
        var (parentCellFormId, parentCellIsInterior) = FollowPointerToCellInfo(buffer, ParentCellPtrOffset);

        // Walk the ExtraDataList once and extract placement semantics we can map into REFR fields.
        var pHead = BinaryUtils.ReadUInt32BE(buffer, ExtraListHeadOffset);
        var extraData = _extraParser.ReadExtraData(pHead);

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
            return false; // No REFRs to probe -> default to final layout
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
    ///     Inspect extra data for a single REFR entry (validates the REFR first).
    /// </summary>
    private RuntimeExtraDataParser.RuntimeRefrExtraDataInspection? InspectExtraData(RuntimeEditorIdEntry entry)
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
        return _extraParser.ReadExtraDataInspection(pHead);
    }

    /// <summary>
    ///     Read the TESObjectREFR struct (120 bytes final, 116 bytes early) using VA-based region validation.
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
    ///     Follow a pointer to a TESObjectCELL and return both FormID and IsInterior flag.
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

    #endregion
}
