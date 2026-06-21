namespace EsmAnalyzer.Commands;

/// <summary>
///     Grid-attribution heuristic for the attribute-dangling pipeline. Maps sweep rows to
///     (worldspace, cell) winners per grid, and attributes per-REFR positions using ESM
///     ref→cell authority first, then the grid heuristic, then CUT.
/// </summary>
internal static class DmpDanglingAttributor
{
    public static AttributionResult Attribute(AuthorityView auth, IEnumerable<SweepRow> rows)
    {
        var result = new AttributionResult();

        foreach (var row in rows)
        {
            var key = (row.GridX, row.GridY);
            if (auth.CellsByGrid.TryGetValue(key, out var candidates) && candidates.Count > 0)
            {
                // Rank: has editor_id desc, worldspace cell count desc, ws_id asc (stable)
                var winner = candidates
                    .OrderByDescending(c => !string.IsNullOrWhiteSpace(c.EditorId))
                    .ThenByDescending(c => c.WorldspaceFormId.HasValue
                        ? auth.WorldspaceCellCounts.GetValueOrDefault(c.WorldspaceFormId.Value, 0)
                        : 0)
                    .ThenBy(c => c.WorldspaceFormId)
                    .First();

                var hasEdid = !string.IsNullOrWhiteSpace(winner.EditorId);
                string confidence;
                if (candidates.Count == 1 && hasEdid)
                {
                    confidence = "HIGH";
                }
                else if (hasEdid)
                {
                    confidence = "STRONG";
                }
                else if (candidates.Count == 1)
                {
                    confidence = "MEDIUM";
                }
                else
                {
                    confidence = "LOW";
                }

                if (!result.GridAttributions.TryGetValue(key, out var attr))
                {
                    attr = new GridAttribution
                    {
                        WorldspaceFormId = winner.WorldspaceFormId ?? 0,
                        WorldspaceEditorId = winner.WorldspaceFormId.HasValue
                            ? auth.WorldspaceNames.GetValueOrDefault(winner.WorldspaceFormId.Value, "")
                            : "",
                        CellFormId = winner.FormId,
                        CellEditorId = winner.EditorId,
                        CandidateCount = candidates.Count,
                        Confidence = confidence
                    };
                    result.GridAttributions[key] = attr;
                }

                attr.RefCount += row.NullParentRefrs;
                attr.EvidenceDumps.Add(row.Dump);
                foreach (var f in ParseSampleFormIds(row.SampleFormIds))
                {
                    if (attr.SampleFormIds.Count < 8)
                    {
                        attr.SampleFormIds.Add(f);
                    }
                }
            }
            else
            {
                // Cut region: no cell in any worldspace at this grid
                var cut = result.CutRegions.FirstOrDefault(c => c.Gx == row.GridX && c.Gy == row.GridY);
                if (cut == null)
                {
                    cut = new CutRegion { Gx = row.GridX, Gy = row.GridY };
                    result.CutRegions.Add(cut);
                }

                cut.RefCount += row.NullParentRefrs;
                cut.EvidenceDumps.Add(row.Dump);
                foreach (var f in ParseSampleFormIds(row.SampleFormIds))
                {
                    if (cut.SampleFormIds.Count < 8)
                    {
                        cut.SampleFormIds.Add(f);
                    }
                }
            }
        }

        return result;
    }

    private static IEnumerable<uint> ParseSampleFormIds(string field)
    {
        if (string.IsNullOrWhiteSpace(field))
        {
            yield break;
        }
        foreach (var part in field.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (DmpDanglingParsing.TryParseHexUInt(part, out var f) && f != 0)
            {
                yield return f;
            }
        }
    }

    /// <summary>
    ///     For each REFR position, attribute to (worldspace, cell) with priority:
    ///     1. ESM-derived ref→cell (authoritative — FormID exists as child of CELL in a Sample ESM)
    ///     2. Grid attribution (per (gx, gy) heuristic match from earlier pass) —
    ///        SKIPPED for ACHR/ACRE base types, because actor positions in memory dumps reflect
    ///        last-known runtime state (player-visited, AI-driven, or stale-cache), not their
    ///        canonical cell. Putting Mr. House at the player's last-loaded Novac grid would
    ///        be misleading. Without an ESM match, actors fall through to CUT.
    ///     3. CUT (no cell record at this grid, OR an actor with no ESM authority)
    /// </summary>
    public static void AttributePositions(
        AuthorityView auth,
        AttributionResult attribution,
        IEnumerable<PositionRow> positions,
        IReadOnlyDictionary<uint, ReferenceEnrichment> enrichment)
    {
        foreach (var p in positions)
        {
            enrichment.TryGetValue(p.FormId, out var er);

            // 1. ESM-derived authoritative attribution
            if (auth.RefToCell.TryGetValue(p.FormId, out var esmCellFid) &&
                auth.Cells.TryGetValue(esmCellFid, out var esmCell))
            {
                attribution.Positions.Add(MakePositionAttribution(
                    p, er, esmCell.WorldspaceFormId, esmCellFid, esmCell.EditorId, "ESM"));
                continue;
            }

            // 2. Grid-attribution fallback — but NOT for actors (ACHR/ACRE).
            // BaseFormType 0x2A = NPC_, 0x2B = CREA in the runtime FormType enum (RuntimeBuildOffsets).
            // These values are stable across all post-shift builds (the +1 shift at 0x46 only
            // affects types above 0x45, so NPC_/CREA are unaffected).
            var isActor = IsActorBaseType(p.BaseFormType);
            if (!isActor && attribution.GridAttributions.TryGetValue((p.GridX, p.GridY), out var gridAttr))
            {
                attribution.Positions.Add(MakePositionAttribution(
                    p, er,
                    gridAttr.WorldspaceFormId == 0 ? null : gridAttr.WorldspaceFormId,
                    gridAttr.CellFormId, gridAttr.CellEditorId, gridAttr.Confidence));
                continue;
            }

            // 3. Cut content — no cell at this grid, OR an actor without ESM authority
            attribution.Positions.Add(MakePositionAttribution(
                p, er, null, 0, "", "CUT"));
        }
    }

    private static bool IsActorBaseType(byte baseFormType) => baseFormType is 0x2A or 0x2B;

    private static PositionAttribution MakePositionAttribution(
        PositionRow p, ReferenceEnrichment? er,
        uint? worldspace, uint cellFormId, string cellEditorId, string confidence)
    {
        return new PositionAttribution
        {
            FormId = p.FormId,
            X = p.X, Y = p.Y, Z = p.Z, Scale = p.Scale,
            GridX = p.GridX, GridY = p.GridY,
            FoundInDumps = p.FoundInDumps,
            WorldspaceFormId = worldspace,
            CellFormId = cellFormId,
            CellEditorId = cellEditorId,
            BaseFormId = p.BaseFormId,
            BaseFormType = p.BaseFormType,
            Confidence = confidence,
            EditorId = er?.EditorId,
            BaseEditorId = er?.BaseEditorId,
            BaseFullName = er?.BaseFullName,
            ModelPath = er?.ModelPath,
            RecordType = er?.RecordType,
            IsMapMarker = er?.IsMapMarker ?? false,
            MarkerName = er?.MarkerName,
            MarkerType = er?.MarkerType
        };
    }
}
