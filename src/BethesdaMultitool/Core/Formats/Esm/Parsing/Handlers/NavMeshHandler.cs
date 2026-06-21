using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Processing;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;

/// <summary>
///     Parses Navigation Mesh (NAVM) and NavMesh Info Map (NAVI) records, including the
///     runtime-discovery path that walks the engine's NavMeshInfoMap to surface loaded
///     BSNavMesh structs that have no editor-id hash entry.
/// </summary>
internal sealed class NavMeshHandler(RecordParserContext context) : RecordHandlerBase(context)
{
    /// <summary>
    ///     Parse all Navigation Mesh (NAVM) records.
    /// </summary>
    internal List<NavMeshRecord> ParseNavMeshes()
    {
        var navMeshes = ParseRecordList("NAVM", 8192,
            ParseNavMeshFromAccessor,
            record => new NavMeshRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            });

        Context.MergeRuntimeRecords(navMeshes, 0x43, n => n.FormId,
            (reader, entry) => reader.ReadRuntimeNavMesh(entry), "navmeshes");

        // The editor-id-hash discovery path above is empty for navmeshes (verified across
        // xex2/4/21/44: zero NAVMs with editor IDs, so the BSTHashMap<BSFixedString,TESForm*>
        // never lists them). Walk the engine's NavMeshInfoMap.InfoMap NiTPointerMap directly
        // so the hundreds of loaded BSNavMesh structs surrounding the active cell grid become
        // visible. Each runtime-discovered NAVM gets synthetic RawSubrecords (DATA + NVER +
        // NVVX + NVTR + NVDP) reconstructed from the BSNavMesh's vertex/triangle/portal
        // BSSimpleArrays so both the GUI overlay (WorldMapNavMeshOverlayRenderer) and the
        // ESP encoder can consume them like any ESM-parsed record.
        DiscoverRuntimeNavMeshesFromInfoMap(navMeshes);

        return navMeshes;
    }

    private void DiscoverRuntimeNavMeshesFromInfoMap(List<NavMeshRecord> navMeshes)
    {
        if (Context.RuntimeReader == null)
        {
            return;
        }

        var knownNavmFormIds = new HashSet<uint>(navMeshes.Select(n => n.FormId));

        // Build the drift remap from drift-corrected editor-id entries. RuntimeBuildOffsets.
        // ApplyDriftCorrection populates OriginalFormType on entries whose raw heap-memory
        // FormType byte got remapped to a canonical value. The enumerator applies this remap
        // to every raw byte it reads in Path 1's pAllForms walk + Path 2/3 cell validation so
        // the canonical 0x39/0x41/0x43 constants work across early-build drift (e.g. Nov 2009
        // +1 shift at 0x46). Identity (null) when no drift was detected.
        var driftRemap = BuildDriftRemap(Context.ScanResult.RuntimeEditorIds);

        // Multi-source cell enumeration. Falls back to editor-id-only enumeration
        // (Path 0 inline) when the upstream pointer-triple scan didn't expose pAllForms.
        var enumerator = Context.RuntimeReader.CreateRuntimeCellEnumerator(
            Context.ScanResult.PAllFormsVa,
            driftRemap);
        var wrldFormIds = Context.ScanResult.MainRecords
            .Where(r => r.RecordType == "WRLD")
            .Select(r => r.FormId)
            .ToHashSet();
        // navMeshes at this point contains only byte-stream NAVMs (Path 0/runtime merge has
        // already run via MergeRuntimeRecords above). Their FormIDs anchor Path 4's NAVM-byte
        // calibration so undetected-drift builds (e.g. xex.dmp) still classify NAVMs in
        // pAllForms even when the upstream drift detector returned null.
        var navmFormIds = navMeshes.Select(n => n.FormId).Where(id => id != 0).ToHashSet();
        var cells = enumerator?.Enumerate(Context.ScanResult.RuntimeEditorIds, wrldFormIds, navmFormIds)
                    ?? FallbackEnumerateEditorIdCellsOnly(Context.ScanResult.RuntimeEditorIds);

        var perSourceAdds = new int[4];
        var cellsWithNavm = 0;
        foreach (var hit in cells.Cells)
        {
            var discovered = Context.RuntimeReader.DiscoverNavMeshesForCellVa(hit.CellVa, hit.FormId);
            var addedThisCell = 0;
            foreach (var navm in discovered)
            {
                if (!knownNavmFormIds.Add(navm.FormId))
                {
                    continue;
                }

                navMeshes.Add(navm);
                perSourceAdds[(int)hit.Source]++;
                addedThisCell++;
            }

            if (addedThisCell > 0)
            {
                cellsWithNavm++;
            }
        }

        // Path 4: walk pAllForms directly for NAVM entries. BSNavMesh is TESForm-derived so
        // each pAllForms value VA can be projected straight into a synthetic NavMeshRecord
        // without going through the cell graph. On dumps where the engine has detached
        // NavMeshArrays from cells (empirically: every cell path tested on xex21 had
        // pNavMeshes=null even for active grid cells), this is the only path that surfaces
        // runtime NAVM data.
        //
        // Two validator modes:
        //   * Permissive over NavMeshVas — calibration is anchored or drift-confirmed, so
        //     candidates here are already trustworthy; the validator only catches rare
        //     shape outliers (e.g. paged-out structs) and allows detached navmeshes through.
        //   * Strict over NavMeshVaCandidates — uncalibrated build (no anchor, no drift);
        //     the enumerator emitted a speculative byte-window net and the validator
        //     cross-references each candidate's pParentCell against KnownCellVas to reject
        //     non-BSNavMesh structs (DIAL / INFO / etc. at neighboring FormType bytes).
        var knownCellVas = new HashSet<uint>(cells.Cells.Count);
        foreach (var hit in cells.Cells)
        {
            knownCellVas.Add(hit.CellVa);
        }

        var permissiveValidator = Context.RuntimeReader.CreateBsNavMeshValidator(
            knownCellVas, BsNavMeshValidationMode.Permissive);
        var strictValidator = Context.RuntimeReader.CreateBsNavMeshValidator(
            knownCellVas, BsNavMeshValidationMode.Strict);

        var (addedFromDirectNavm, rejectedFromDirectNavm) = TryAddValidatedNavMeshes(
            cells.NavMeshVas, permissiveValidator, navMeshes, knownNavmFormIds);
        var (addedFromSpeculative, rejectedFromSpeculative) = TryAddValidatedNavMeshes(
            cells.NavMeshVaCandidates, strictValidator, navMeshes, knownNavmFormIds);

        // Belt-and-braces: NAVI singleton path. Currently a no-op on vanilla FNV (the singleton
        // has no editor ID), but if a future build adds one or we pivot to PDB-global discovery,
        // this picks up the slack.
        var addedFromSingleton = 0;
        foreach (var entry in Context.ScanResult.RuntimeEditorIds)
        {
            if (entry.FormType != 0x38)
            {
                continue;
            }

            var discovered = Context.RuntimeReader.DiscoverNavMeshesFromInfoMap(entry);
            foreach (var navm in discovered)
            {
                if (!knownNavmFormIds.Add(navm.FormId))
                {
                    continue;
                }

                navMeshes.Add(navm);
                addedFromSingleton++;
            }
        }

        var stats = cells.Stats;
        if (perSourceAdds.Sum() > 0 || addedFromSingleton > 0
            || addedFromDirectNavm > 0 || addedFromSpeculative > 0
            || stats.UniqueTotal > 0)
        {
            Logger.Instance.Debug(
                $"  [Semantic] Runtime NAVM discovery cells: editor-id={stats.FromEditorIdHash:N0}, " +
                $"all-forms={stats.FromAllFormsHash:N0}, worldspace-grid={stats.FromWorldspaceGrid:N0}, " +
                $"heap-scan={stats.FromHeapScan:N0} (unique={stats.UniqueTotal:N0}); " +
                $"direct-navm-vas={cells.NavMeshVas.Count:N0}, " +
                $"speculative-navm-vas={cells.NavMeshVaCandidates.Count:N0}");
            Logger.Instance.Debug(
                $"  [Semantic] Runtime NAVM discovery navmeshes: +{perSourceAdds[0]:N0}/+{perSourceAdds[1]:N0}/" +
                $"+{perSourceAdds[2]:N0}/+{perSourceAdds[3]:N0} per-cell-source, " +
                $"+{addedFromDirectNavm:N0} direct (-{rejectedFromDirectNavm:N0} rejected), " +
                $"+{addedFromSpeculative:N0} speculative (-{rejectedFromSpeculative:N0} rejected), " +
                $"+{addedFromSingleton:N0} NAVI singleton " +
                $"({cellsWithNavm:N0} cells produced ≥1 NAVM, grand total {navMeshes.Count:N0}).");
        }
    }

    /// <summary>
    ///     Validate each candidate VA with <paramref name="validator" />, then project surviving
    ///     candidates into <see cref="NavMeshRecord" /> via <c>RuntimeStructReader.DiscoverNavMeshAtVa</c>.
    ///     Dedup by FormID against <paramref name="knownNavmFormIds" /> (mutated). Returns
    ///     (added count, rejected count). "Rejected" only counts validator-side rejections;
    ///     a null return from <c>DiscoverNavMeshAtVa</c> after validation passed (rare: the
    ///     struct page-faulted in between) counts as neither added nor rejected.
    /// </summary>
    private (int Added, int Rejected) TryAddValidatedNavMeshes(
        IReadOnlyList<uint> candidateVas,
        BsNavMeshStructuralValidator validator,
        List<NavMeshRecord> navMeshes,
        HashSet<uint> knownNavmFormIds)
    {
        if (candidateVas.Count == 0 || Context.RuntimeReader == null)
        {
            return (0, 0);
        }

        var added = 0;
        var rejected = 0;
        foreach (var navMeshVa in candidateVas)
        {
            if (!validator.LooksLikeBsNavMesh(navMeshVa))
            {
                rejected++;
                continue;
            }

            var navm = Context.RuntimeReader.DiscoverNavMeshAtVa(navMeshVa, fallbackParentCellFormId: 0);
            if (navm is null)
            {
                continue;
            }

            if (!knownNavmFormIds.Add(navm.FormId))
            {
                continue;
            }

            navMeshes.Add(navm);
            added++;
        }

        return (added, rejected);
    }

    /// <summary>
    ///     Reconstructs the drift remap (raw-byte → canonical-byte) from
    ///     <see cref="RuntimeEditorIdEntry.OriginalFormType" /> fields. These are populated
    ///     upstream by <c>RuntimeBuildOffsets.ApplyDriftCorrection</c> only when an entry's
    ///     FormType byte was changed; absence means identity. The reconstructed dictionary
    ///     is passed into <see cref="RuntimeCellEnumerator" /> so its raw-heap reads
    ///     (Path 1 pAllForms walk, Path 2/3 cell validators) use the same canonical FormType
    ///     space as the editor-id entries it shares with Path 0.
    /// </summary>
    private static Dictionary<byte, byte>? BuildDriftRemap(
        IReadOnlyList<RuntimeEditorIdEntry> editorIdEntries)
    {
        Dictionary<byte, byte>? remap = null;
        foreach (var entry in editorIdEntries)
        {
            if (entry.OriginalFormType is not { } original || original == entry.FormType)
            {
                continue;
            }

            remap ??= [];
            remap.TryAdd(original, entry.FormType);
        }

        return remap;
    }

    private static RuntimeCellEnumeration FallbackEnumerateEditorIdCellsOnly(
        IReadOnlyList<RuntimeEditorIdEntry> editorIdEntries)
    {
        var hits = new List<RuntimeCellHit>();
        foreach (var entry in editorIdEntries)
        {
            if (entry.FormType != 0x39 || entry.TesFormPointer is not { } ptr || ptr == 0 || entry.FormId == 0)
            {
                continue;
            }

            hits.Add(new RuntimeCellHit(entry.FormId, unchecked((uint)ptr), RuntimeCellSource.EditorIdHash));
        }

        return new RuntimeCellEnumeration(
            hits,
            new RuntimeCellEnumeratorStats(hits.Count, 0, 0, 0, hits.Count),
            [],
            []);
    }

    private NavMeshRecord? ParseNavMeshFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new NavMeshRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        uint cellFormId = 0, vertexCount = 0, triangleCount = 0;
        uint worldspaceFormId = 0;
        int? gridX = null, gridY = null;
        var doorPortalCount = 0;
        var rawSubrecords = new List<NavMeshSubrecord>();

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmStringUtils.ReadNullTermString(subData);
                    if (!string.IsNullOrEmpty(editorId))
                    {
                        Context.FormIdToEditorId[record.FormId] = editorId;
                    }

                    break;
                case "DATA" when sub.DataLength >= 20:
                {
                    if (SubrecordSchemaView.TryRead("DATA", "NAVM", subData, record.IsBigEndian) is { } v)
                    {
                        cellFormId = v.UInt32("Cell");
                        vertexCount = v.UInt32("VertexCount");
                        triangleCount = v.UInt32("TriangleCount");
                    }

                    break;
                }
                case "NVDP":
                    // Each door portal is 8 bytes
                    if (sub.DataLength >= 8)
                    {
                        doorPortalCount = sub.DataLength / 8;
                    }

                    break;
                case "NVNM" when sub.DataLength >= 20:
                {
                    // Skyrim packs the whole navmesh into one NVNM blob (no DATA/NVVX/NVTR). The header
                    // carries the cell linkage + vertex/triangle counts; geometry is decoded by
                    // NavMeshGeometry.TryParse from the same subrecord.
                    if (ParseNvnmHeader(subData, record.IsBigEndian) is { } h)
                    {
                        cellFormId = h.CellFormId;
                        worldspaceFormId = h.WorldspaceFormId;
                        gridX = h.GridX;
                        gridY = h.GridY;
                        vertexCount = h.VertexCount;
                        triangleCount = h.TriangleCount;
                    }

                    break;
                }
            }

            // Capture subrecord bytes verbatim (or post-endian-conversion for Xbox 360 source)
            // so the cell pipeline can re-emit DMP NAVMs in cells master doesn't cover.
            CaptureNavmSubrecord(record, sub.Signature, subData, rawSubrecords);
        }

        return new NavMeshRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            CellFormId = cellFormId,
            WorldspaceFormId = worldspaceFormId,
            GridX = gridX,
            GridY = gridY,
            VertexCount = vertexCount,
            TriangleCount = triangleCount,
            DoorPortalCount = doorPortalCount,
            RawSubrecords = rawSubrecords,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    /// <summary>
    ///     Parses the Skyrim NVNM "Geometry" header (per xEdit <c>wbNVNM</c>): u32 Version, u32 CRC Hash,
    ///     formid Parent World, then a 4-byte union — interior (Parent World == 0) is the Parent Cell
    ///     formid; exterior is {int16 Grid Y, int16 Grid X} — then the u32 vertex count, the vertex array,
    ///     and the u32 triangle count. Returns null when the blob is too short.
    /// </summary>
    private static (uint CellFormId, uint WorldspaceFormId, int? GridX, int? GridY, uint VertexCount, uint TriangleCount)?
        ParseNvnmHeader(ReadOnlySpan<byte> nvnm, bool bigEndian)
    {
        if (nvnm.Length < 20)
        {
            return null;
        }

        var parentWorld = ReadU32(nvnm, 8, bigEndian);
        uint cellFormId = 0, worldspaceFormId = 0;
        int? gridX = null, gridY = null;
        if (parentWorld != 0)
        {
            worldspaceFormId = parentWorld;
            gridY = ReadI16(nvnm, 12, bigEndian);
            gridX = ReadI16(nvnm, 14, bigEndian);
        }
        else
        {
            cellFormId = ReadU32(nvnm, 12, bigEndian); // interior: union holds the Parent Cell formid
        }

        var vertexCount = ReadU32(nvnm, 16, bigEndian);
        // The triangle count sits right after the vertex array (12 bytes per vertex).
        var triOffset = 20L + ((long)vertexCount * 12);
        var triangleCount = triOffset + 4 <= nvnm.Length ? ReadU32(nvnm, (int)triOffset, bigEndian) : 0u;

        return (cellFormId, worldspaceFormId, gridX, gridY, vertexCount, triangleCount);
    }

    private static uint ReadU32(ReadOnlySpan<byte> s, int offset, bool bigEndian) => bigEndian
        ? BinaryPrimitives.ReadUInt32BigEndian(s.Slice(offset, 4))
        : BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(offset, 4));

    private static short ReadI16(ReadOnlySpan<byte> s, int offset, bool bigEndian) => bigEndian
        ? BinaryPrimitives.ReadInt16BigEndian(s.Slice(offset, 2))
        : BinaryPrimitives.ReadInt16LittleEndian(s.Slice(offset, 2));

    /// <summary>
    ///     Capture one subrecord into the NavMeshSubrecord list, applying the existing
    ///     Xbox-360→PC endian conversion via the schema-driven subrecord converter when
    ///     the source record was detected as big-endian. PC-endian sources pass through
    ///     verbatim.
    /// </summary>
    private static void CaptureNavmSubrecord(
        DetectedMainRecord record,
        string signature,
        ReadOnlySpan<byte> subData,
        List<NavMeshSubrecord> outList)
    {
        if (subData.Length == 0)
        {
            outList.Add(new NavMeshSubrecord(signature, []));
            return;
        }

        if (record.IsBigEndian)
        {
            try
            {
                var converted = EsmSubrecordConverter.ConvertSubrecordData(signature, subData, "NAVM");
                outList.Add(new NavMeshSubrecord(signature, converted));
                return;
            }
            catch (NotSupportedException)
            {
                // No schema → fall through to passthrough; the engine may still accept the
                // bytes since most NAVM subrecord variants are byte-stream blobs.
            }
        }

        outList.Add(new NavMeshSubrecord(signature, subData.ToArray()));
    }

    /// <summary>
    ///     Parse the single NavMesh Info Map (NAVI) record per ESM.
    /// </summary>
    internal List<NavMeshInfoMapRecord> ParseNavMeshInfoMaps()
    {
        var maps = ParseAccessorOnly("NAVI", 8192, ParseNavMeshInfoMapFromAccessor);

        Context.MergeRuntimeRecords(maps, 0x38, n => n.FormId,
            (reader, entry) => reader.ReadRuntimeNavMeshInfoMap(entry), "navmesh info maps");

        return maps;
    }

    private NavMeshInfoMapRecord? ParseNavMeshInfoMapFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return null;
        }

        var (data, dataSize) = recordData.Value;
        string? editorId = null;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            if (sub.Signature == "EDID")
            {
                editorId = EsmStringUtils.ReadNullTermString(data.AsSpan(sub.DataOffset, sub.DataLength));
                if (!string.IsNullOrEmpty(editorId))
                {
                    Context.FormIdToEditorId[record.FormId] = editorId;
                }
            }
        }

        return new NavMeshInfoMapRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }
}
