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

    // ExtraMapMarker: BSExtraData(0-11) + pMapData(12, 4B ptr)
    private const byte ExtraMapMarkerType = 0x2C; // 44 decimal
    private const int MapDataPtrOffset = 12;

    // MapMarkerData (20 bytes): TESFullName(0-11) + cFlags(12) + cOriginalFlags(13) + sType(14, uint16) + pReputation(16)
    // TESFullName: vfptr(0) + cFullName(4, BSFixedString 8B)
    private const int MapMarkerNameFieldOffset = 4; // BSFixedString at TESFullName+4
    private const int MapMarkerTypeOffset = 14;

    private const int MaxExtraListNodes = 100;

    #endregion

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

        // Follow pParentCell → cell FormID (expected type 0x39 = CELL)
        var parentCellFormId = _context.FollowPointerToFormId(buffer, ParentCellPtrOffset, 0x39);

        // Walk ExtraDataList for map marker
        var pHead = BinaryUtils.ReadUInt32BE(buffer, ExtraListHeadOffset);
        var (isMapMarker, markerType, markerName) = ReadExtraDataForMapMarker(pHead);

        // Determine record type from FormType
        var recordType = RuntimeBuildOffsets.GetRecordTypeCode(formType) ?? "REFR";

        var position = new PositionSubrecord(locX, locY, locZ, rotX, rotY, rotZ, offset, IsBigEndian: true);

        return new ExtractedRefrRecord
        {
            Header = new DetectedMainRecord(recordType, 0, flags, formId, offset, IsBigEndian: true),
            BaseFormId = baseFormId.Value,
            Position = position,
            Scale = scale,
            ParentCellFormId = parentCellFormId,
            IsMapMarker = isMapMarker,
            MarkerType = markerType,
            MarkerName = markerName
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

    /// <summary>
    ///     Walk the BSExtraData linked list looking for an ExtraMapMarker node.
    ///     Returns (isMapMarker, markerType, markerName).
    /// </summary>
    private (bool IsMapMarker, ushort? MarkerType, string? MarkerName) ReadExtraDataForMapMarker(uint pHead)
    {
        if (pHead == 0 || !_context.IsValidPointer(pHead))
        {
            return (false, null, null);
        }

        var visited = new HashSet<uint>();
        var currentVa = pHead;

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

            // Read BSExtraData node: vfptr(4) + cEtype(1) + pad(3) + pNext(4) = 12 bytes minimum
            // For ExtraMapMarker we need +4 more for pMapData pointer
            var nodeBuffer = _context.ReadBytes(nodeFileOffset.Value, ExtraNodeSize + 4);
            if (nodeBuffer == null)
            {
                break;
            }

            var eType = nodeBuffer[ExtraEtypeOffset];
            var nextVa = BinaryUtils.ReadUInt32BE(nodeBuffer, ExtraNextOffset);

            if (eType == ExtraMapMarkerType)
            {
                // Found ExtraMapMarker — read pMapData pointer at +12
                var pMapData = BinaryUtils.ReadUInt32BE(nodeBuffer, MapDataPtrOffset);
                if (pMapData != 0 && _context.IsValidPointer(pMapData))
                {
                    var mapDataFileOffset = _context.VaToFileOffset(pMapData);
                    if (mapDataFileOffset != null)
                    {
                        return ReadMapMarkerData(mapDataFileOffset.Value);
                    }
                }

                // ExtraMapMarker found but pMapData invalid — still a map marker
                return (true, null, null);
            }

            currentVa = nextVa;
        }

        return (false, null, null);
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
}
