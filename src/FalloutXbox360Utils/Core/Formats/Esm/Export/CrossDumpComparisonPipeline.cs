using System.Runtime;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Semantic;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

internal static class CrossDumpComparisonPipeline
{
    private static readonly string[] SupportedReportRecordTypes =
    [
        "Ammo",
        "Armor",
        "BaseEffect",
        "Cell",
        "Consumable",
        "Container",
        "Creature",
        "Dialogue",
        "DialogTopic",
        "Explosion",
        "Faction",
        "Key",
        "LeveledList",
        "MapMarker",
        "MiscItem",
        "Note",
        "NPC",
        "Perk",
        "Projectile",
        "Quest",
        "Race",
        "Recipe",
        "Script",
        "Spell",
        "Weapon",
        "WeaponMod",
        "Worldspace"
    ];

    private static readonly Dictionary<string, string[]> RecordTypeDependencies =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Ammo"] = ["Projectile", "Weapon"],
            ["Consumable"] = ["BaseEffect"],
            ["Container"] = ["Cell"],
            ["Dialogue"] = ["DialogTopic"],
            ["Faction"] = ["NPC", "Creature"],
            ["Key"] = ["Cell"],
            ["MapMarker"] = ["Cell"],
            ["Spell"] = ["BaseEffect"],
            ["WeaponMod"] = ["Weapon"]
        };

    internal static async Task<CrossDumpComparisonResult> BuildAsync(
        CrossDumpComparisonRequest request,
        Action<string>? status = null,
        CancellationToken cancellationToken = default)
    {
        var sources = new List<SemanticSource>();
        var heightmapSources = new List<HeightmapSource>();
        var hasBaseBuild = false;
        var allowedTypes = ParseTypeFilter(request.TypeFilter);

        if (!string.IsNullOrWhiteSpace(request.BaseDirectoryPath))
        {
            var baseSource = await SemanticSourceSetBuilder.LoadMergedBaseDirectoryAsync(
                request.BaseDirectoryPath!,
                request.Verbose,
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

            var source = await SemanticSourceSetBuilder.LoadSourceAsync(
                new SemanticSourceRequest
                {
                    FilePath = filePath,
                    FileType = fileType,
                    IncludeMetadata = fileType == AnalysisFileType.Minidump,
                    VerboseMinidumpAnalysis = request.Verbose,
                    VerboseEsmAnalysis = request.Verbose
                },
                cancellationToken: cancellationToken);

            if (CaptureHeightmapSource(source) is { } heightmapSource)
            {
                heightmapSources.Add(heightmapSource);
            }

            sources.Add(DropRawScanPayload(source));
        }

        var sourceSummaries = sources.Select(source => new CrossDumpSourceSummary(
                source.FilePath,
                source.Records.Weapons.Count,
                source.Records.Npcs.Count,
                source.Records.Cells.Count,
                source.Resolver.SkillEra?.Summary))
            .ToList();

        var npcPlacementIndexes = allowedTypes == null || allowedTypes.Contains("NPC")
            ? CrossDumpAggregator.BuildNpcPlacementIndexes(
                sources.Select(source => (source.FilePath, source.Records)))
            : null;
        var npcScriptReferenceIndexes = allowedTypes == null || allowedTypes.Contains("NPC")
            ? CrossDumpAggregator.BuildNpcScriptReferenceIndexes(
                sources.Select(source => (source.FilePath, source.Records)))
            : null;

        var aggregateInputs = sources
            .Select(source => (source.FilePath, source.Records, source.Resolver, source.MinidumpInfo))
            .ToList();
        sources.Clear();

        var index = CrossDumpAggregator.Aggregate(
            aggregateInputs,
            allowedTypes,
            true,
            npcPlacementIndexes,
            npcScriptReferenceIndexes);
        aggregateInputs.Clear();

        AppendHeightmaps(index, heightmapSources);
        ApplyBaseBuildNormalization(index, hasBaseBuild);
        ApplyTypeFilter(index, allowedTypes);

        return new CrossDumpComparisonResult
        {
            Index = index,
            SourceSummaries = sourceSummaries,
            HasBaseBuild = hasBaseBuild
        };
    }

    internal static async Task<CrossDumpStreamingComparisonResult> WriteHtmlByRecordTypeAsync(
        CrossDumpComparisonRequest request,
        Action<string>? status = null,
        CancellationToken cancellationToken = default)
    {
        var sources = new List<SemanticSource>();
        var heightmapSources = new List<HeightmapSource>();
        var hasBaseBuild = false;
        var allowedTypes = ParseTypeFilter(request.TypeFilter);

        if (!string.IsNullOrWhiteSpace(request.BaseDirectoryPath))
        {
            var baseSource = await SemanticSourceSetBuilder.LoadMergedBaseDirectoryAsync(
                request.BaseDirectoryPath!,
                request.Verbose,
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

            var source = await SemanticSourceSetBuilder.LoadSourceAsync(
                new SemanticSourceRequest
                {
                    FilePath = filePath,
                    FileType = fileType,
                    IncludeMetadata = fileType == AnalysisFileType.Minidump,
                    VerboseMinidumpAnalysis = request.Verbose,
                    VerboseEsmAnalysis = request.Verbose
                },
                cancellationToken: cancellationToken);

            if (CaptureHeightmapSource(source) is { } heightmapSource)
            {
                heightmapSources.Add(heightmapSource);
            }

            sources.Add(DropRawScanPayload(source));
        }

        var sourceSummaries = sources.Select(source => new CrossDumpSourceSummary(
                source.FilePath,
                source.Records.Weapons.Count,
                source.Records.Npcs.Count,
                source.Records.Cells.Count,
                source.Resolver.SkillEra?.Summary))
            .ToList();

        var recordTypes = DetermineRecordTypes(sources, allowedTypes);
        if (recordTypes.Count == 0)
        {
            return new CrossDumpStreamingComparisonResult
            {
                Dumps = [],
                RecordTypes = [],
                WrittenFiles = [],
                SourceSummaries = sourceSummaries,
                HasBaseBuild = hasBaseBuild
            };
        }

        var npcPlacementIndexes = recordTypes.Contains("NPC", StringComparer.OrdinalIgnoreCase)
            ? CrossDumpAggregator.BuildNpcPlacementIndexes(
                sources.Select(source => (source.FilePath, source.Records)))
            : null;
        var npcScriptReferenceIndexes = recordTypes.Contains("NPC", StringComparer.OrdinalIgnoreCase)
            ? CrossDumpAggregator.BuildNpcScriptReferenceIndexes(
                sources.Select(source => (source.FilePath, source.Records)))
            : null;

        var writtenFiles = new List<string>();
        var summaries = new List<CrossDumpRecordTypeSummary>();
        List<DumpSnapshot>? dumps = null;

        ReleaseRecordListsNotNeededForRemainingTypes(sources, recordTypes);

        for (var typeIndex = 0; typeIndex < recordTypes.Count; typeIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var recordType = recordTypes[typeIndex];
            status?.Invoke($"Writing {recordType} report...");

            var aggregateInputs = sources
                .Select(source => (source.FilePath, source.Records, source.Resolver, source.MinidumpInfo))
                .ToList();
            var allowedRecordType = new HashSet<string>([recordType], StringComparer.OrdinalIgnoreCase);
            var index = CrossDumpAggregator.Aggregate(
                aggregateInputs,
                allowedRecordType,
                npcPlacementIndexes: npcPlacementIndexes,
                npcScriptReferenceIndexes: npcScriptReferenceIndexes);
            aggregateInputs.Clear();

            if (string.Equals(recordType, "Cell", StringComparison.OrdinalIgnoreCase))
            {
                AppendHeightmaps(index, heightmapSources);
            }

            ApplyBaseBuildNormalization(index, hasBaseBuild);

            if (index.StructuredRecords.TryGetValue(recordType, out var formIdMap) &&
                formIdMap.Count > 0)
            {
                dumps ??= index.Dumps.ToList();
                summaries.Add(CrossDumpJsonHtmlWriter.BuildRecordTypeSummary(
                    recordType,
                    formIdMap,
                    index.Dumps.Count));

                if (await CrossDumpHtmlWriter.WriteRecordTypeFileAsync(
                        index,
                        recordType,
                        request.OutputPath,
                        cancellationToken) is { } outputFile)
                {
                    writtenFiles.Add(outputFile);
                }
            }

            ClearIndexPayload(index);

            var remainingTypes = recordTypes.Skip(typeIndex + 1).ToList();
            ReleaseRecordListsNotNeededForRemainingTypes(sources, remainingTypes);
            ReleaseTransientRecordTypeMemory();
        }

        dumps ??= [];
        var indexFile = await CrossDumpHtmlWriter.WriteIndexPageAsync(
            dumps,
            summaries,
            request.OutputPath,
            cancellationToken);
        writtenFiles.Add(indexFile);

        sources.Clear();
        heightmapSources.Clear();
        ReleaseTransientRecordTypeMemory();

        return new CrossDumpStreamingComparisonResult
        {
            Dumps = dumps,
            RecordTypes = summaries,
            WrittenFiles = writtenFiles,
            SourceSummaries = sourceSummaries,
            HasBaseBuild = hasBaseBuild
        };
    }

    private static HashSet<string>? ParseTypeFilter(string? typeFilter)
    {
        if (string.IsNullOrWhiteSpace(typeFilter))
        {
            return null;
        }

        return new HashSet<string>(
            typeFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);
    }

    private static HeightmapSource? CaptureHeightmapSource(SemanticSource source)
    {
        if (source.RawResult?.EsmRecords is not { } scanResult)
        {
            return null;
        }

        var landEntries = scanResult.LandRecords
            .Where(land => land.Heightmap != null && land.BestCellX.HasValue && land.BestCellY.HasValue)
            .Select(land => new HeightmapEntry(
                land.Header.FormId,
                land.Heightmap!,
                land.BestCellX!.Value,
                land.BestCellY!.Value))
            .ToList();

        if (landEntries.Count == 0)
        {
            return null;
        }

        return new HeightmapSource(
            source.Resolver,
            new Dictionary<uint, uint>(scanResult.LandToWorldspaceMap),
            landEntries);
    }

    private static SemanticSource DropRawScanPayload(SemanticSource source)
    {
        return source.RawResult == null
            ? source
            : source with { RawResult = null };
    }

    private static void AppendHeightmaps(CrossDumpRecordIndex index, IEnumerable<HeightmapSource> sources)
    {
        foreach (var source in sources)
        {
            foreach (var land in source.LandRecords)
            {
                var worldspaceGroup = ResolveWorldspaceGroup(source, land);
                if (!index.CellHeightmaps.TryGetValue(worldspaceGroup, out var worldspaceHeightmaps))
                {
                    worldspaceHeightmaps = new Dictionary<(int X, int Y), LandHeightmap>();
                    index.CellHeightmaps[worldspaceGroup] = worldspaceHeightmaps;
                }

                worldspaceHeightmaps[(land.X, land.Y)] = land.Heightmap;
            }
        }
    }

    private static string ResolveWorldspaceGroup(
        HeightmapSource source,
        HeightmapEntry land)
    {
        var worldspaceGroup = "WastelandNV";
        if (source.LandToWorldspaceMap.TryGetValue(land.FormId, out var worldspaceFormId) &&
            worldspaceFormId != 0)
        {
            var worldspaceName = source.Resolver.ResolveEditorId(worldspaceFormId);
            if (!string.IsNullOrEmpty(worldspaceName))
            {
                worldspaceGroup = worldspaceName;
            }
        }

        return worldspaceGroup;
    }

    private static List<string> DetermineRecordTypes(
        IEnumerable<SemanticSource> sources,
        HashSet<string>? allowedTypes)
    {
        var recordTypes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            foreach (var (typeName, _, _, _, _) in RecordTextFormatter.EnumerateAll(source.Records))
            {
                if (allowedTypes is { Count: > 0 } && !allowedTypes.Contains(typeName))
                {
                    continue;
                }

                recordTypes.Add(typeName);
            }
        }

        return recordTypes.ToList();
    }

    private static void ReleaseRecordListsNotNeededForRemainingTypes(
        IReadOnlyList<SemanticSource> sources,
        IReadOnlyCollection<string> remainingRecordTypes)
    {
        var stillNeeded = new HashSet<string>(remainingRecordTypes, StringComparer.OrdinalIgnoreCase);
        foreach (var recordType in remainingRecordTypes)
        {
            if (!RecordTypeDependencies.TryGetValue(recordType, out var dependencies))
            {
                continue;
            }

            foreach (var dependency in dependencies)
            {
                stillNeeded.Add(dependency);
            }
        }

        foreach (var source in sources)
        {
            foreach (var recordType in SupportedReportRecordTypes)
            {
                if (!stillNeeded.Contains(recordType))
                {
                    ClearRecordListForType(source.Records, recordType);
                }
            }
        }
    }

    private static void ClearRecordListForType(RecordCollection records, string recordType)
    {
        switch (recordType)
        {
            case "Ammo":
                records.Ammo.Clear();
                break;
            case "Armor":
                records.Armor.Clear();
                break;
            case "BaseEffect":
                records.BaseEffects.Clear();
                break;
            case "Cell":
                records.Cells.Clear();
                records.RuntimeWorldspaceMaps.Clear();
                break;
            case "Consumable":
                records.Consumables.Clear();
                break;
            case "Container":
                records.Containers.Clear();
                break;
            case "Creature":
                records.Creatures.Clear();
                break;
            case "Dialogue":
                records.Dialogues.Clear();
                break;
            case "DialogTopic":
                records.DialogTopics.Clear();
                break;
            case "Explosion":
                records.Explosions.Clear();
                break;
            case "Faction":
                records.Factions.Clear();
                break;
            case "Key":
                records.Keys.Clear();
                break;
            case "LeveledList":
                records.LeveledLists.Clear();
                break;
            case "MapMarker":
                records.MapMarkers.Clear();
                break;
            case "MiscItem":
                records.MiscItems.Clear();
                break;
            case "Note":
                records.Notes.Clear();
                break;
            case "NPC":
                records.Npcs.Clear();
                break;
            case "Perk":
                records.Perks.Clear();
                break;
            case "Projectile":
                records.Projectiles.Clear();
                break;
            case "Quest":
                records.Quests.Clear();
                break;
            case "Race":
                records.Races.Clear();
                break;
            case "Recipe":
                records.Recipes.Clear();
                break;
            case "Script":
                records.Scripts.Clear();
                break;
            case "Spell":
                records.Spells.Clear();
                break;
            case "Weapon":
                records.Weapons.Clear();
                break;
            case "WeaponMod":
                records.WeaponMods.Clear();
                break;
            case "Worldspace":
                records.Worldspaces.Clear();
                break;
        }
    }

    private static void ClearIndexPayload(CrossDumpRecordIndex index)
    {
        index.StructuredRecords.Clear();
        index.RecordGroups.Clear();
        index.RecordMetadata.Clear();
        index.CellGridCoords.Clear();
        index.CellHeightmaps.Clear();
        index.EsmLandRecords.Clear();
        index.LandWorldspaceMap.Clear();
        index.Dumps.Clear();
    }

    private static void ReleaseTransientRecordTypeMemory()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
#pragma warning disable S1215
        // The streaming comparison path intentionally discards large per-type report payloads.
        // Compact between types so CELL/NPC chunks do not keep the next type's peak inflated.
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
#pragma warning restore S1215
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

    private static void ApplyTypeFilter(CrossDumpRecordIndex index, HashSet<string>? allowedTypes)
    {
        if (allowedTypes is not { Count: > 0 })
        {
            return;
        }

        var keysToRemove = index.StructuredRecords.Keys
            .Where(key => !allowedTypes.Contains(key))
            .ToList();

        foreach (var key in keysToRemove)
        {
            index.StructuredRecords.Remove(key);
        }
    }

    private sealed record HeightmapSource(
        FormIdResolver Resolver,
        IReadOnlyDictionary<uint, uint> LandToWorldspaceMap,
        IReadOnlyList<HeightmapEntry> LandRecords);

    private sealed record HeightmapEntry(uint FormId, LandHeightmap Heightmap, int X, int Y);
}
