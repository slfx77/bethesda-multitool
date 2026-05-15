using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Reporting;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.AssetPacking;

internal sealed record AssetRenameResult(
    bool Ran,
    int Considered,
    int Rewritten,
    int SkippedExact,
    int SkippedMissing,
    string? SkipReason = null);

/// <summary>
///     Coordinates the pre-encode asset rename pass. It owns folder validation, indexing,
///     resolver ordering, and progress logging so plugin conversion code does not need
///     to know how Data folders are searched.
/// </summary>
internal sealed class AssetRenameService(IConversionProgressSink sink)
{
    public AssetRenameResult Apply(RecordCollection records, PluginBuildOptions options, CancellationToken ct)
    {
        if (options.AssetRenameBaselineFolder is null
            || options.AssetRenameSecondaryFolders.Count == 0)
        {
            return new AssetRenameResult(false, 0, 0, 0, 0, "rename folders not configured");
        }

        if (!Directory.Exists(options.AssetRenameBaselineFolder))
        {
            sink.Warn("AssetRename",
                $"Baseline folder not found, skipping rename pass: {options.AssetRenameBaselineFolder}");
            return new AssetRenameResult(false, 0, 0, 0, 0, "baseline folder not found");
        }

        sink.Info("AssetRename", $"Indexing baseline: {options.AssetRenameBaselineFolder}");
        using var baseline = new DataFolderIndex(options.AssetRenameBaselineFolder, false);
        baseline.Build();

        var secondaryIndexes = new List<DataFolderIndex>();
        try
        {
            foreach (var secondary in options.AssetRenameSecondaryFolders)
            {
                ct.ThrowIfCancellationRequested();
                if (!Directory.Exists(secondary.Path))
                {
                    sink.Warn("AssetRename",
                        $"Secondary folder not found, skipping: {secondary.Path}");
                    continue;
                }

                sink.Info("AssetRename",
                    $"Indexing secondary: {secondary.Path} (Xbox360={secondary.IsXbox360Format})");
                var index = new DataFolderIndex(secondary.Path, secondary.IsXbox360Format);
                index.Build();
                secondaryIndexes.Add(index);
            }

            if (secondaryIndexes.Count == 0)
            {
                sink.Warn("AssetRename", "No valid secondary folders, skipping rename pass.");
                return new AssetRenameResult(false, 0, 0, 0, 0, "no valid secondary folders");
            }

            var resolver = new DataFolderResolver(
                baseline, secondaryIndexes, options.AssetRenameOverrideVanilla);
            var result = AssetPathRewriter.ApplyRewrites(records, resolver, sink);

            sink.Info("AssetRename",
                $"Asset rewrite pass considered {result.Considered:N0} paths, " +
                $"rewrote {result.Rewritten:N0}, exact/no-change {result.SkippedExact:N0}, " +
                $"missing {result.SkippedMissing:N0}.");

            return new AssetRenameResult(
                true,
                result.Considered,
                result.Rewritten,
                result.SkippedExact,
                result.SkippedMissing);
        }
        finally
        {
            foreach (var index in secondaryIndexes)
            {
                index.Dispose();
            }
        }
    }
}
