using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;

/// <summary>
///     Stateless analysis helpers over placed references (REFR/ACHR/ACRE) shared by the
///     planner and planned writer: master placement reads, map-marker divergence checks,
///     structural-marker detection, and DMP capture coverage.
/// </summary>
internal static class PlacedReferenceAnalysis
{
    /// <summary>OverrideDoorCloning's overlap guard reads master placements at plan time.</summary>
    internal static bool TryReadPlacementData(ParsedMainRecord record, out PositionSubrecord position)
    {
        var data = record.Subrecords.FirstOrDefault(s => s.Signature == "DATA" && s.Data.Length >= 24);
        if (data is null)
        {
            position = new PositionSubrecord(0, 0, 0, 0, 0, 0, 0, false);
            return false;
        }

        position = new PositionSubrecord(
            BinaryPrimitives.ReadSingleLittleEndian(data.Data.AsSpan(0, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(data.Data.AsSpan(4, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(data.Data.AsSpan(8, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(data.Data.AsSpan(12, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(data.Data.AsSpan(16, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(data.Data.AsSpan(20, 4)),
            0,
            false);
        return true;
    }

    internal static bool MapMarkerDiffersFromMaster(PlacedReference placed, ParsedMainRecord masterRecord)
    {
        if (!placed.IsMapMarker)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(placed.MarkerName))
        {
            var masterName = masterRecord.Subrecords
                .FirstOrDefault(s => s.Signature == "FULL")?.DataAsString;
            if (!string.Equals(placed.MarkerName, masterName ?? string.Empty, StringComparison.Ordinal))
            {
                return true;
            }
        }

        if (placed.MarkerType.HasValue)
        {
            var masterTypeSubrecord = masterRecord.Subrecords
                .FirstOrDefault(s => s.Signature == "TNAM" && s.Data.Length >= 2);
            var masterType = masterTypeSubrecord is null
                ? (ushort)0
                : BinaryPrimitives.ReadUInt16LittleEndian(masterTypeSubrecord.Data.AsSpan(0, 2));
            if (masterType != (ushort)placed.MarkerType.Value)
            {
                return true;
            }
        }

        if (TryReadPlacementData(masterRecord, out var masterPosition))
        {
            var dx = placed.X - masterPosition.X;
            var dy = placed.Y - masterPosition.Y;
            var dz = placed.Z - masterPosition.Z;
            if (dx * dx + dy * dy + dz * dz > 1.0f)
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsRuntimeStructuralMarkerPlacement(
        PlacedReference placed,
        IReadOnlyDictionary<uint, ParsedMainRecord> pcRecordsByFormId,
        out string? baseEditorId)
    {
        baseEditorId = null;
        if (placed.RecordType != "REFR"
            || placed.BaseFormId == 0
            || !pcRecordsByFormId.TryGetValue(placed.BaseFormId, out var baseRecord)
            || !CellStructuralReferencePreserver.IsStructuralMarkerBase(baseRecord))
        {
            return false;
        }

        baseEditorId = baseRecord.EditorId;
        return true;
    }

    /// <summary>
    ///     Compute the set of base-record signatures (e.g. <c>STAT</c>, <c>DOOR</c>,
    ///     <c>FURN</c>) represented among the placed objects the DMP captured for a cell.
    ///     Used by the <c>LoadedReplacement</c> preservation path to keep master refs whose
    ///     base type the DMP didn't capture — addresses sparse-cell captures like Doc
    ///     Mitchell's house where the DMP captures only DOOR/ACHR/FURN and master statics
    ///     would otherwise be wiped.
    /// </summary>
    internal static HashSet<string> ComputeDmpCapturedBaseTypes(
        IReadOnlyList<PlacedReference> placedObjects,
        IReadOnlyDictionary<uint, ParsedMainRecord> pcRecordsByFormId)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var placed in placedObjects)
        {
            if (placed.BaseFormId == 0)
            {
                continue;
            }

            if (pcRecordsByFormId.TryGetValue(placed.BaseFormId, out var baseRecord)
                && !string.IsNullOrEmpty(baseRecord.Header.Signature))
            {
                result.Add(baseRecord.Header.Signature);
            }
        }

        return result;
    }
}
