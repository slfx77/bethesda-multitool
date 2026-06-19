using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;
using BethesdaMultitool.Core.Formats.Esm.Reporting;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;

/// <summary>
///     Preserves room, portal, and occlusion marker references that are required for
///     interior cell structure but are often absent from runtime DMP captures.
/// </summary>
internal static class CellStructuralReferencePreserver
{
    private static readonly HashSet<string> StructuralCellRefSubrecords = new(StringComparer.Ordinal)
    {
        "XPOD", // room/portal connection
        "XOCP", // occlusion plane geometry
        "XORD", // linked occlusion planes
        "XMBO", // room/bound marker extents
        "XPRM", // primitive marker geometry
        "XNDP" // navigation door portal
    };

    private static readonly string[] StructuralBaseEditorIdNeedles =
    [
        "RoomMarker",
        "PortalMarker",
        "Occlusion",
        "MultiBound",
        "Culling"
    ];

    /// <summary>
    ///     Engine default-object base FormIDs for the render-culling markers: room, portal,
    ///     occlusion, and multibound. The COLLISION marker (base 0x21) is deliberately excluded —
    ///     it drives physics/navigation, not rendering, so it is kept even when the render-culling
    ///     graph is dropped. See <see cref="IsRenderCullingMarker" />.
    /// </summary>
    private static readonly HashSet<uint> RenderCullingMarkerBaseFormIds =
    [
        0x00000015, // MultiBoundMarker
        0x00000017, // OcclusionMarker
        0x0000001F, // RoomMarker
        0x00000020 // PortalMarker
    ];

    private static readonly string[] RenderCullingBaseEditorIdNeedles =
    [
        "RoomMarker",
        "PortalMarker",
        "Occlusion",
        "MultiBound",
        "Culling"
    ];

    /// <summary>
    ///     Subrecords stripped from preserved master refs before re-emission. XEMI (Emittance —
    ///     links a ref to a REGN for ambient light tint) is dereferenced by the engine during
    ///     master-init when the plugin is ESM-flagged, BEFORE the REGN FormID is linked, so it
    ///     reads the raw FormID as a pointer and AVs (the Doc Mitchell light-rays crash on cell
    ///     exit). Emittance is a cosmetic runtime grid the engine recomputes; dropping the link
    ///     on preserved refs avoids the eager-resolve crash with no gameplay effect.
    /// </summary>
    private static readonly HashSet<string> EsmUnsafePreservedSubrecords =
        new(StringComparer.Ordinal) { "XEMI" };

    public static int PreserveMissing(
        IReadOnlyList<ParsedMainRecord> masterRefs,
        ISet<uint> dmpFormIdsInCell,
        IReadOnlyDictionary<uint, ParsedMainRecord> pcRecordsByFormId,
        List<byte[]> persistentRecords,
        List<byte[]> temporaryRecords,
        ConversionPipelineStats stats)
    {
        var preserved = 0;
        foreach (var masterRef in masterRefs)
        {
            if (dmpFormIdsInCell.Contains(masterRef.Header.FormId)
                || !IsStructuralCellRef(masterRef, pcRecordsByFormId))
            {
                continue;
            }

            var bytes = CellGrupBuilder.ReconstructRecordBytes(masterRef, EsmUnsafePreservedSubrecords);
            if ((masterRef.Header.Flags & 0x00000400u) != 0)
            {
                persistentRecords.Add(bytes);
            }
            else
            {
                temporaryRecords.Add(bytes);
            }

            preserved++;
            stats.IncrementEmitted(masterRef.Header.Signature);
        }

        return preserved;
    }

    public static int PreserveAllMissing(
        IReadOnlyList<ParsedMainRecord> masterRefs,
        ISet<uint> dmpFormIdsInCell,
        IReadOnlyDictionary<uint, MasterChildLocation> masterChildLocations,
        List<byte[]> persistentRecords,
        List<byte[]> vwdRecords,
        List<byte[]> temporaryRecords,
        ConversionPipelineStats stats)
    {
        var preserved = 0;
        foreach (var masterRef in masterRefs)
        {
            var formId = masterRef.Header.FormId;
            if (dmpFormIdsInCell.Contains(formId))
            {
                continue;
            }

            AddPreservedToOriginalBucket(
                masterRef, masterChildLocations, persistentRecords, vwdRecords, temporaryRecords);
            preserved++;
            stats.IncrementEmitted(masterRef.Header.Signature);
        }

        return preserved;
    }

    public static int PreserveLoadedReplacementMissing(
        IReadOnlyList<ParsedMainRecord> masterRefs,
        ISet<uint> coveredMasterFormIdsInCell,
        IReadOnlyDictionary<uint, MasterChildLocation> masterChildLocations,
        IReadOnlyDictionary<uint, ParsedMainRecord> pcRecordsByFormId,
        List<byte[]> persistentRecords,
        List<byte[]> vwdRecords,
        List<byte[]> temporaryRecords,
        ConversionPipelineStats stats,
        bool hasAuthoritativeDmpStructuralRefs = false,
        IReadOnlySet<string>? dmpCapturedBaseTypes = null,
        bool dropRenderCullingMarkers = false)
    {
        var preserved = 0;
        foreach (var masterRef in masterRefs)
        {
            var formId = masterRef.Header.FormId;
            if (coveredMasterFormIdsInCell.Contains(formId)
                || (dropRenderCullingMarkers && IsRenderCullingMarker(masterRef, pcRecordsByFormId))
                || (hasAuthoritativeDmpStructuralRefs && IsStructuralCellRef(masterRef, pcRecordsByFormId))
                || !ShouldPreserveInLoadedReplacement(masterRef, pcRecordsByFormId, dmpCapturedBaseTypes))
            {
                continue;
            }

            AddPreservedToOriginalBucket(
                masterRef, masterChildLocations, persistentRecords, vwdRecords, temporaryRecords);
            preserved++;
            stats.IncrementEmitted(masterRef.Header.Signature);
        }

        return preserved;
    }

    public static bool ShouldPreserveInLoadedReplacement(
        ParsedMainRecord masterRef,
        IReadOnlyDictionary<uint, ParsedMainRecord> pcRecordsByFormId,
        IReadOnlySet<string>? dmpCapturedBaseTypes = null)
    {
        if (masterRef.Header.Signature is "ACHR" or "ACRE")
        {
            return true;
        }

        if ((masterRef.Header.Flags & 0x00000400u) != 0)
        {
            return true;
        }

        if (HasScriptSubrecord(masterRef))
        {
            return true;
        }

        var baseFormId = ReadNameFormId(masterRef);
        var baseRecordResolved = baseFormId.HasValue
                                 && pcRecordsByFormId.TryGetValue(baseFormId.Value, out var baseRecord)
            ? baseRecord
            : null;

        if (baseRecordResolved is not null && HasScriptSubrecord(baseRecordResolved))
        {
            return true;
        }

        // Type-set-based preservation. If the DMP's placement snapshot didn't capture any
        // refs whose base record matches this master ref's base type signature, treat the
        // master ref as "out of scope for the DMP override" and preserve it. Addresses the
        // sparse-cell-replacement bug (Doc Mitchell's house: DMP captures DOOR+ACHR+FURN
        // only, master statics get wiped). The check is intentionally late so the existing
        // script / persistent / actor preservation still wins for genuinely-script-bearing
        // refs even when their base type happens to be in the DMP set.
        if (dmpCapturedBaseTypes is not null && baseRecordResolved is not null)
        {
            var baseSig = baseRecordResolved.Header.Signature;
            if (!dmpCapturedBaseTypes.Contains(baseSig))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsStructuralCellRef(
        ParsedMainRecord masterRef,
        IReadOnlyDictionary<uint, ParsedMainRecord> pcRecordsByFormId)
    {
        if (masterRef.Header.Signature != "REFR")
        {
            return false;
        }

        if (masterRef.Subrecords.Any(sub => StructuralCellRefSubrecords.Contains(sub.Signature)))
        {
            return true;
        }

        var baseFormId = ReadNameFormId(masterRef);
        if (!baseFormId.HasValue
            || !pcRecordsByFormId.TryGetValue(baseFormId.Value, out var baseRecord)
            || string.IsNullOrEmpty(baseRecord.EditorId))
        {
            return false;
        }

        return IsStructuralMarkerBase(baseRecord);
    }

    public static bool IsStructuralMarkerBase(ParsedMainRecord baseRecord)
    {
        if (string.IsNullOrEmpty(baseRecord.EditorId))
        {
            return false;
        }

        return StructuralBaseEditorIdNeedles.Any(needle =>
            baseRecord.EditorId!.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     True when <paramref name="masterRef" /> is a render-culling marker REFR — a room,
    ///     portal, occlusion, or multibound marker (base 0x15/0x17/0x1F/0x20, or a base whose
    ///     editor ID names one). These define the cell's portal/occlusion graph. For interiors
    ///     that get authoritatively replaced with prototype layouts, the master (final-game)
    ///     graph is positioned for final geometry and no longer matches the proto placements —
    ///     and it cannot be reconstructed from the DMP (XPRM/XPOD/XOCP bounds are not in the
    ///     runtime <c>TESObjectREFR</c> struct), so the only coherent option is to drop the
    ///     markers entirely, disabling portal culling so all present geometry renders.
    ///     The COLLISION marker (base 0x21) is intentionally NOT matched: it drives physics and
    ///     navigation rather than rendering, and is kept.
    /// </summary>
    public static bool IsRenderCullingMarker(
        ParsedMainRecord masterRef,
        IReadOnlyDictionary<uint, ParsedMainRecord> pcRecordsByFormId)
    {
        if (masterRef.Header.Signature != "REFR")
        {
            return false;
        }

        var baseFormId = ReadNameFormId(masterRef);
        if (!baseFormId.HasValue)
        {
            return false;
        }

        if (RenderCullingMarkerBaseFormIds.Contains(baseFormId.Value))
        {
            return true;
        }

        return pcRecordsByFormId.TryGetValue(baseFormId.Value, out var baseRecord)
               && !string.IsNullOrEmpty(baseRecord.EditorId)
               && RenderCullingBaseEditorIdNeedles.Any(needle =>
                   baseRecord.EditorId!.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    public static uint? ReadNameFormId(ParsedMainRecord record)
    {
        var name = record.Subrecords.FirstOrDefault(sub => sub.Signature == "NAME" && sub.Data.Length >= 4);
        return name is null
            ? null
            : BinaryPrimitives.ReadUInt32LittleEndian(name.Data);
    }

    private static void AddPreservedToOriginalBucket(
        ParsedMainRecord masterRef,
        IReadOnlyDictionary<uint, MasterChildLocation> masterChildLocations,
        List<byte[]> persistentRecords,
        List<byte[]> vwdRecords,
        List<byte[]> temporaryRecords)
    {
        var bytes = CellGrupBuilder.ReconstructRecordBytes(masterRef, EsmUnsafePreservedSubrecords);
        var groupType = masterChildLocations.TryGetValue(masterRef.Header.FormId, out var location)
            ? location.GroupType
            : GetDefaultChildGroupType(masterRef);
        switch (groupType)
        {
            case 8:
                persistentRecords.Add(bytes);
                break;
            case 10:
                vwdRecords.Add(bytes);
                break;
            default:
                temporaryRecords.Add(bytes);
                break;
        }
    }

    private static int GetDefaultChildGroupType(ParsedMainRecord masterRef)
    {
        return (masterRef.Header.Flags & 0x00000400u) != 0 ? 8 : 9;
    }

    private static bool HasScriptSubrecord(ParsedMainRecord record)
    {
        return record.Subrecords.Any(sub => sub.Signature == "SCRI");
    }
}
