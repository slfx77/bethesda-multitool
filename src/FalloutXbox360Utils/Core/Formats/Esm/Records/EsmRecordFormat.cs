using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     ESM Record format handler for detecting and parsing Bethesda ESM records
///     from memory dumps. Supports both PC (little-endian) and Xbox 360 (big-endian) formats.
/// </summary>
public sealed class EsmRecordFormat : FileFormatBase, IDumpScanner
{
    public override string DisplayName => "ESM Records";
    public override string FormatId => "esmrecord";
    public override string Extension => ".esm";
    public override string OutputFolder => "esm_records";
    public override FileCategory Category => FileCategory.EsmData;
    public override int MinSize => 8;
    public override int MaxSize => 64 * 1024;
    public override bool ShowInFilterUI => false;
    public override IReadOnlyList<FormatSignature> Signatures { get; } = [];

    public override ParseResult? Parse(ReadOnlySpan<byte> data, int offset = 0)
    {
        return null;
    }

    public object ScanDump(MemoryMappedViewAccessor accessor, long fileSize)
    {
        return EsmRecordScanner.ScanForRecordsMemoryMapped(accessor, fileSize);
    }

    /// <summary>
    ///     Export ESM records to files. Delegates to EsmRecordExporter.
    /// </summary>
    public static Task ExportRecordsAsync(
        EsmRecordScanResult records,
        Dictionary<uint, string> formIdMap,
        string outputDir,
        List<CellRecord>? cells = null,
        List<WorldspaceRecord>? worldspaces = null)
    {
        return EsmRecordExporter.ExportRecordsAsync(records, formIdMap, outputDir, cells, worldspaces);
    }
}
