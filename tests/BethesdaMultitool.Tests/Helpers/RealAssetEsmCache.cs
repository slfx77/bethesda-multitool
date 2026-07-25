using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.Semantic;

namespace BethesdaMultitool.Tests.Helpers;

/// <summary>
///     Shared cache of full-master <see cref="SemanticFileLoader" /> results for real-asset
///     integration tests. Parsing a retail master is the most expensive operation in the suite
///     (25s–4min and multiple GB each); with every heavyweight class parsing its own copy in
///     parallel, the suite exhausts RAM and degenerates into GC thrash that never finishes.
///     <para>
///         Callers MUST run inside <see cref="SequentialIntegrationGroup" /> — sequential execution
///         is what makes the eviction policy safe (no test can hold an evicted result while another
///         test triggers eviction). Callers MUST NOT dispose the returned result; the cache owns it.
///     </para>
/// </summary>
internal static class RealAssetEsmCache
{
    /// <summary>
    ///     Budget on the summed SOURCE file sizes of cached entries (a cheap proxy for parse RAM,
    ///     which runs ~10x the file size). Small masters (TES3 75MB, FNV/Skyrim ~240MB) can share the
    ///     cache; a giant one (SeventySix.esm 2.2GB) evicts everything and lives alone. Room is made
    ///     BEFORE a miss parses — idle results must never sit under a multi-GB decode, which is what
    ///     OOM-killed the suite when a fixed 3-entry LRU held Oblivion+FNV+TES3 through the FO4 parse.
    /// </summary>
    private const long SourceByteBudget = 600L * 1024 * 1024;

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly List<(string Path, long SourceBytes, UnifiedAnalysisResult Result)> Entries = [];

    /// <summary>
    ///     Loads (or reuses) the semantic parse of <paramref name="path" />. Least-recently-used
    ///     entries are disposed until the incoming file fits <see cref="SourceByteBudget" />.
    /// </summary>
    public static async Task<UnifiedAnalysisResult> LoadAsync(string path, CancellationToken cancellationToken)
    {
        var key = Path.GetFullPath(path);
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var index = Entries.FindIndex(e => string.Equals(e.Path, key, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                var hit = Entries[index];
                Entries.RemoveAt(index);
                Entries.Add(hit); // most-recently-used last
                return hit.Result;
            }

            var incoming = new FileInfo(key).Length;
            var evictedAny = false;
            while (Entries.Count > 0 && Entries.Sum(e => e.SourceBytes) + incoming > SourceByteBudget)
            {
                var evicted = Entries[0];
                Entries.RemoveAt(0);
                evicted.Result.Dispose();
                evictedAny = true;
            }

            if (evictedAny)
            {
                // Return the evicted graphs to the OS before the next multi-GB decode peaks.
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }

            var result = await SemanticFileLoader.LoadAsync(key, cancellationToken: cancellationToken);
            Entries.Add((key, incoming, result));
            return result;
        }
        finally
        {
            Gate.Release();
        }
    }
}