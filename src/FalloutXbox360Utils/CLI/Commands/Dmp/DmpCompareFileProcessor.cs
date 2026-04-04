using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Parsing;
using FalloutXbox360Utils.Core.Formats.Esm.Records;
using FalloutXbox360Utils.Core.Minidump;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Commands.Dmp;

/// <summary>
///     Handles individual file processing for the cross-dump comparison command.
///     Each method processes a single source (DMP file, ESM file, or base directory)
///     and returns parsed records with a resolver.
/// </summary>
internal static class DmpCompareFileProcessor
{
    /// <summary>
    ///     Process a single DMP file: analyze -> parse -> return records + resolver.
    /// </summary>
    internal static async Task<(string FilePath, RecordCollection Records, FormIdResolver Resolver, MinidumpInfo? Info)?>
        ProcessDumpAsync(string dmpFile, bool verbose)
    {
        var analyzer = new MinidumpAnalyzer();
        var result = await analyzer.AnalyzeAsync(dmpFile, includeMetadata: true, verbose: verbose);

        if (result.EsmRecords == null || result.EsmRecords.MainRecords.Count == 0)
            return null;

        var fileSize = new FileInfo(dmpFile).Length;
        using var mmf = MemoryMappedFile.CreateFromFile(dmpFile, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

        var parser = new RecordParser(
            result.EsmRecords, result.FormIdMap, accessor, fileSize, result.MinidumpInfo);
        var records = parser.ParseAll();

        var resolver = records.CreateResolver(result.FormIdMap);

        return (dmpFile, records, resolver, result.MinidumpInfo);
    }

    /// <summary>
    ///     Process a single ESM file: analyze -> parse -> return records + resolver + scan result.
    /// </summary>
    internal static async Task<(string FilePath, RecordCollection Records, FormIdResolver Resolver, MinidumpInfo? Info,
            EsmRecordScanResult? ScanResult)?>
        ProcessEsmAsync(string esmFile, CancellationToken ct)
    {
        var analysisResult = await EsmFileAnalyzer.AnalyzeAsync(esmFile, null, ct);

        if (analysisResult.EsmRecords == null || analysisResult.EsmRecords.MainRecords.Count == 0)
            return null;

        var fileSize = new FileInfo(esmFile).Length;
        using var mmf = MemoryMappedFile.CreateFromFile(esmFile, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileSize, MemoryMappedFileAccess.Read);

        var parser = new RecordParser(
            analysisResult.EsmRecords, analysisResult.FormIdMap, accessor, fileSize, analysisResult.MinidumpInfo);
        var records = parser.ParseAll();

        var resolver = records.CreateResolver(analysisResult.FormIdMap);

        return (esmFile, records, resolver, null, analysisResult.EsmRecords);
    }

    /// <summary>
    ///     Process a base build directory: auto-detect load order from MAST subrecords,
    ///     parse and merge all ESMs in dependency order (master first, DLCs overlay).
    /// </summary>
    internal static async Task<(string FilePath, RecordCollection Records, FormIdResolver Resolver, MinidumpInfo? Info)?>
        ProcessBaseDirectoryAsync(string baseDirPath, CancellationToken ct)
    {
        if (!Directory.Exists(baseDirPath))
        {
            AnsiConsole.MarkupLine($"[red]Base directory not found:[/] {baseDirPath}");
            return null;
        }

        var esmFiles = Directory.GetFiles(baseDirPath, "*.esm").ToList();
        if (esmFiles.Count == 0)
        {
            AnsiConsole.MarkupLine($"[red]No .esm files found in base directory:[/] {baseDirPath}");
            return null;
        }

        // Read headers to determine load order from MAST subrecords
        var fileHeaders = new List<(string Path, string FileName, EsmFileHeader Header)>();
        foreach (var esmFile in esmFiles)
        {
            var headerBytes = new byte[Math.Min(8192, new FileInfo(esmFile).Length)];
            await using var fs = File.OpenRead(esmFile);
            var bytesRead = await fs.ReadAsync(headerBytes, ct);
            var header = EsmParser.ParseFileHeader(headerBytes.AsSpan(0, bytesRead));
            if (header != null)
            {
                fileHeaders.Add((esmFile, System.IO.Path.GetFileName(esmFile), header));
            }
        }

        // Sort: files with no masters first (the base game ESM), then DLCs
        var ordered = fileHeaders
            .OrderBy(f => f.Header.Masters.Count)
            .ThenBy(f => f.FileName)
            .ToList();

        var masterName = System.IO.Path.GetFileNameWithoutExtension(ordered[0].FileName);
        AnsiConsole.MarkupLine($"  [blue]Base build: {Markup.Escape(masterName)}[/] ({ordered.Count} ESMs)");

        foreach (var (_, fileName, header) in ordered)
        {
            var mastersStr = header.Masters.Count > 0
                ? $" (masters: {string.Join(", ", header.Masters)})"
                : " (master)";
            AnsiConsole.MarkupLine($"    {Markup.Escape(fileName)}{mastersStr}");
        }

        // Parse and merge in order
        RecordCollection? merged = null;
        FormIdResolver? mergedResolver = null;

        foreach (var (path, _, _) in ordered)
        {
            var analysisResult = await EsmFileAnalyzer.AnalyzeAsync(path, null, ct);
            if (analysisResult.EsmRecords == null || analysisResult.EsmRecords.MainRecords.Count == 0)
                continue;

            var fileSize = new FileInfo(path).Length;
            using var mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0,
                MemoryMappedFileAccess.Read);
            using var accessor = mmf.CreateViewAccessor(0, fileSize, MemoryMappedFileAccess.Read);

            var parser = new RecordParser(
                analysisResult.EsmRecords, analysisResult.FormIdMap, accessor, fileSize,
                analysisResult.MinidumpInfo);
            var records = parser.ParseAll();
            var resolver = records.CreateResolver(analysisResult.FormIdMap);

            if (merged == null)
            {
                merged = records;
                mergedResolver = resolver;
            }
            else
            {
                merged = merged.MergeWith(records);
                mergedResolver = mergedResolver!.MergeWith(resolver);
            }
        }

        if (merged == null || mergedResolver == null)
            return null;

        // Use a non-existent path so FileInfo.Exists returns false -> DateTime.MinValue -> sorts first.
        // The filename portion is used as the display name.
        return (System.IO.Path.Combine(baseDirPath, $"{masterName}.base"), merged, mergedResolver, null);
    }
}
