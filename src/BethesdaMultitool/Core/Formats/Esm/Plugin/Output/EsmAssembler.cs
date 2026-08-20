using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Cells;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;
using BethesdaMultitool.Core.Formats.Esm.Reporting;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Output;

internal sealed class EsmAssembler(RecordEncoderRegistry encoderRegistry)
{
    /// <summary>
    ///     Concatenates TES4, emitted top-level GRUPs, and the planner-built cell hierarchy
    ///     into final ESP bytes. The legacy <see cref="CellGrupBuilder" />-over-bundles branch
    ///     was removed in the 2026-08-11 retirement (Stage F); the cell tree is structurally
    ///     atomic and the planner owns all of it (CELL / REFR / ACHR / ACRE / PGRE / LAND /
    ///     NAVM / NAVI / WRLD-with-cells).
    /// </summary>
    public byte[] Assemble(
        PluginBuildOptions options,
        long masterFileSize,
        ConversionPipelineStats stats,
        IReadOnlyDictionary<string, byte[]> grupBytesByType,
        IReadOnlyDictionary<uint, ParsedMainRecord> pcRecordsByFormId,
        FormIdAllocator allocator,
        EmitPlan? emitPlan = null,
        MasterRecordIndex? masterRecordIndex = null,
        CellSectionBuildResult? prebuiltPlannerCellSection = null)
    {
        var optionsForBuild = options with { MasterFileSize = masterFileSize };

        var orderedGrups = new List<byte[]>();
        var emittedTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var recordType in encoderRegistry.SupportedRecordTypes)
        {
            if (RecordEncoderRegistry.IsCellChildRecordType(recordType)
                || RecordEncoderRegistry.IsCellRecordType(recordType))
            {
                continue;
            }

            if (grupBytesByType.TryGetValue(recordType, out var bytes))
            {
                orderedGrups.Add(bytes);
                emittedTypes.Add(recordType);
            }
        }

        // Synthesized top-level GRUPs whose record type isn't in the encoder registry (e.g.
        // NAVI override built directly by NavInfoMapBuilder + AppendOrCreateTopLevelRecord)
        // still need to be flushed to the output. Without this fallback they sit in
        // grupBytesByType and never reach disk. Sort alphabetically for deterministic output.
        foreach (var kvp in grupBytesByType.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (emittedTypes.Contains(kvp.Key)) continue;
            if (RecordEncoderRegistry.IsCellChildRecordType(kvp.Key)
                || RecordEncoderRegistry.IsCellRecordType(kvp.Key))
            {
                continue;
            }

            orderedGrups.Add(kvp.Value);
            emittedTypes.Add(kvp.Key);
        }

        // The planner section is normally prebuilt by PluginConversionPipeline (so NAVI rows can be
        // filtered to actually-written NAVMs before assembly); the fallback build here
        // serves direct callers/tests only. A plan-less call emits no cell hierarchy at
        // all — legitimate for the header-only fixtures that exercise TES4 assembly.
        var plannerSection = emitPlan is null
            ? prebuiltPlannerCellSection
            : prebuiltPlannerCellSection
              ?? PlanCellSectionBuilder.BuildCellSectionCore(
                  emitPlan, pcRecordsByFormId, options, stats, masterRecordIndex);
        var cellSectionBytes = plannerSection?.SectionBytes;

        // Body first, then census, then TES4. The HEDR record count and the run's emitted
        // stats are both derived from the bytes we actually produced rather than from
        // per-write-site counters, which drift whenever a later pass discards records
        // (cell gates) or an encoder declines an override.
        using var body = new MemoryStream();
        foreach (var grup in orderedGrups)
        {
            body.Write(grup);
        }

        if (cellSectionBytes != null)
        {
            body.Write(cellSectionBytes);
        }

        var bodyBytes = body.ToArray();
        var census = PluginEmissionCensus.Count(bodyBytes);
        census.ApplyTo(stats);

        var nextObjectId = allocator.HasAllocations ? allocator.NextObjectId : 0x800u;
        var tes4 = Tes4HeaderBuilder.Build(
            optionsForBuild, (uint)census.HedrRecordCount, nextObjectId,
            plannerSection?.OverriddenChildFormIds);

        using var stream = new MemoryStream(tes4.Length + bodyBytes.Length);
        stream.Write(tes4);
        stream.Write(bodyBytes);
        return stream.ToArray();
    }
}
