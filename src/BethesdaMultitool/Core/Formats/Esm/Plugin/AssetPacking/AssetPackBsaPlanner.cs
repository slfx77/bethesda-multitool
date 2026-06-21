using BethesdaMultitool.Core.Formats.Esm.Reporting;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;

/// <summary>
///     Plans how packed assets are split across one or more output BSA archives:
///     classifies each file into an asset bucket (main / textures / sounds / voices),
///     chunks oversized buckets so no archive crosses the signed 2 GB boundary, assigns
///     sidecar output paths, and deletes stale prior outputs that the new plan no longer
///     produces.
/// </summary>
internal static class AssetPackBsaPlanner
{
    public static IReadOnlyList<BsaOutputPlan> Plan(
        string outputBsaPath,
        IReadOnlyList<(string Path, byte[] Data)> packedFiles,
        long maxArchiveBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputBsaPath);

        if (packedFiles.Count == 0)
        {
            return [];
        }

        var bucketPlans = packedFiles
            .GroupBy(f => ClassifyAssetBucket(f.Path))
            .OrderBy(g => BucketSortOrder(g.Key))
            .SelectMany(g => ChunkBucket(g.Key, g, maxArchiveBytes))
            .ToList();

        var split = bucketPlans.Count > 1 || bucketPlans[0].ChunkIndex > 0;
        for (var i = 0; i < bucketPlans.Count; i++)
        {
            var plan = bucketPlans[i];
            var outputPath = split
                ? BuildSidecarPath(outputBsaPath, plan.Bucket, plan.ChunkIndex)
                : outputBsaPath;
            bucketPlans[i] = plan with { OutputPath = outputPath };
        }

        return bucketPlans;
    }

    private static IEnumerable<BsaOutputPlan> ChunkBucket(
        AssetPackBucket bucket,
        IEnumerable<(string Path, byte[] Data)> files,
        long maxArchiveBytes)
    {
        var ordered = files
            .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (EstimateBsaSize(ordered) <= maxArchiveBytes)
        {
            yield return new BsaOutputPlan(
                OutputPath: "",
                Bucket: bucket,
                BucketLabel: BucketLabel(bucket),
                ChunkIndex: 0,
                Files: ordered);
            yield break;
        }

        var chunk = new List<(string Path, byte[] Data)>();
        var chunkIndex = 0;
        var chunkEstimate = new BsaSizeEstimate();
        foreach (var file in ordered)
        {
            var candidateEstimate = chunkEstimate.EstimateWith(file);
            if (chunk.Count > 0 && candidateEstimate > maxArchiveBytes)
            {
                yield return new BsaOutputPlan(
                    OutputPath: "",
                    Bucket: bucket,
                    BucketLabel: BucketLabel(bucket),
                    ChunkIndex: chunkIndex++,
                    Files: chunk);
                chunk = [];
                chunkEstimate = new BsaSizeEstimate();
            }

            chunk.Add(file);
            chunkEstimate.Add(file);
        }

        if (chunk.Count > 0)
        {
            yield return new BsaOutputPlan(
                OutputPath: "",
                Bucket: bucket,
                BucketLabel: BucketLabel(bucket),
                ChunkIndex: chunkIndex,
                Files: chunk);
        }
    }

    private static long EstimateBsaSize(List<(string Path, byte[] Data)> files)
    {
        var unique = new Dictionary<(string Folder, string Name), byte[]>(files.Count);
        foreach (var (path, data) in files)
        {
            var normalized = path.Replace('/', '\\').TrimStart('\\').ToLowerInvariant();
            var slash = normalized.LastIndexOf('\\');
            var folder = slash >= 0 ? normalized[..slash] : "";
            var name = slash >= 0 ? normalized[(slash + 1)..] : normalized;
            unique.TryAdd((folder, name), data);
        }

        var folderCount = unique.Keys.Select(k => k.Folder).Distinct(StringComparer.Ordinal).Count();
        var fileCount = unique.Count;
        var totalFolderNameLength = unique.Keys
            .Select(k => k.Folder)
            .Distinct(StringComparer.Ordinal)
            .Sum(folder => folder.Length + 1);
        var totalFileNameLength = unique.Keys.Sum(k => k.Name.Length + 1);
        var dataLength = unique.Values.Sum(static data => (long)data.Length);

        return 36L
               + folderCount * 16L
               + folderCount
               + totalFolderNameLength
               + fileCount * 16L
               + totalFileNameLength
               + dataLength;
    }

    private sealed class BsaSizeEstimate
    {
        private readonly HashSet<(string Folder, string Name)> _files = [];
        private readonly HashSet<string> _folders = new(StringComparer.Ordinal);
        private long _total = 36;

        /// <summary>Returns the projected total BSA size if the given file were added, without adding it.</summary>
        public long EstimateWith((string Path, byte[] Data) file)
        {
            var key = Normalize(file.Path);
            if (_files.Contains(key))
            {
                return _total;
            }

            var estimate = _total + 16 + key.Name.Length + 1 + file.Data.Length;
            if (!_folders.Contains(key.Folder))
            {
                estimate += 16 + 1 + key.Folder.Length + 1;
            }

            return estimate;
        }

        /// <summary>Adds a file to the running BSA size estimate (ignores duplicates by path).</summary>
        public void Add((string Path, byte[] Data) file)
        {
            var key = Normalize(file.Path);
            if (!_files.Add(key))
            {
                return;
            }

            _total += 16 + key.Name.Length + 1 + file.Data.Length;
            if (_folders.Add(key.Folder))
            {
                _total += 16 + 1 + key.Folder.Length + 1;
            }
        }

        private static (string Folder, string Name) Normalize(string path)
        {
            var normalized = path.Replace('/', '\\').TrimStart('\\').ToLowerInvariant();
            var slash = normalized.LastIndexOf('\\');
            return slash >= 0
                ? (normalized[..slash], normalized[(slash + 1)..])
                : ("", normalized);
        }
    }

    private static AssetPackBucket ClassifyAssetBucket(string path)
    {
        var normalized = path.Replace('/', '\\').TrimStart('\\').ToLowerInvariant();
        if (normalized.StartsWith("textures\\", StringComparison.Ordinal))
        {
            return AssetPackBucket.Textures;
        }

        if (normalized.StartsWith("sound\\voice\\", StringComparison.Ordinal))
        {
            return AssetPackBucket.Voices;
        }

        if (normalized.StartsWith("sound\\", StringComparison.Ordinal))
        {
            return AssetPackBucket.Sounds;
        }

        return AssetPackBucket.Main;
    }

    private static int BucketSortOrder(AssetPackBucket bucket) => bucket switch
    {
        AssetPackBucket.Main => 0,
        AssetPackBucket.Textures => 1,
        AssetPackBucket.Sounds => 2,
        AssetPackBucket.Voices => 3,
        _ => 99
    };

    private static string BucketLabel(AssetPackBucket bucket) => bucket switch
    {
        AssetPackBucket.Main => "main",
        AssetPackBucket.Textures => "texture",
        AssetPackBucket.Sounds => "sound",
        AssetPackBucket.Voices => "voice",
        _ => "misc"
    };

    private static string BucketSuffix(AssetPackBucket bucket) => bucket switch
    {
        AssetPackBucket.Main => "Main",
        AssetPackBucket.Textures => "Textures",
        AssetPackBucket.Sounds => "Sounds",
        AssetPackBucket.Voices => "Voices",
        _ => "Assets"
    };

    private static string BuildSidecarPath(string outputBsaPath, AssetPackBucket bucket, int chunkIndex)
    {
        var directory = Path.GetDirectoryName(outputBsaPath);
        var stem = Path.GetFileNameWithoutExtension(outputBsaPath);
        var ext = Path.GetExtension(outputBsaPath);
        if (string.IsNullOrEmpty(ext))
        {
            ext = ".bsa";
        }

        var suffix = BucketSuffix(bucket);
        if (chunkIndex > 0)
        {
            suffix += chunkIndex + 1;
        }

        return Path.Combine(directory ?? "", $"{stem} - {suffix}{ext}");
    }

    public static void DeleteStaleBsaOutputs(
        string outputBsaPath,
        IReadOnlyList<BsaOutputPlan> outputPlans,
        IConversionProgressSink sink)
    {
        var planned = new HashSet<string>(
            outputPlans.Select(p => Path.GetFullPath(p.OutputPath)),
            StringComparer.OrdinalIgnoreCase);

        DeleteIfStale(outputBsaPath);

        var directory = Path.GetDirectoryName(outputBsaPath);
        var stem = Path.GetFileNameWithoutExtension(outputBsaPath);
        var ext = Path.GetExtension(outputBsaPath);
        if (string.IsNullOrEmpty(ext))
        {
            ext = ".bsa";
        }

        if (string.IsNullOrEmpty(directory))
        {
            directory = ".";
        }

        if (Directory.Exists(directory))
        {
            foreach (var sidecar in Directory.EnumerateFiles(directory, $"{stem} - *{ext}"))
            {
                DeleteIfStale(sidecar);
            }
        }

        void DeleteIfStale(string path)
        {
            var fullPath = Path.GetFullPath(path);
            if (planned.Contains(fullPath) || !File.Exists(fullPath))
            {
                return;
            }

            try
            {
                File.Delete(fullPath);
                sink.Info("AssetPacking", $"Deleted stale BSA output: {Path.GetFileName(fullPath)}");
            }
            catch (Exception ex)
            {
                sink.Warn("AssetPacking",
                    $"Could not delete stale BSA output {fullPath}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
