using FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter.Cells;
using FalloutXbox360Utils.Core.Formats.Esm.Planner;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Cell;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Pipeline;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Reference;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Reporting;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Output;

internal sealed class EspAssembler(RecordEncoderRegistry encoderRegistry)
{
    /// <summary>
    ///     Concatenates TES4, emitted top-level GRUPs, and the cell hierarchy into final ESP bytes.
    ///     The optional <paramref name="emitPlan" /> activates the planner-owned cell pipeline
    ///     when <c>options.PlannerEnabledRecordTypes</c> contains <c>"CELL"</c>; otherwise the
    ///     legacy <see cref="CellGrupBuilder" /> runs against <paramref name="bundles" />.
    /// </summary>
    public byte[] Assemble(
        PluginBuildOptions options,
        long masterFileSize,
        ConversionPipelineStats stats,
        IReadOnlyDictionary<string, byte[]> grupBytesByType,
        IReadOnlyList<CellOverrideBundle> bundles,
        IReadOnlyDictionary<uint, ParsedMainRecord> pcRecordsByFormId,
        FormIdAllocator allocator,
        IReadOnlyDictionary<uint, NewWorldspaceEntry>? newWorldspacesByDmpFormId,
        EmitPlan? emitPlan = null)
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

        // Two-pass migration switch: when "CELL" is in PlannerEnabledRecordTypes the
        // planner owns the entire cell hierarchy (CELL / REFR / ACHR / ACRE / LAND /
        // NAVM / NAVI / WRLD-with-cells). Per-record opt-in is incoherent here — the
        // cell tree is structurally atomic — so a single sentinel ("CELL") activates
        // the whole pipeline.
        var cellSectionBytes = options.PlannerEnabledRecordTypes.Contains("CELL") && emitPlan is not null
            ? PlanCellSectionBuilder.BuildCellSection(emitPlan, pcRecordsByFormId, options)
            : CellGrupBuilder.BuildCellSection(bundles, pcRecordsByFormId, newWorldspacesByDmpFormId);

        var nextObjectId = allocator.HasAllocations ? allocator.NextObjectId : 0x800u;
        var tes4 = Tes4HeaderBuilder.Build(optionsForBuild, (uint)stats.RecordsEmitted, nextObjectId);

        using var stream = new MemoryStream();
        stream.Write(tes4);
        foreach (var grup in orderedGrups)
        {
            stream.Write(grup);
        }

        if (cellSectionBytes != null)
        {
            stream.Write(cellSectionBytes);
        }

        return stream.ToArray();
    }
}
