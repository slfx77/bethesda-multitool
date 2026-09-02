using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Runtime.Readers;

/// <summary>
///     Reads TESObjectCELL runtime structs from Xbox 360 memory dumps.
///     Extracts cell probe snapshots (FormID, flags, water height, worldspace, land,
///     references, lighting, extra data) from PDB-derived struct layouts. Per-build
///     cell-field offset shifts are applied via <see cref="PdbStructView.WithShift(int,int,int)" />
///     using the <c>cellShift</c> registered at construction time.
/// </summary>
internal sealed class RuntimeCellObjectEnumerator
{
    private const int CellShiftStartOffset = 52;

    private readonly int _cellShift;
    private readonly RuntimeMemoryContext _context;
    private readonly RuntimePdbFieldAccessor _fields;

    internal RuntimeCellObjectEnumerator(
        RuntimeMemoryContext context,
        RuntimePdbFieldAccessor fields,
        int cellShift)
    {
        _context = context;
        _fields = fields;
        _cellShift = cellShift;
    }

    private PdbStructView OpenCellView(byte[] buffer, long fileOffset, PdbTypeLayout layout)
    {
        return new PdbStructView(_fields, layout, buffer, fileOffset, null)
            .WithShift(CellShiftStartOffset, int.MaxValue, _cellShift);
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
            layout,
            entry.OriginalFormType);
    }

    /// <summary>
    ///     Read a 192-byte <c>TESObjectCELL</c> that is only known by pointer.
    ///     <para>
    ///         VA-based on purpose. Callers hold a heap pointer, and translating it to a file offset
    ///         to flat-read the struct is wrong in both directions: the struct can straddle two
    ///         regions that are file-adjacent but VA-disjoint (splicing an unrelated allocation into
    ///         the cell's tail), or VA-adjacent but file-disjoint (a flat read misses the tail
    ///         entirely). <see cref="RuntimeMemoryContext.ReadBytesAtVa(long, int)" /> handles both and fails
    ///         closed. The file offset is still derived for the snapshot's provenance field.
    ///     </para>
    /// </summary>
    internal RuntimeCellProbeSnapshot? ReadRuntimeCellProbeSnapshotAtVa(uint cellVa, uint? expectedFormId,
        string? displayName)
    {
        var layout = PdbStructLayouts.Get(0x39);
        if (layout == null)
        {
            return null;
        }

        var buffer = _context.ReadBytesAtVa(Xbox360MemoryUtils.VaToLong(cellVa), layout.StructSize);
        if (buffer == null)
        {
            return null;
        }

        return ReadRuntimeCellProbeSnapshotFromBuffer(
            buffer,
            _context.VaToFileOffset(cellVa) ?? 0,
            expectedFormId,
            displayName,
            layout);
    }

    internal RuntimeCellProbeSnapshot? ReadRuntimeCellProbeSnapshotFromBuffer(
        byte[] buffer,
        long fileOffset,
        uint? expectedFormId,
        string? displayName,
        PdbTypeLayout layout,
        byte? originalFormType = null)
    {
        // Accept the pre-drift byte as well as canonical CELL. Entries are drift-corrected by
        // RuntimeBuildOffsets.ApplyRemap, but the bytes in memory are NOT — so on a build whose
        // FormType enum shifts across 0x39, a hard equality test silently drops EVERY runtime
        // cell. The WRLD twin (RuntimeCellReader.cs:219) already tolerates this; CELL did not.
        // No dump in the current corpus drifts across 0x39, so this is latent, not active.
        if (buffer.Length < 16 || (buffer[4] != 0x39 && buffer[4] != (originalFormType ?? 0x39)))
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

        var view = OpenCellView(buffer, fileOffset, layout);

        var flagsOffset = view.Offset("cCellFlags", "TESObjectCELL");
        var waterHeightOffset = view.Offset("fWaterHeight", "TESObjectCELL");
        var referenceListOffset = view.Offset("listReferences", "TESObjectCELL");

        var flags = flagsOffset.HasValue && flagsOffset.Value < buffer.Length
            ? buffer[flagsOffset.Value]
            : (byte)0;

        // The engine's own water corroboration: TESObjectCELL::bAutoWaterLoaded is set when the
        // cell actually created its auto-water. Read strictly — a bool byte holds 0 or 1, so any
        // other value means the slot is garbage and carries no evidence either way (null).
        var autoWaterOffset = view.Offset("bAutoWaterLoaded", "TESObjectCELL");
        bool? autoWaterLoaded = autoWaterOffset.HasValue && autoWaterOffset.Value < buffer.Length
            ? buffer[autoWaterOffset.Value] switch { 0 => false, 1 => true, _ => null }
            : null;

        // iLightingTemplateInheritanceFlags (uint32)
        var inheritFlagsOffset = view.Offset("iLightingTemplateInheritanceFlags", "TESObjectCELL");
        uint? lightingInheritanceFlags = inheritFlagsOffset.HasValue && inheritFlagsOffset.Value + 4 <= buffer.Length
            ? RuntimePdbFieldAccessor.ReadUInt32(buffer, inheritFlagsOffset.Value)
            : null;

        // Walk the BSExtraData linked list for encounter zone, music, acoustic, image space
        var cellExtras = ReadCellExtraData(view);

        return new RuntimeCellProbeSnapshot(
            formId,
            NormalizeString(displayName) ?? view.BsString("cFullName", "TESFullName"),
            flags,
            ReadReportableHeight(buffer, waterHeightOffset),
            view.FormIdPointer("pWorldSpace", "TESObjectCELL", 0x41),
            // DELIBERATELY ungated, second attempt (2026-09-01): gating this on the per-dump
            // empirical LAND byte (`_context.ResolvedLandFormType`) is correct in principle — the
            // byte drifts across builds, so neither a literal nor the layout DB may stand in —
            // but measured on xex21 the gate's rejections ripple into the WRLD/CELL shift probe's
            // scores (margin collapsed 11 → 0) and shifted downstream cell classification until a
            // real gridless exterior cell reached the planner's hard guard. The follow's blast
            // radius while ungated is only the runtime cells CSV and probe scoring, so the honest
            // trade is to stay ungated until the probe/classification stop consuming this link.
            view.FormIdPointer("pCellLand", "TESObjectCELL"),
            referenceListOffset.HasValue
                ? ReadCellReferenceFormIds(buffer, referenceListOffset.Value)
                : [],
            // Gate byte from the layout DB, not a literal: this gate shipped hardcoded as 0x67
            // (TESObjectIMOD's byte) and therefore NEVER matched — measured 2026-09-01 on xex44,
            // all 40 sampled interior cells' pLightingTemplate targets carry 0x65
            // (BGSLightingTemplate, exactly what the DB says), so LightingTemplateFormId was
            // silently null on every cell since the gate was written.
            view.FormIdPointer("pLightingTemplate", "TESObjectCELL", LightingTemplateFormType),
            lightingInheritanceFlags,
            cellExtras.EncounterZoneFormId,
            cellExtras.MusicTypeFormId,
            cellExtras.AcousticSpaceFormId,
            cellExtras.ImageSpaceFormId,
            autoWaterLoaded);
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
            AutoWaterLoaded = snapshot.AutoWaterLoaded,
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
        ReadCellExtraData(PdbStructView view)
    {
        var cellBuffer = view.Buffer;
        // ExtraDataList is an embedded struct in TESObjectCELL; pHead is at +4 within it.
        var extraDataOffset = view.Offset("ExtraData", "TESObjectCELL");
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

            var nodeBuffer = _context.ReadBytesAtVa(
                Xbox360MemoryUtils.VaToLong(currentVa), CellExtraNodeReadSize);
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
                    // 0x66 per the layout DB — the old hardcoded 0x6B (TESRecipeCategory) never
                    // matched a real BGSMusicType (measured 2026-09-01: all sampled targets carry
                    // 0x66), so this field was silently null everywhere.
                    var musicVa = BinaryUtils.ReadUInt32BE(nodeBuffer, CellExtraPayloadOffset);
                    musicTypeFormId ??= _context.FollowPointerVaToFormId(musicVa, MusicTypeFormType);
                    break;
                }
                case ExtraCellAcousticSpaceCode:
                {
                    var acousticVa = BinaryUtils.ReadUInt32BE(nodeBuffer, CellExtraPayloadOffset);
                    // 0x0E (ASPC), matching the type constraint its three sibling cases already
                    // apply. Without it any TESForm a stale extra-data pointer happens to land on
                    // became the cell's XCAS, which the engine then reports as an acoustic space
                    // it cannot find.
                    acousticSpaceFormId ??= _context.FollowPointerVaToFormId(acousticVa, 0x0E);
                    break;
                }
                case ExtraCellImageSpaceCode:
                {
                    // 0x53 per the layout DB — the old hardcoded 0x56 (BGSPerk) never matched a
                    // real TESImageSpace (measured 2026-09-01: 19/19 sampled targets carry 0x53).
                    var imageVa = BinaryUtils.ReadUInt32BE(nodeBuffer, CellExtraPayloadOffset);
                    imageSpaceFormId ??= _context.FollowPointerVaToFormId(imageVa, ImageSpaceFormType);
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

    internal static float? ReadReportableHeight(byte[] buffer, int? offset)
    {
        if (!offset.HasValue || offset.Value + 4 > buffer.Length)
        {
            return null;
        }

        // Garbage collapses to the NO-WATER sentinel, never to 0. A runtime fWaterHeight that is
        // NaN/Inf/FLT_MAX/out-of-range means the engine never set a level for this cell;
        // NormalizeReportableHeight turned exactly that into 0f — an in-range, real-looking
        // sea-level plane indistinguishable downstream from an authored XCLW of 0, which flooded
        // every dry DMP interior.
        var value = RuntimePdbFieldAccessor.ReadFloat(buffer, offset.Value);
        return WorldHeightNormalizer.PreserveSentinelOrNormalize(value);
    }

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
        uint? ImageSpaceFormId = null,
        bool? AutoWaterLoaded = null);

    #region BSExtraData Linked List (Cell ExtraDataList)

    private const int CellExtraEtypeOffset = 4;
    private const int CellExtraNextOffset = 8;
    private const int CellExtraPayloadOffset = 12;
    private const int CellExtraNodeReadSize = 16;

    // Pointer-gate bytes resolved from the layout DB rather than transcribed by hand — three of
    // the original literals (0x67/0x6B/0x56) were wrong for EVERY corpus dump and their fields
    // never resolved. The DB is the shipped build's PDB, so a future enum-drifted build could
    // still disagree (the LAND 0x42-vs-0x44 lesson); these classes measured drift-free on the
    // Release_Beta corpus (2026-09-01).
    private static readonly byte LightingTemplateFormType = ResolveFormType("BGSLightingTemplate", 0x65);
    private static readonly byte MusicTypeFormType = ResolveFormType("BGSMusicType", 0x66);
    private static readonly byte ImageSpaceFormType = ResolveFormType("TESImageSpace", 0x53);

    private static byte ResolveFormType(string className, byte measuredFallback)
    {
        return PdbStructLayouts.TryGetFormTypeByClassName(className, out var formType)
            ? formType
            : measuredFallback;
    }
    private const int MaxCellExtraListNodes = 64;

    private const byte ExtraCellMusicTypeCode = 0x07;
    private const byte ExtraCellImageSpaceCode = 0x59;
    private const byte ExtraEncounterZoneCode = 0x74;
    private const byte ExtraCellAcousticSpaceCode = 0x81;

    #endregion
}
