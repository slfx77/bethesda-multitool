using FalloutXbox360Utils.Core.Formats.Esm.Export.Projections;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

internal static class VirtualCellCanonicalizer
{
    internal static Dictionary<CellCoordinateKey, RealCellCandidate> BuildVirtualCellCanonicalFormIds(
        IEnumerable<RecordCollection> recordCollections)
    {
        var candidates = new Dictionary<CellCoordinateKey, Dictionary<uint, RealCellCandidate>>();
        var virtualOnlyKeys = new HashSet<CellCoordinateKey>();
        var allCellFormIds = new HashSet<uint>();
        foreach (var collection in recordCollections)
        {
            foreach (var cell in collection.Cells)
            {
                allCellFormIds.Add(cell.FormId);
                if (cell.IsVirtual &&
                    !cell.IsInterior &&
                    !cell.IsPersistentCell &&
                    !cell.IsUnresolvedBucket &&
                    TryGetCellCoordinateKey(cell, out var virtualKey))
                {
                    virtualOnlyKeys.Add(virtualKey);
                }

                if (!IsStableRealExteriorCell(cell) ||
                    !TryGetCellCoordinateKey(cell, out var key))
                {
                    continue;
                }

                if (!candidates.TryGetValue(key, out var formIds))
                {
                    formIds = [];
                    candidates[key] = formIds;
                }

                if (!formIds.TryGetValue(cell.FormId, out var existingCandidate))
                {
                    formIds[cell.FormId] = new RealCellCandidate(cell.FormId, cell.EditorId, cell.FullName, false);
                }
                else
                {
                    formIds[cell.FormId] = existingCandidate with
                    {
                        EditorId = existingCandidate.EditorId ?? cell.EditorId,
                        DisplayName = existingCandidate.DisplayName ?? cell.FullName
                    };
                }
            }
        }

        var canonical = new Dictionary<CellCoordinateKey, RealCellCandidate>();
        foreach (var (key, formIds) in candidates)
        {
            if (formIds.Count == 1)
            {
                canonical[key] = formIds.Values.Single();
            }
        }

        var nextSyntheticFormId = 0xFD000001u;
        foreach (var key in virtualOnlyKeys
                     .OrderBy(key => key.WorldspaceFormId)
                     .ThenBy(key => key.GridY)
                     .ThenBy(key => key.GridX))
        {
            if (canonical.ContainsKey(key))
            {
                continue;
            }

            while (allCellFormIds.Contains(nextSyntheticFormId))
            {
                nextSyntheticFormId++;
            }

            canonical[key] = new RealCellCandidate(nextSyntheticFormId, null, null, true);
            allCellFormIds.Add(nextSyntheticFormId);
            nextSyntheticFormId++;
        }

        return canonical;
    }

    internal static bool TryGetVirtualCellCanonicalFormId(
        CellRecord cell,
        IReadOnlyDictionary<CellCoordinateKey, RealCellCandidate> canonicalFormIds,
        out RealCellCandidate canonicalCell)
    {
        canonicalCell = default;
        if (!cell.IsVirtual ||
            cell.IsInterior ||
            cell.IsPersistentCell ||
            cell.IsUnresolvedBucket ||
            !TryGetCellCoordinateKey(cell, out var key))
        {
            return false;
        }

        return canonicalFormIds.TryGetValue(key, out canonicalCell);
    }

    internal static bool IsStableRealExteriorCell(CellRecord cell)
    {
        return !cell.IsInterior
               && !cell.IsVirtual
               && !cell.IsPersistentCell
               && !cell.IsUnresolvedBucket
               && cell.FormId is > 0 and < 0xFE000000
               && cell.WorldspaceFormId.HasValue
               && cell.GridX.HasValue
               && cell.GridY.HasValue;
    }

    internal static bool TryGetCellCoordinateKey(CellRecord cell, out CellCoordinateKey key)
    {
        if (cell.WorldspaceFormId.HasValue &&
            cell.GridX.HasValue &&
            cell.GridY.HasValue)
        {
            key = new CellCoordinateKey(cell.WorldspaceFormId.Value, cell.GridX.Value, cell.GridY.Value);
            return true;
        }

        key = default;
        return false;
    }

    // ---------- Skeleton overloads (Phase 3: streaming-pipeline support) ----------
    //
    // These accept the lightweight CellSkeleton from CrossDumpSourceProjection so the
    // canonicalizer can run after the heavy CellRecord has been released. The bodies
    // mirror the CellRecord versions exactly — the byte-identical parity is enforced
    // by VirtualCellCanonicalizerParityTests (see Phase 2/3 test scaffolding).

    internal static Dictionary<CellCoordinateKey, RealCellCandidate> BuildVirtualCellCanonicalFormIds(
        IEnumerable<IReadOnlyList<CellSkeleton>> cellSkeletonsBySource)
    {
        var candidates = new Dictionary<CellCoordinateKey, Dictionary<uint, RealCellCandidate>>();
        var virtualOnlyKeys = new HashSet<CellCoordinateKey>();
        var allCellFormIds = new HashSet<uint>();
        foreach (var cells in cellSkeletonsBySource)
        {
            foreach (var cell in cells)
            {
                allCellFormIds.Add(cell.FormId);
                if (cell.IsVirtual &&
                    !cell.IsInterior &&
                    !cell.IsPersistentCell &&
                    !cell.IsUnresolvedBucket &&
                    TryGetCellCoordinateKey(cell, out var virtualKey))
                {
                    virtualOnlyKeys.Add(virtualKey);
                }

                if (!IsStableRealExteriorCell(cell) ||
                    !TryGetCellCoordinateKey(cell, out var key))
                {
                    continue;
                }

                if (!candidates.TryGetValue(key, out var formIds))
                {
                    formIds = [];
                    candidates[key] = formIds;
                }

                if (!formIds.TryGetValue(cell.FormId, out var existingCandidate))
                {
                    formIds[cell.FormId] = new RealCellCandidate(cell.FormId, cell.EditorId, cell.FullName, false);
                }
                else
                {
                    formIds[cell.FormId] = existingCandidate with
                    {
                        EditorId = existingCandidate.EditorId ?? cell.EditorId,
                        DisplayName = existingCandidate.DisplayName ?? cell.FullName
                    };
                }
            }
        }

        var canonical = new Dictionary<CellCoordinateKey, RealCellCandidate>();
        foreach (var (key, formIds) in candidates)
        {
            if (formIds.Count == 1)
            {
                canonical[key] = formIds.Values.Single();
            }
        }

        var nextSyntheticFormId = 0xFD000001u;
        foreach (var key in virtualOnlyKeys
                     .OrderBy(key => key.WorldspaceFormId)
                     .ThenBy(key => key.GridY)
                     .ThenBy(key => key.GridX))
        {
            if (canonical.ContainsKey(key))
            {
                continue;
            }

            while (allCellFormIds.Contains(nextSyntheticFormId))
            {
                nextSyntheticFormId++;
            }

            canonical[key] = new RealCellCandidate(nextSyntheticFormId, null, null, true);
            allCellFormIds.Add(nextSyntheticFormId);
            nextSyntheticFormId++;
        }

        return canonical;
    }

    internal static bool TryGetVirtualCellCanonicalFormId(
        CellSkeleton cell,
        IReadOnlyDictionary<CellCoordinateKey, RealCellCandidate> canonicalFormIds,
        out RealCellCandidate canonicalCell)
    {
        canonicalCell = default;
        if (!cell.IsVirtual ||
            cell.IsInterior ||
            cell.IsPersistentCell ||
            cell.IsUnresolvedBucket ||
            !TryGetCellCoordinateKey(cell, out var key))
        {
            return false;
        }

        return canonicalFormIds.TryGetValue(key, out canonicalCell);
    }

    internal static bool IsStableRealExteriorCell(CellSkeleton cell)
    {
        return !cell.IsInterior
               && !cell.IsVirtual
               && !cell.IsPersistentCell
               && !cell.IsUnresolvedBucket
               && cell.FormId is > 0 and < 0xFE000000
               && cell.WorldspaceFormId.HasValue
               && cell.GridX.HasValue
               && cell.GridY.HasValue;
    }

    internal static bool TryGetCellCoordinateKey(CellSkeleton cell, out CellCoordinateKey key)
    {
        if (cell.WorldspaceFormId.HasValue &&
            cell.GridX.HasValue &&
            cell.GridY.HasValue)
        {
            key = new CellCoordinateKey(cell.WorldspaceFormId.Value, cell.GridX.Value, cell.GridY.Value);
            return true;
        }

        key = default;
        return false;
    }

    internal static RecordReport RebaseVirtualCellReport(RecordReport report, RealCellCandidate canonicalCell)
    {
        var sections = new List<ReportSection>(report.Sections.Count);
        foreach (var section in report.Sections)
        {
            var fields = new List<ReportField>(section.Fields.Count);
            foreach (var field in section.Fields)
            {
                if (field.Key == "FormID")
                {
                    fields.Add(field with { Value = ReportValue.String($"0x{canonicalCell.FormId:X8}") });
                }
                else if (field.Key == "Editor ID")
                {
                    if (canonicalCell.EditorId != null)
                    {
                        fields.Add(field with { Value = ReportValue.String(canonicalCell.EditorId) });
                    }
                }
                else if (field.Key == "Display Name")
                {
                    if (canonicalCell.DisplayName != null)
                    {
                        fields.Add(field with { Value = ReportValue.String(canonicalCell.DisplayName) });
                    }
                }
                else
                {
                    fields.Add(field);
                }
            }

            if (section.Name.Equals("Identity", StringComparison.OrdinalIgnoreCase))
            {
                if (canonicalCell.EditorId != null && fields.All(field => field.Key != "Editor ID"))
                {
                    fields.Add(new ReportField("Editor ID", ReportValue.String(canonicalCell.EditorId)));
                }

                if (canonicalCell.DisplayName != null && fields.All(field => field.Key != "Display Name"))
                {
                    fields.Add(new ReportField("Display Name", ReportValue.String(canonicalCell.DisplayName)));
                }
            }

            if (fields.Count > 0)
            {
                sections.Add(section with { Fields = fields });
            }
        }

        return report with
        {
            FormId = canonicalCell.FormId,
            EditorId = canonicalCell.EditorId,
            DisplayName = canonicalCell.DisplayName,
            Sections = sections
        };
    }

    internal static void AddUpgradedVirtualCellForDump(
        Dictionary<uint, SortedDictionary<int, SortedSet<uint>>> upgradedVirtualCellIdsByDump,
        uint realFormId,
        int dumpIdx,
        uint originalVirtualFormId)
    {
        if (!upgradedVirtualCellIdsByDump.TryGetValue(realFormId, out var dumpMap))
        {
            dumpMap = [];
            upgradedVirtualCellIdsByDump[realFormId] = dumpMap;
        }

        if (!dumpMap.TryGetValue(dumpIdx, out var originalIds))
        {
            originalIds = [];
            dumpMap[dumpIdx] = originalIds;
        }

        originalIds.Add(originalVirtualFormId);
    }

    internal static void AppendVirtualCellAuditMetadata(
        CrossDumpRecordIndex index,
        Dictionary<uint, SortedSet<uint>> upgradedVirtualCellIds,
        Dictionary<uint, SortedDictionary<int, SortedSet<uint>>> upgradedVirtualCellIdsByDump)
    {
        if (upgradedVirtualCellIds.Count == 0)
        {
            return;
        }

        if (!index.RecordMetadata.TryGetValue("Cell", out var cellMetadata))
        {
            cellMetadata = new Dictionary<uint, Dictionary<string, string>>();
            index.RecordMetadata["Cell"] = cellMetadata;
        }

        foreach (var (realFormId, originalVirtualFormIds) in upgradedVirtualCellIds)
        {
            if (!cellMetadata.TryGetValue(realFormId, out var metadata))
            {
                metadata = new Dictionary<string, string>();
                cellMetadata[realFormId] = metadata;
            }

            metadata["upgradedVirtualFormIds"] =
                string.Join(", ", originalVirtualFormIds.Select(formId => $"0x{formId:X8}"));
            if (upgradedVirtualCellIdsByDump.TryGetValue(realFormId, out var dumpMap))
            {
                metadata["upgradedVirtualFormIdsByDump"] = string.Join(
                    ";",
                    dumpMap.Select(dumpEntry =>
                        $"{dumpEntry.Key}:{string.Join(", ", dumpEntry.Value.Select(formId => $"0x{formId:X8}"))}"));
            }
        }
    }
}

internal readonly record struct CellCoordinateKey(uint WorldspaceFormId, int GridX, int GridY);

internal readonly record struct RealCellCandidate(
    uint FormId,
    string? EditorId,
    string? DisplayName,
    bool IsSyntheticVirtual);
