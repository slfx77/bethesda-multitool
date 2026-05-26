using System.Runtime;
using FalloutXbox360Utils.Core.Formats.Esm.Export.Projections;
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

    /// <summary>
    ///     Non-streaming variant of the cross-dump pipeline: loads every source up front,
    ///     aggregates all record types in one pass, returns the full
    ///     <see cref="CrossDumpRecordIndex" /> in memory. Used by callers that need direct
    ///     access to the index (GUI batch mode, CLI dmp compare without --streaming).
    /// </summary>
    /// <remarks>
    ///     Internally projects each source the same way <see cref="WriteHtmlByRecordTypeAsync" />
    ///     does, so the per-source <c>RecordCollection</c> can be released as soon as projection
    ///     finishes. Peak managed heap drops from O(parsed records × sources) to
    ///     O(reports + skeletons × sources), matching the streaming pipeline's memory profile.
    /// </remarks>
    internal static async Task<CrossDumpComparisonResult> BuildAsync(
        CrossDumpComparisonRequest request,
        Action<string>? status = null,
        CancellationToken cancellationToken = default)
    {
        var projections = new List<CrossDumpSourceProjection>();
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
                if (CaptureHeightmapSource(baseSource) is { } baseHeightmap)
                {
                    heightmapSources.Add(baseHeightmap);
                }

                projections.Add(CrossDumpSourceProjector.Project(baseSource));
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

            projections.Add(CrossDumpSourceProjector.Project(source));
            ReleaseTransientRecordTypeMemory();
        }

        var sourceSummaries = projections.Select(p => new CrossDumpSourceSummary(
                p.FilePath,
                p.ReportsByType.TryGetValue("Weapon", out var weaponReports) ? weaponReports.Count : 0,
                p.NpcSkeletons.Count,
                p.CellSkeletons.Count,
                p.Resolver.SkillEra?.Summary))
            .ToList();

        var virtualCellCanonicalFormIds = VirtualCellCanonicalizer.BuildVirtualCellCanonicalFormIds(
            projections.Select(p => p.CellSkeletons));

        var npcPlacementIndexes = allowedTypes == null || allowedTypes.Contains("NPC")
            ? CrossDumpPlacementIndexBuilder.BuildNpcPlacementIndexes(projections, virtualCellCanonicalFormIds)
            : null;
        var npcScriptReferenceIndexes = allowedTypes == null || allowedTypes.Contains("NPC")
            ? CrossDumpPlacementIndexBuilder.BuildNpcScriptReferenceIndexes(projections)
            : null;
        var keyLockedDoorIndexes = allowedTypes == null || allowedTypes.Contains("Key")
            ? CrossDumpPlacementIndexBuilder.BuildKeyLockedDoorIndexes(projections, virtualCellCanonicalFormIds)
            : null;
        var containerPlacementIndexes = allowedTypes == null || allowedTypes.Contains("Container")
            ? CrossDumpPlacementIndexBuilder.BuildContainerPlacementIndexes(projections, virtualCellCanonicalFormIds)
            : null;

        status?.Invoke("Building cross-source NPC/Key/Container reports...");
        CrossDumpProjectionAggregator.BuildLatePassReports(
            projections,
            npcPlacementIndexes,
            npcScriptReferenceIndexes,
            keyLockedDoorIndexes,
            containerPlacementIndexes);

        var index = CrossDumpProjectionAggregator.AggregateFromProjections(
            projections, virtualCellCanonicalFormIds, allowedTypes);

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

    /// <summary>
    ///     Stream the comparison HTML pipeline using projection-based loading. Each
    ///     <see cref="SemanticSource" /> is projected to a lightweight
    ///     <see cref="CrossDumpSourceProjection" /> immediately after load and the source
    ///     (with its heavy <c>RecordCollection</c>) is released. Cross-source indexes are
    ///     built once from skeletons; pass-B builds NPC/Key/Container reports; then the
    ///     per-record-type aggregator consumes the projections.
    /// </summary>
    internal static async Task<CrossDumpStreamingComparisonResult> WriteHtmlByRecordTypeAsync(
        CrossDumpComparisonRequest request,
        Action<string>? status = null,
        CancellationToken cancellationToken = default)
    {
        var projections = new List<CrossDumpSourceProjection>();
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
                if (CaptureHeightmapSource(baseSource) is { } baseHeightmap)
                {
                    heightmapSources.Add(baseHeightmap);
                }

                projections.Add(CrossDumpSourceProjector.Project(baseSource));
                hasBaseBuild = true;
                // baseSource falls out of scope; its RecordCollection is now eligible for GC
                // (the lightweight projection holds the reports + skeletons we still need).
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

            projections.Add(CrossDumpSourceProjector.Project(source));
            // source falls out of scope after this iteration; the heavy RecordCollection
            // is eligible for GC. Free transiently retained large-object-heap blocks.
            ReleaseTransientRecordTypeMemory();
        }

        var sourceSummaries = projections.Select(p => new CrossDumpSourceSummary(
                p.FilePath,
                p.ReportsByType.TryGetValue("Weapon", out var weaponReports) ? weaponReports.Count : 0,
                p.NpcSkeletons.Count,
                p.CellSkeletons.Count,
                p.Resolver.SkillEra?.Summary))
            .ToList();

        var recordTypes = DetermineRecordTypesFromProjections(projections, allowedTypes);
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

        // Cross-source indexes built once from skeletons. virtualCellCanonicalFormIds is
        // shared by every placement / locked-door / canonicalizer call below.
        var virtualCellCanonicalFormIds = VirtualCellCanonicalizer.BuildVirtualCellCanonicalFormIds(
            projections.Select(p => p.CellSkeletons));

        var npcPlacementIndexes = recordTypes.Contains("NPC", StringComparer.OrdinalIgnoreCase)
            ? CrossDumpPlacementIndexBuilder.BuildNpcPlacementIndexes(projections, virtualCellCanonicalFormIds)
            : null;
        var npcScriptReferenceIndexes = recordTypes.Contains("NPC", StringComparer.OrdinalIgnoreCase)
            ? CrossDumpPlacementIndexBuilder.BuildNpcScriptReferenceIndexes(projections)
            : null;
        var keyLockedDoorIndexes = recordTypes.Contains("Key", StringComparer.OrdinalIgnoreCase)
            ? CrossDumpPlacementIndexBuilder.BuildKeyLockedDoorIndexes(projections, virtualCellCanonicalFormIds)
            : null;
        var containerPlacementIndexes = recordTypes.Contains("Container", StringComparer.OrdinalIgnoreCase)
            ? CrossDumpPlacementIndexBuilder.BuildContainerPlacementIndexes(projections, virtualCellCanonicalFormIds)
            : null;

        // Pass B: fill in NPC/Key/Container reports on each projection using the cross-source
        // enrichment indexes that didn't exist at projection time.
        status?.Invoke("Building cross-source NPC/Key/Container reports...");
        CrossDumpProjectionAggregator.BuildLatePassReports(
            projections,
            npcPlacementIndexes,
            npcScriptReferenceIndexes,
            keyLockedDoorIndexes,
            containerPlacementIndexes);
        ReleaseTransientRecordTypeMemory();

        var writtenFiles = new List<string>();
        var summaries = new List<CrossDumpRecordTypeSummary>();
        List<DumpSnapshot>? dumps = null;

        for (var typeIndex = 0; typeIndex < recordTypes.Count; typeIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var recordType = recordTypes[typeIndex];
            status?.Invoke($"Writing {recordType} report...");

            var allowedRecordType = new HashSet<string>([recordType], StringComparer.OrdinalIgnoreCase);
            var index = CrossDumpProjectionAggregator.AggregateFromProjections(
                projections,
                virtualCellCanonicalFormIds,
                allowedRecordType);

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
            ReleaseTransientRecordTypeMemory();
        }

        dumps ??= [];
        var indexFile = await CrossDumpHtmlWriter.WriteIndexPageAsync(
            dumps,
            summaries,
            request.OutputPath,
            cancellationToken);
        writtenFiles.Add(indexFile);

        projections.Clear();
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

    private static List<string> DetermineRecordTypesFromProjections(
        IEnumerable<CrossDumpSourceProjection> projections,
        HashSet<string>? allowedTypes)
    {
        var recordTypes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var projection in projections)
        {
            foreach (var typeName in projection.ReportsByType.Keys)
            {
                if (allowedTypes is { Count: > 0 } && !allowedTypes.Contains(typeName))
                {
                    continue;
                }

                recordTypes.Add(typeName);
            }

            // Include NPC/Key/Container even if pass-B hasn't run yet (e.g., empty late
            // enrichment) so DetermineRecordTypes matches the legacy behavior of including
            // every type that has at least one record in some source.
            foreach (var (typeName, count) in new[]
                     {
                         ("NPC", projection.NpcSkeletons.Count),
                         ("Key", projection.KeySkeletons.Count),
                         ("Container", projection.ContainerSkeletons.Count)
                     })
            {
                if (count == 0)
                {
                    continue;
                }

                if (allowedTypes is { Count: > 0 } && !allowedTypes.Contains(typeName))
                {
                    continue;
                }

                recordTypes.Add(typeName);
            }
        }

        return recordTypes.ToList();
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
