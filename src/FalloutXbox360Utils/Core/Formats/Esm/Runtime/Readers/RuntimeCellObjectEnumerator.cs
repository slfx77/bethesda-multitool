using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Generic;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Reads TESObjectCELL runtime structs from Xbox 360 memory dumps.
///     Extracts cell probe snapshots (FormID, flags, water height, worldspace, land,
///     references, lighting, extra data) from PDB-derived struct layouts.
/// </summary>
internal sealed class RuntimeCellObjectEnumerator
{
    private readonly RuntimeMemoryContext _context;
    private readonly RuntimePdbFieldAccessor _fields;
    private readonly Func<int?, int?> _adjustCellFieldOffset;

    internal RuntimeCellObjectEnumerator(
        RuntimeMemoryContext context,
        RuntimePdbFieldAccessor fields,
        Func<int?, int?> adjustCellFieldOffset)
    {
        _context = context;
        _fields = fields;
        _adjustCellFieldOffset = adjustCellFieldOffset;
    }

    internal RuntimeCellProbeSnapshot? ReadRuntimeCellProbeSnapshot(RuntimeEditorIdEntry entry,
        Func<RuntimeEditorIdEntry, int, byte[]?> readStructBuffer)
    {
        if (entry.FormType != 0x39 || !entry.TesFormOffset.HasValue)
        {
            return null;
        }

        var layout = PdbStructLayouts.Get(0x39);
        if (layout == null)
        {
            return null;
        }

        var buffer = readStructBuffer(entry, layout.StructSize);
        if (buffer == null)
        {
            return null;
        }

        return ReadRuntimeCellProbeSnapshotFromBuffer(
            buffer,
            entry.TesFormOffset.Value,
            entry.FormId,
            entry.DisplayName,
            layout);
    }

    internal RuntimeCellProbeSnapshot? ReadRuntimeCellProbeSnapshot(long fileOffset, uint? expectedFormId,
        string? displayName)
    {
        var layout = PdbStructLayouts.Get(0x39);
        if (layout == null)
        {
            return null;
        }

        var buffer = _context.ReadBytes(fileOffset, layout.StructSize);
        if (buffer == null)
        {
            return null;
        }

        return ReadRuntimeCellProbeSnapshotFromBuffer(
            buffer,
            fileOffset,
            expectedFormId,
            displayName,
            layout);
    }

    internal RuntimeCellProbeSnapshot? ReadRuntimeCellProbeSnapshotFromBuffer(
        byte[] buffer,
        long fileOffset,
        uint? expectedFormId,
        string? displayName,
        PdbTypeLayout layout)
    {
        if (buffer.Length < 16 || buffer[4] != 0x39)
        {
            return null;
        }

        var formId = BinaryUtils.ReadUInt32BE(buffer, 12);
        if (formId == 0 || formId == 0xFFFFFFFF)
        {
            return null;
        }

        if (expectedFormId.HasValue && formId != expectedFormId.Value)
        {
            return null;
        }

        var flagsOffset = _adjustCellFieldOffset(RuntimePdbFieldAccessor.FindFieldOffset(layout, "cCellFlags", "TESObjectCELL"));
        var waterHeightOffset =
            _adjustCellFieldOffset(RuntimePdbFieldAccessor.FindFieldOffset(layout, "fWaterHeight", "TESObjectCELL"));
        var worldspaceOffset =
            _adjustCellFieldOffset(RuntimePdbFieldAccessor.FindFieldOffset(layout, "pWorldSpace", "TESObjectCELL"));
        var landOffset = _adjustCellFieldOffset(RuntimePdbFieldAccessor.FindFieldOffset(layout, "pCellLand", "TESObjectCELL"));
        var referenceListOffset =
            _adjustCellFieldOffset(RuntimePdbFieldAccessor.FindFieldOffset(layout, "listReferences", "TESObjectCELL"));

        var flags = flagsOffset.HasValue && flagsOffset.Value < buffer.Length
            ? buffer[flagsOffset.Value]
            : (byte)0;

        // pLightingTemplate — BGSLightingTemplate pointer (FormType 0x67)
        var lightingTemplateOffset =
            _adjustCellFieldOffset(RuntimePdbFieldAccessor.FindFieldOffset(layout, "pLightingTemplate", "TESObjectCELL"));
        var lightingTemplateFormId = lightingTemplateOffset.HasValue
            ? _fields.ReadPointerToFormId(buffer, lightingTemplateOffset.Value, 0x67)
            : null;

        // iLightingTemplateInheritanceFlags (uint32)
        var inheritFlagsOffset = _adjustCellFieldOffset(
            RuntimePdbFieldAccessor.FindFieldOffset(layout, "iLightingTemplateInheritanceFlags", "TESObjectCELL"));
        uint? lightingInheritanceFlags = inheritFlagsOffset.HasValue && inheritFlagsOffset.Value + 4 <= buffer.Length
            ? RuntimePdbFieldAccessor.ReadUInt32(buffer, inheritFlagsOffset.Value)
            : null;

        // Walk the BSExtraData linked list for encounter zone, music, acoustic, image space
        var cellExtras = ReadCellExtraData(buffer, layout);

        return new RuntimeCellProbeSnapshot(
            formId,
            NormalizeString(displayName)
            ?? _fields.ReadBsString(fileOffset, layout, "cFullName", "TESFullName"),
            flags,
            ReadNormalFloat(buffer, waterHeightOffset),
            worldspaceOffset.HasValue
                ? _fields.ReadPointerToFormId(buffer, worldspaceOffset.Value, 0x41)
                : null,
            landOffset.HasValue
                ? _fields.ReadPointerToFormId(buffer, landOffset.Value)
                : null,
            referenceListOffset.HasValue
                ? ReadCellReferenceFormIds(buffer, referenceListOffset.Value)
                : [],
            lightingTemplateFormId,
            lightingInheritanceFlags,
            cellExtras.EncounterZoneFormId,
            cellExtras.MusicTypeFormId,
            cellExtras.AcousticSpaceFormId,
            cellExtras.ImageSpaceFormId);
    }

    internal static CellRecord? BuildCellRecord(
        RuntimeCellProbeSnapshot? snapshot,
        long fileOffset,
        string? editorId,
        string? displayName)
    {
        if (snapshot == null)
        {
            return null;
        }

        return new CellRecord
        {
            FormId = snapshot.FormId,
            EditorId = NormalizeString(editorId),
            FullName = snapshot.FullName ?? NormalizeString(displayName),
            Flags = snapshot.Flags,
            WaterHeight = snapshot.WaterHeight,
            WorldspaceFormId = snapshot.WorldspaceFormId,
            LightingTemplateFormId = snapshot.LightingTemplateFormId,
            LightingTemplateInheritanceFlags = snapshot.LightingTemplateInheritanceFlags,
            EncounterZoneFormId = snapshot.EncounterZoneFormId,
            MusicTypeFormId = snapshot.MusicTypeFormId,
            AcousticSpaceFormId = snapshot.AcousticSpaceFormId,
            ImageSpaceFormId = snapshot.ImageSpaceFormId,
            Offset = fileOffset,
            IsBigEndian = true
        };
    }

    private List<uint> ReadCellReferenceFormIds(byte[] cellBuffer, int listHeadOffset)
    {
        if (listHeadOffset + 8 > cellBuffer.Length)
        {
            return [];
        }

        var formIds = _fields.ReadFormIdSimpleList(cellBuffer, listHeadOffset);
        if (formIds.Count <= 1)
        {
            return formIds;
        }

        var seen = new HashSet<uint>();
        var deduped = new List<uint>(formIds.Count);
        foreach (var formId in formIds)
        {
            if (formId != 0 && seen.Add(formId))
            {
                deduped.Add(formId);
            }
        }

        return deduped;
    }

    /// <summary>
    ///     Walk the BSExtraData linked list from a CELL's ExtraDataList and extract
    ///     encounter zone, music type, acoustic space, and image space FormIDs.
    /// </summary>
    private (uint? EncounterZoneFormId, uint? MusicTypeFormId, uint? AcousticSpaceFormId, uint? ImageSpaceFormId)
        ReadCellExtraData(byte[] cellBuffer, PdbTypeLayout layout)
    {
        // ExtraDataList is an embedded struct in TESObjectCELL; pHead is at +4 within it.
        var extraDataOffset = _adjustCellFieldOffset(
            RuntimePdbFieldAccessor.FindFieldOffset(layout, "ExtraData", "TESObjectCELL"));
        if (!extraDataOffset.HasValue || extraDataOffset.Value + 8 > cellBuffer.Length)
        {
            return (null, null, null, null);
        }

        // pHead is at ExtraDataList+4 (first 4 bytes are vfptr)
        var pHead = BinaryUtils.ReadUInt32BE(cellBuffer, extraDataOffset.Value + 4);
        if (pHead == 0 || !_context.IsValidPointer(pHead))
        {
            return (null, null, null, null);
        }

        uint? encounterZoneFormId = null;
        uint? musicTypeFormId = null;
        uint? acousticSpaceFormId = null;
        uint? imageSpaceFormId = null;

        var visited = new HashSet<uint>();
        var currentVa = pHead;

        for (var i = 0; i < MaxCellExtraListNodes; i++)
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

            var nodeBuffer = _context.ReadBytes(nodeFileOffset.Value, CellExtraNodeReadSize);
            if (nodeBuffer == null)
            {
                break;
            }

            var eType = nodeBuffer[CellExtraEtypeOffset];
            var nextVa = BinaryUtils.ReadUInt32BE(nodeBuffer, CellExtraNextOffset);

            switch (eType)
            {
                case ExtraEncounterZoneCode:
                {
                    var zoneVa = BinaryUtils.ReadUInt32BE(nodeBuffer, CellExtraPayloadOffset);
                    encounterZoneFormId ??= _context.FollowPointerVaToFormId(zoneVa, 0x61);
                    break;
                }
                case ExtraCellMusicTypeCode:
                {
                    var musicVa = BinaryUtils.ReadUInt32BE(nodeBuffer, CellExtraPayloadOffset);
                    musicTypeFormId ??= _context.FollowPointerVaToFormId(musicVa, 0x6B);
                    break;
                }
                case ExtraCellAcousticSpaceCode:
                {
                    var acousticVa = BinaryUtils.ReadUInt32BE(nodeBuffer, CellExtraPayloadOffset);
                    acousticSpaceFormId ??= _context.FollowPointerVaToFormId(acousticVa);
                    break;
                }
                case ExtraCellImageSpaceCode:
                {
                    var imageVa = BinaryUtils.ReadUInt32BE(nodeBuffer, CellExtraPayloadOffset);
                    imageSpaceFormId ??= _context.FollowPointerVaToFormId(imageVa, 0x56);
                    break;
                }
            }

            // Early exit if all four found
            if (encounterZoneFormId.HasValue && musicTypeFormId.HasValue &&
                acousticSpaceFormId.HasValue && imageSpaceFormId.HasValue)
            {
                break;
            }

            currentVa = nextVa;
        }

        return (encounterZoneFormId, musicTypeFormId, acousticSpaceFormId, imageSpaceFormId);
    }

    internal static string? NormalizeString(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    internal static float? ReadNormalFloat(byte[] buffer, int? offset)
    {
        if (!offset.HasValue || offset.Value + 4 > buffer.Length)
        {
            return null;
        }

        var value = RuntimePdbFieldAccessor.ReadFloat(buffer, offset.Value);
        return RuntimeMemoryContext.IsNormalFloat(value) ? value : null;
    }

    #region BSExtraData Linked List (Cell ExtraDataList)

    private const int CellExtraEtypeOffset = 4;
    private const int CellExtraNextOffset = 8;
    private const int CellExtraPayloadOffset = 12;
    private const int CellExtraNodeReadSize = 16;
    private const int MaxCellExtraListNodes = 64;

    private const byte ExtraCellMusicTypeCode = 0x07;
    private const byte ExtraCellImageSpaceCode = 0x59;
    private const byte ExtraEncounterZoneCode = 0x74;
    private const byte ExtraCellAcousticSpaceCode = 0x81;

    #endregion

    internal sealed record RuntimeCellProbeSnapshot(
        uint FormId,
        string? FullName,
        byte Flags,
        float? WaterHeight,
        uint? WorldspaceFormId,
        uint? LandFormId,
        IReadOnlyList<uint> ReferenceFormIds,
        uint? LightingTemplateFormId = null,
        uint? LightingTemplateInheritanceFlags = null,
        uint? EncounterZoneFormId = null,
        uint? MusicTypeFormId = null,
        uint? AcousticSpaceFormId = null,
        uint? ImageSpaceFormId = null);
}
