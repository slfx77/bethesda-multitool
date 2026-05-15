using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Reporting;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin;

internal sealed class EspAssembler(RecordEncoderRegistry encoderRegistry)
{
    /// <summary>
    ///     Concatenates TES4, emitted top-level GRUPs, and the cell hierarchy into final ESP bytes.
    /// </summary>
    public byte[] Assemble(
        PluginBuildOptions options,
        long masterFileSize,
        ConversionPipelineStats stats,
        IReadOnlyDictionary<string, byte[]> grupBytesByType,
        IReadOnlyList<CellOverrideBundle> bundles,
        IReadOnlyDictionary<uint, ParsedMainRecord> pcRecordsByFormId,
        FormIdAllocator allocator,
        IReadOnlyDictionary<uint, NewWorldspaceEntry>? newWorldspacesByDmpFormId)
    {
        var optionsForBuild = options with { MasterFileSize = masterFileSize };

        var orderedGrups = new List<byte[]>();
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
            }
        }

        var cellSectionBytes = CellGrupBuilder.BuildCellSection(
            bundles,
            pcRecordsByFormId,
            newWorldspacesByDmpFormId);

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
