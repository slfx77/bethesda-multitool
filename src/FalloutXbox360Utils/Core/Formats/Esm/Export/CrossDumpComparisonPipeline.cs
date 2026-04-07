using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Records;
using FalloutXbox360Utils.Core.Semantic;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

internal static class CrossDumpComparisonPipeline
{
    internal static async Task<CrossDumpComparisonResult> BuildAsync(
        CrossDumpComparisonRequest request,
        Action<string>? status = null,
        CancellationToken cancellationToken = default)
    {
        var sources = new List<SemanticSource>();
        var hasBaseBuild = false;

        if (!string.IsNullOrWhiteSpace(request.BaseDirectoryPath))
        {
            var baseSource = await SemanticSourceSetBuilder.LoadMergedBaseDirectoryAsync(
                request.BaseDirectoryPath!,
                status,
                cancellationToken);
            if (baseSource != null)
            {
                sources.Add(baseSource);
                hasBaseBuild = true;
            }
        }

        foreach (var filePath in request.SourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileType = SemanticFileLoader.ResolveSemanticFileType(filePath);
            status?.Invoke($"Loading {Path.GetFileName(filePath)}...");

            sources.Add(await SemanticSourceSetBuilder.LoadSourceAsync(
                new SemanticSourceRequest
                {
                    FilePath = filePath,
                    FileType = fileType,
                    IncludeMetadata = fileType == AnalysisFileType.Minidump,
                    VerboseMinidumpAnalysis = request.Verbose
                },
                cancellationToken: cancellationToken));
        }

        var index = CrossDumpAggregator.Aggregate(
            sources.Select(source => (source.FilePath, source.Records, source.Resolver, source.MinidumpInfo)).ToList());

        AppendHeightmaps(index, sources);
        ApplyBaseBuildNormalization(index, hasBaseBuild);
        ApplyTypeFilter(index, request.TypeFilter);

        return new CrossDumpComparisonResult
        {
            Index = index,
            Sources = sources,
            HasBaseBuild = hasBaseBuild
        };
    }

    private static void AppendHeightmaps(CrossDumpRecordIndex index, IEnumerable<SemanticSource> sources)
    {
        foreach (var source in sources)
        {
            if (source.RawResult?.EsmRecords is not { } scanResult)
            {
                continue;
            }

            foreach (var land in scanResult.LandRecords)
            {
                if (land.Heightmap == null || !land.BestCellX.HasValue || !land.BestCellY.HasValue)
                {
                    continue;
                }

                var worldspaceGroup = ResolveWorldspaceGroup(scanResult, source.Resolver, land);
                if (!index.CellHeightmaps.TryGetValue(worldspaceGroup, out var worldspaceHeightmaps))
                {
                    worldspaceHeightmaps = new Dictionary<(int X, int Y), LandHeightmap>();
                    index.CellHeightmaps[worldspaceGroup] = worldspaceHeightmaps;
                }

                worldspaceHeightmaps[(land.BestCellX.Value, land.BestCellY.Value)] = land.Heightmap;
            }
        }
    }

    private static string ResolveWorldspaceGroup(
        EsmRecordScanResult scanResult,
        FormIdResolver resolver,
        ExtractedLandRecord land)
    {
        var worldspaceGroup = "WastelandNV";
        if (scanResult.LandToWorldspaceMap.TryGetValue(land.Header.FormId, out var worldspaceFormId) &&
            worldspaceFormId != 0)
        {
            var worldspaceName = resolver.ResolveEditorId(worldspaceFormId);
            if (!string.IsNullOrEmpty(worldspaceName))
            {
                worldspaceGroup = worldspaceName;
            }
        }

        return worldspaceGroup;
    }

    private static void ApplyBaseBuildNormalization(CrossDumpRecordIndex index, bool hasBaseBuild)
    {
        if (!hasBaseBuild || index.Dumps.Count == 0)
        {
            return;
        }

        index.Dumps[0] = index.Dumps[0] with { IsBase = true };

        foreach (var (_, formIdMap) in index.StructuredRecords)
        {
            var editorIdToFormId = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            foreach (var (formId, dumpMap) in formIdMap)
            {
                foreach (var (dumpIndex, report) in dumpMap)
                {
                    if (dumpIndex != 0 && !string.IsNullOrEmpty(report.EditorId))
                    {
                        editorIdToFormId.TryAdd(report.EditorId, formId);
                    }
                }
            }

            var toMerge = new List<(uint BaseFormId, uint TargetFormId)>();
            foreach (var (formId, dumpMap) in formIdMap)
            {
                if (!dumpMap.ContainsKey(0) || dumpMap.Count > 1)
                {
                    continue;
                }

                var editorId = dumpMap[0].EditorId;
                if (string.IsNullOrEmpty(editorId))
                {
                    continue;
                }

                if (editorIdToFormId.TryGetValue(editorId, out var targetFormId) && targetFormId != formId)
                {
                    toMerge.Add((formId, targetFormId));
                }
            }

            foreach (var (baseFormId, targetFormId) in toMerge)
            {
                if (!formIdMap.TryGetValue(baseFormId, out var baseDumpMap) ||
                    !formIdMap.TryGetValue(targetFormId, out var targetDumpMap) ||
                    !baseDumpMap.TryGetValue(0, out var baseEntry) ||
                    targetDumpMap.ContainsKey(0))
                {
                    continue;
                }

                targetDumpMap[0] = baseEntry;
                formIdMap.Remove(baseFormId);
            }

            var toRemove = formIdMap
                .Where(entry => entry.Value.Count == 1 && entry.Value.ContainsKey(0))
                .Select(entry => entry.Key)
                .ToList();

            foreach (var formId in toRemove)
            {
                formIdMap.Remove(formId);
            }
        }
    }

    private static void ApplyTypeFilter(CrossDumpRecordIndex index, string? typeFilter)
    {
        if (string.IsNullOrWhiteSpace(typeFilter))
        {
            return;
        }

        var allowedTypes = new HashSet<string>(
            typeFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);

        var keysToRemove = index.StructuredRecords.Keys
            .Where(key => !allowedTypes.Contains(key))
            .ToList();

        foreach (var key in keysToRemove)
        {
            index.StructuredRecords.Remove(key);
        }
    }
}
