using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Parsing;
using FalloutXbox360Utils.Core.Formats.Esm.Records;
using FalloutXbox360Utils.Core.Minidump;
using Spectre.Console;

namespace TerrainAnalyzer;

/// <summary>
///     Shared pipeline: DMP file -> enriched LandRecords with terrain data.
/// </summary>
internal sealed class DumpData(
    EsmRecordScanResult scanResult,
    MemoryMappedFile mmf,
    MemoryMappedViewAccessor accessor) : IDisposable
{
    public EsmRecordScanResult ScanResult { get; } = scanResult;
    public MemoryMappedFile Mmf { get; } = mmf;
    public MemoryMappedViewAccessor Accessor { get; } = accessor;

    /// <summary>
    ///     Pre-enrichment VHGT heightmaps, keyed by FormID.
    ///     Captured before ReconstructAll() overwrites them with ExactHeights.
    /// </summary>
    public Dictionary<uint, LandHeightmap> OriginalVhgtHeightmaps { get; init; } = [];

    public void Dispose()
    {
        Accessor.Dispose();
        Mmf.Dispose();
    }
}

internal static class DumpLoader
{
    internal static async Task<DumpData> LoadAsync(string dumpPath, bool preserveVhgt = false, bool verbose = false)
    {
        if (!File.Exists(dumpPath))
        {
            throw new FileNotFoundException($"Dump file not found: {dumpPath}");
        }

        if (verbose)
        {
            Logger.Instance.SetVerbose(true);
            Logger.Instance.IncludeTimestamp = true;
        }

        // Phase 1: Scan dump for ESM records
        var analyzer = new MinidumpAnalyzer();
        AnalysisResult result = null!;

        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("[green]Scanning dump[/]", maxValue: 100);
                var progress = new Progress<AnalysisProgress>(p =>
                {
                    task.Value = p.PercentComplete;
                    task.Description = $"[green]{p.Phase}[/]";
                });

                result = await analyzer.AnalyzeAsync(dumpPath, progress, verbose: verbose);
                task.Value = 100;
                task.Description = "[green]Scan complete[/]";
            });

        if (result.EsmRecords == null)
        {
            throw new InvalidOperationException("No ESM records found in dump file.");
        }

        // Phase 2: Semantic reconstruction (enriches LandRecords with terrain meshes)
        var fileSize = new FileInfo(dumpPath).Length;
        var mmf = MemoryMappedFile.CreateFromFile(dumpPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

        // Snapshot VHGT heightmaps before reconstruction overwrites them
        Dictionary<uint, LandHeightmap> originalVhgt = [];
        if (preserveVhgt)
        {
            foreach (var land in result.EsmRecords.LandRecords)
            {
                if (land.Heightmap != null)
                {
                    originalVhgt[land.Header.FormId] = land.Heightmap;
                }
            }
        }

        AnsiConsole.MarkupLine("[blue]Running semantic reconstruction...[/]");
        var reconstructor = new RecordParser(
            result.EsmRecords, result.FormIdMap, accessor, fileSize, result.MinidumpInfo);
        reconstructor.ReconstructAll();

        var landWithHeightmap = result.EsmRecords.LandRecords.Count(l => l.Heightmap != null);
        var landWithMesh = result.EsmRecords.LandRecords.Count(l => l.RuntimeTerrainMesh != null);
        AnsiConsole.MarkupLine(
            $"[green]Loaded:[/] {result.EsmRecords.LandRecords.Count} LAND records, " +
            $"{landWithHeightmap} with heightmaps, {landWithMesh} with runtime meshes");

        return new DumpData(result.EsmRecords, mmf, accessor) { OriginalVhgtHeightmaps = originalVhgt };
    }
}
