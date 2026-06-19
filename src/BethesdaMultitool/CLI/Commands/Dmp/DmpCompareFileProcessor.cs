using BethesdaMultitool.Core;
using BethesdaMultitool.Core.Formats.Esm.Export;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Minidump;
using BethesdaMultitool.Core.Semantic;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Commands.Dmp;

/// <summary>
///     Handles individual file processing for the cross-dump comparison command.
///     Methods are thin adapters over the shared semantic loading/source-set pipeline.
/// </summary>
internal static class DmpCompareFileProcessor
{
    internal static async
        Task<(string FilePath, RecordCollection Records, FormIdResolver Resolver, MinidumpInfo? Info)?>
        ProcessDumpAsync(string dmpFile, bool verbose, CancellationToken cancellationToken = default)
    {
        var source = await SemanticSourceSetBuilder.LoadSourceAsync(
            new SemanticSourceRequest
            {
                FilePath = dmpFile,
                FileType = AnalysisFileType.Minidump,
                IncludeMetadata = true,
                VerboseMinidumpAnalysis = verbose
            },
            cancellationToken: cancellationToken);

        return (source.FilePath, source.Records, source.Resolver, source.MinidumpInfo);
    }

    internal static async Task<(string FilePath, RecordCollection Records, FormIdResolver Resolver, MinidumpInfo? Info,
            EsmRecordScanResult? ScanResult)?>
        ProcessEsmAsync(string esmFile, CancellationToken cancellationToken)
    {
        var source = await SemanticSourceSetBuilder.LoadSourceAsync(
            new SemanticSourceRequest
            {
                FilePath = esmFile,
                FileType = AnalysisFileType.EsmFile
            },
            cancellationToken: cancellationToken);

        return (
            source.FilePath,
            source.Records,
            source.Resolver,
            source.MinidumpInfo,
            source.RawResult?.EsmRecords);
    }

    internal static async
        Task<(string FilePath, RecordCollection Records, FormIdResolver Resolver, MinidumpInfo? Info)?>
        ProcessBaseDirectoryAsync(string baseDirPath, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(baseDirPath))
        {
            AnsiConsole.MarkupLine($"[red]Base directory not found:[/] {baseDirPath}");
            return null;
        }

        var source = await SemanticSourceSetBuilder.LoadMergedBaseDirectoryAsync(
            baseDirPath,
            log: message => AnsiConsole.MarkupLine($"  [blue]{Markup.Escape(message)}[/]"),
            cancellationToken: cancellationToken);

        if (source == null)
        {
            AnsiConsole.MarkupLine($"[red]No .esm files found in base directory:[/] {baseDirPath}");
            return null;
        }

        return (source.FilePath, source.Records, source.Resolver, source.MinidumpInfo);
    }
}
