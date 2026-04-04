using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using System.Text.Json;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime;
using FalloutXbox360Utils.Core.Minidump;

namespace FalloutXbox360Utils.Tests.Helpers;

/// <summary>
///     One-time extraction tool that captures the sparse byte ranges a DMP test accesses.
///     Run once with real dump files present → produces a snippet file (.bin) + manifest (.json)
///     that <see cref="DmpSnippetReader" /> loads for fast tests.
/// </summary>
/// <remarks>
///     Usage: call <see cref="ExtractAsync" /> with a dump path and a test action that exercises
///     the production code paths. The extractor instruments the memory accessor, runs the action,
///     then saves only the accessed byte ranges.
/// </remarks>
internal static class DmpSnippetExtractor
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = false };

    /// <summary>
    ///     Extract a snippet from a real DMP file by running <paramref name="testAction" />.
    ///     Saves the snippet binary and JSON manifest to <paramref name="outputDir" />.
    /// </summary>
    /// <param name="dumpPath">Path to the real DMP file.</param>
    /// <param name="snippetName">Base filename for output (e.g., "debug_dump").</param>
    /// <param name="outputDir">Directory to write snippet files.</param>
    /// <param name="testAction">
    ///     Action to run against the instrumented reader. Receives the analysis result,
    ///     the recording accessor, and the file size.
    /// </param>
    public static async Task ExtractAsync(
        string dumpPath,
        string snippetName,
        string outputDir,
        Func<AnalysisResult, IMemoryAccessor, long, Task> testAction)
    {
        // Step 1: Run MinidumpAnalyzer to get metadata
        var analyzer = new MinidumpAnalyzer();
        var analysisResult = await analyzer.AnalyzeAsync(
            dumpPath, includeMetadata: true, cancellationToken: CancellationToken.None);

        if (analysisResult.EsmRecords == null || analysisResult.MinidumpInfo == null)
        {
            throw new InvalidOperationException($"Analysis of {dumpPath} produced no ESM records or minidump info");
        }

        // Step 2: Create instrumented accessor
        var fileInfo = new FileInfo(dumpPath);
        using var mmf = MemoryMappedFile.CreateFromFile(
            dumpPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);
        var recording = new RecordingMemoryAccessor(accessor);

        // Step 3: Run the test action with the recording accessor
        await testAction(analysisResult, recording, fileInfo.Length);

        // Step 4: Coalesce and save captured ranges
        var ranges = recording.GetCoalescedRanges(fileInfo.Length);
        if (ranges.Count == 0)
        {
            throw new InvalidOperationException("Test action did not read any data from the dump");
        }

        Directory.CreateDirectory(outputDir);

        // Save binary snippet (concatenated ranges with offset table)
        var binPath = Path.Combine(outputDir, $"{snippetName}.bin");
        await SaveSnippetBinaryAsync(binPath, ranges, accessor);

        // Save JSON manifest
        var manifest = new DmpSnippetManifest
        {
            SourceFileName = Path.GetFileName(dumpPath),
            SourceFileSize = fileInfo.Length,
            MinidumpInfo = SerializeMinidumpInfo(analysisResult.MinidumpInfo),
            RuntimeEditorIds = analysisResult.EsmRecords.RuntimeEditorIds,
            RuntimeRefrFormEntries = analysisResult.EsmRecords.RuntimeRefrFormEntries,
            FormIdMap = analysisResult.FormIdMap ?? new Dictionary<uint, string>(),
            Ranges = ranges.Select(r => new DmpSnippetRange { Offset = r.Offset, Length = r.Length }).ToList()
        };

        var jsonPath = Path.Combine(outputDir, $"{snippetName}.json.gz");
        await using var gzFs = File.Create(jsonPath);
        await using var gzStream = new GZipStream(gzFs, CompressionLevel.Optimal);
        await JsonSerializer.SerializeAsync(gzStream, manifest, s_jsonOptions);

        var totalCaptured = ranges.Sum(r => (long)r.Length);
        Console.WriteLine($"Snippet '{snippetName}': {ranges.Count} ranges, " +
                          $"{totalCaptured:N0} bytes captured from {fileInfo.Length:N0} byte dump " +
                          $"({100.0 * totalCaptured / fileInfo.Length:F2}%)");
    }

    private static async Task SaveSnippetBinaryAsync(
        string path, List<(long Offset, int Length)> ranges, MemoryMappedViewAccessor accessor)
    {
        await using var fs = File.Create(path);

        foreach (var (offset, length) in ranges)
        {
            var buf = new byte[length];
            accessor.ReadArray(offset, buf, 0, length);
            await fs.WriteAsync(buf);
        }
    }

    private static DmpSnippetMinidumpInfo SerializeMinidumpInfo(MinidumpInfo info)
    {
        return new DmpSnippetMinidumpInfo
        {
            IsValid = info.IsValid,
            ProcessorArchitecture = info.ProcessorArchitecture,
            Modules = info.Modules.Select(m => new DmpSnippetModule
            {
                Name = m.Name,
                BaseAddress = m.BaseAddress,
                Size = m.Size,
                Checksum = m.Checksum,
                TimeDateStamp = m.TimeDateStamp
            }).ToList(),
            MemoryRegions = info.MemoryRegions.Select(r => new DmpSnippetMemoryRegion
            {
                VirtualAddress = r.VirtualAddress,
                Size = r.Size,
                FileOffset = r.FileOffset
            }).ToList()
        };
    }
}