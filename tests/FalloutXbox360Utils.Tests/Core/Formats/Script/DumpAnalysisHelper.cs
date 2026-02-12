using System.Collections.Concurrent;
using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Minidump;

namespace FalloutXbox360Utils.Tests.Core.Formats.Script;

/// <summary>
///     Helper for analyzing memory dumps in test fixtures.
///     Caches results per path so the expensive analysis runs at most once per dump file.
///     Uses Task.Run to offload all work to the thread pool, bypassing xUnit's sync context.
/// </summary>
internal static class DumpAnalysisHelper
{
    private static readonly ConcurrentDictionary<string, Lazy<Task<List<ScriptRecord>?>>> Cache = new();

    internal static Task<List<ScriptRecord>?> AnalyzeDumpAsync(string relativePath)
    {
        var path = ScriptTestHelpers.FindSamplePath(relativePath);
        if (path == null)
        {
            return Task.FromResult<List<ScriptRecord>?>(null);
        }

        var lazy = Cache.GetOrAdd(path, p => new Lazy<Task<List<ScriptRecord>?>>(() => Task.Run(() => RunAnalysis(p))));
        return lazy.Value;
    }

    private static async Task<List<ScriptRecord>?> RunAnalysis(string path)
    {
        // Ensure no SynchronizationContext leaks from xUnit into Progress<T> callbacks
        SynchronizationContext.SetSynchronizationContext(null);

        try
        {
            var analyzer = new MinidumpAnalyzer();
            var analysisResult = await analyzer.AnalyzeAsync(path, includeMetadata: true)
                .ConfigureAwait(false);

            if (analysisResult.EsmRecords == null)
            {
                return null;
            }

            var fileInfo = new FileInfo(path);
            using var mmf = MemoryMappedFile.CreateFromFile(
                path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            using var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);

            var reconstructor = new RecordParser(
                analysisResult.EsmRecords,
                analysisResult.FormIdMap,
                accessor,
                fileInfo.Length,
                analysisResult.MinidumpInfo);

            var semanticResult = reconstructor.ReconstructAll();
            return semanticResult.Scripts;
        }
        catch
        {
            return null;
        }
    }
}