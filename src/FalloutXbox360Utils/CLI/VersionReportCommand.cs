using System.CommandLine;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Utils;
using FalloutXbox360Utils.Core.VersionTracking.Caching;
using FalloutXbox360Utils.Core.VersionTracking.Extraction;
using FalloutXbox360Utils.Core.VersionTracking.Models;
using FalloutXbox360Utils.Core.VersionTracking.Processing;
using FalloutXbox360Utils.Core.VersionTracking.Reporting;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI;

/// <summary>
///     CLI command for the full version tracking pipeline:
///     discover, extract, coalesce, diff, and report.
/// </summary>
internal static class VersionReportCommand
{
    /// <summary>
    ///     Fields that commonly differ between FO3 PC ESM and FNV Xbox 360 DMP due to
    ///     platform/engine differences rather than actual content changes.
    /// </summary>
    private static readonly HashSet<string> Fo3InsignificantFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "BipedFlags", // FNV overhauled biped slot system from FO3
        "DR", // FNV replaced Damage Resistance with Damage Threshold
        "DT", // All FO3 armor got new DT values as part of DR→DT system change
        "Flags", // Engine flag bytes can differ between FO3 and FNV versions
        "InfoFlags", // Dialogue info flags differ between engine versions
        "SPECIAL", // Byte arrays may extract differently between PC ESM and Xbox 360 DMP
        "Skills" // Same extraction issue as SPECIAL
    };

    public static Command Create(string defaultBuildsDir, string defaultDumpsDir, string defaultCacheDir)
    {
        var command = new Command("report", "Run full pipeline: discover → extract → coalesce → diff → report");

        var buildsOpt = new Option<string>("--builds")
        {
            Description = "Path to full builds directory",
            DefaultValueFactory = _ => defaultBuildsDir
        };
        var dumpsOpt = new Option<string>("--dumps")
        {
            Description = "Path to memory dumps directory",
            DefaultValueFactory = _ => defaultDumpsDir
        };
        var outputOpt = new Option<string>("--output")
        {
            Description = "Output directory for reports",
            DefaultValueFactory = _ => "TestOutput/vtrack"
        };
        var formatOpt = new Option<string>("--format")
        {
            Description = "Output format: md, json, wiki, codeblock, both, or all",
            DefaultValueFactory = _ => "both"
        };
        var baselineOpt = new Option<string?>("--baseline")
        {
            Description = "Path to baseline ESM for wiki pages (default: auto-detect '360 Final')"
        };
        var cacheDirOpt = new Option<string>("--cache-dir")
        {
            Description = "Cache directory for snapshots",
            DefaultValueFactory = _ => defaultCacheDir
        };
        var forceOpt = new Option<bool>("--force") { Description = "Re-extract all (ignore cache)" };
        var typesOpt = new Option<string[]>("--types")
        {
            Description =
                "Filter categories: quest,npc,dialogue,weapon,armor,item,script,location,placement,creature,perk,ammo,leveledlist,note,terminal"
        };
        var formIdOpt = new Option<string?>("--formid")
            { Description = "Track specific FormID across all versions (hex, e.g. 0x001547A2)" };
        var fo3EsmOpt = new Option<string?>("--fo3-esm")
            { Description = "Path to Fallout 3 ESM directory for leftover filtering" };

        command.Options.Add(buildsOpt);
        command.Options.Add(dumpsOpt);
        command.Options.Add(outputOpt);
        command.Options.Add(formatOpt);
        command.Options.Add(baselineOpt);
        command.Options.Add(cacheDirOpt);
        command.Options.Add(forceOpt);
        command.Options.Add(typesOpt);
        command.Options.Add(formIdOpt);
        command.Options.Add(fo3EsmOpt);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var options = new ReportOptions
            {
                BuildsDir = parseResult.GetValue(buildsOpt)!,
                DumpsDir = parseResult.GetValue(dumpsOpt)!,
                OutputDir = parseResult.GetValue(outputOpt)!,
                Format = parseResult.GetValue(formatOpt)!,
                Baseline = parseResult.GetValue(baselineOpt),
                CacheDir = parseResult.GetValue(cacheDirOpt)!,
                Force = parseResult.GetValue(forceOpt),
                Types = parseResult.GetValue(typesOpt),
                FormId = parseResult.GetValue(formIdOpt),
                Fo3EsmDir = parseResult.GetValue(fo3EsmOpt)
            };

            await ExecuteAsync(options, cancellationToken);
        });

        return command;
    }

    private static async Task ExecuteAsync(ReportOptions opts, CancellationToken cancellationToken)
    {
        var totalStepsInit = opts.Fo3EsmDir != null ? 6 : 5;

        // Step 1: Discover builds
        AnsiConsole.MarkupLine($"[blue]Step 1/{totalStepsInit}:[/] Discovering builds...");
        var builds = BuildDiscovery.DiscoverBuilds(opts.BuildsDir, opts.DumpsDir);

        if (builds.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No builds found.");
            return;
        }

        AnsiConsole.MarkupLine($"  Found {builds.Count} sources");

        // Step 2: Extract snapshots
        AnsiConsole.MarkupLine($"[blue]Step 2/{totalStepsInit}:[/] Extracting snapshots...");
        var snapshots = await ExtractAllSnapshotsAsync(builds, opts, cancellationToken);

        if (snapshots.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No snapshots extracted successfully.");
            return;
        }

        // Step 3: Coalesce DMP snapshots
        AnsiConsole.MarkupLine($"[blue]Step 3/{totalStepsInit}:[/] Coalescing DMP snapshots...");
        var coalesced = SnapshotCoalescer.Coalesce(snapshots);
        AnsiConsole.MarkupLine($"  {snapshots.Count} snapshots → {coalesced.Count} distinct builds");

        // Step 4: Diff adjacent pairs
        AnsiConsole.MarkupLine($"[blue]Step 4/{totalStepsInit}:[/] Computing diffs...");
        var diffs = new List<VersionDiffResult>();
        for (var i = 0; i < coalesced.Count - 1; i++)
        {
            var diff = SnapshotDiffer.Diff(coalesced[i], coalesced[i + 1]);
            diffs.Add(diff);
            AnsiConsole.MarkupLine($"  {Markup.Escape(diff.FromBuild.Label)} → {Markup.Escape(diff.ToBuild.Label)}: " +
                                   $"+{diff.TotalAdded} -{diff.TotalRemoved} ~{diff.TotalChanged}");
        }

        // Step 5: FO3 leftover filtering (optional)
        HashSet<uint>? fo3LeftoverFormIds = null;
        if (opts.Fo3EsmDir != null)
        {
            AnsiConsole.MarkupLine(
                $"[blue]Step 5/{totalStepsInit}:[/] Extracting Fallout 3 data for leftover filtering...");
            fo3LeftoverFormIds = await ExtractFo3LeftoversAsync(opts, coalesced, cancellationToken);
            AnsiConsole.MarkupLine(
                $"  [green]Identified {fo3LeftoverFormIds.Count:N0} FO3 leftover FormIDs to filter[/]");
        }

        // Step 6: Generate reports
        AnsiConsole.MarkupLine($"[blue]Step {totalStepsInit}/{totalStepsInit}:[/] Generating reports...");
        Directory.CreateDirectory(opts.OutputDir);
        await GenerateReportsAsync(opts, coalesced, diffs, fo3LeftoverFormIds, cancellationToken);

        // Print summary
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[green]Version tracking complete.[/]");
        AnsiConsole.MarkupLine($"  Builds: {coalesced.Count}, Diffs: {diffs.Count}");

        var totalChanges = diffs.Sum(d => d.TotalAdded + d.TotalRemoved + d.TotalChanged);
        AnsiConsole.MarkupLine($"  Total changes: {totalChanges:N0}");
    }

    private static async Task<List<VersionSnapshot>> ExtractAllSnapshotsAsync(
        List<BuildInfo> builds, ReportOptions opts, CancellationToken cancellationToken)
    {
        var cache = new SnapshotCache(opts.CacheDir);
        var snapshots = new List<VersionSnapshot>();

        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                for (var i = 0; i < builds.Count; i++)
                {
                    var build = builds[i];
                    var label = $"[[{i + 1}/{builds.Count}]] {Markup.Escape(build.Label)}";
                    var task = ctx.AddTask(label, maxValue: 100);

                    var snapshot = await ExtractSingleSnapshotAsync(build, cache, opts.Force, task, cancellationToken);
                    if (snapshot != null)
                    {
                        snapshots.Add(snapshot);
                    }
                }
            });

        return snapshots;
    }

    private static async Task<VersionSnapshot?> ExtractSingleSnapshotAsync(
        BuildInfo build, SnapshotCache cache, bool force,
        ProgressTask task, CancellationToken cancellationToken)
    {
        // Check cache
        if (!force)
        {
            var cached = cache.TryLoad(build.SourcePath);
            if (cached != null)
            {
                task.Value = 100;
                task.Description =
                    $"[green]{Markup.Escape(build.Label)}[/] [grey](cached, {cached.TotalRecordCount:N0} records)[/]";
                return cached;
            }
        }

        try
        {
            var progress = new Progress<(int percent, string phase)>(p =>
            {
                task.Value = p.percent;
                task.Description = $"{Markup.Escape(build.Label)}: [grey]{Markup.Escape(p.phase)}[/]";
            });

            var snapshot = build.SourceType == BuildSourceType.Esm
                ? await EsmSnapshotExtractor.ExtractAsync(build.SourcePath, build, progress, cancellationToken)
                : await DmpSnapshotExtractor.ExtractAsync(build.SourcePath, build, progress, cancellationToken);

            cache.Save(build.SourcePath, snapshot);
            task.Value = 100;
            task.Description =
                $"[green]{Markup.Escape(build.Label)}[/] [grey]({snapshot.TotalRecordCount:N0} records)[/]";
            return snapshot;
        }
        catch (Exception ex)
        {
            task.Description = $"[red]{Markup.Escape(build.Label)}[/] [grey]({Markup.Escape(ex.Message)})[/]";
            task.Value = 100;
            Logger.Instance.Warn($"[VersionTrack] Failed to extract {build.Label}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    ///     Extracts FO3 ESMs, merges into one snapshot, diffs against combined FNV dev builds,
    ///     and returns the set of FormIDs that are unchanged FO3 leftovers (should be filtered).
    /// </summary>
    private static async Task<HashSet<uint>> ExtractFo3LeftoversAsync(
        ReportOptions opts, List<VersionSnapshot> coalesced, CancellationToken cancellationToken)
    {
        var fo3Dir = opts.Fo3EsmDir!;
        var cache = new SnapshotCache(opts.CacheDir);

        // Discover FO3 ESM files
        var fo3Esms = Directory.GetFiles(fo3Dir, "*.esm", SearchOption.TopDirectoryOnly)
            .OrderByDescending(f => new FileInfo(f).Length) // Base game first
            .ToList();

        AnsiConsole.MarkupLine($"  Found {fo3Esms.Count} FO3 ESM files");

        // Extract each FO3 ESM
        var fo3Snapshots = new List<VersionSnapshot>();
        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                for (var i = 0; i < fo3Esms.Count; i++)
                {
                    var esmPath = fo3Esms[i];
                    var fileName = Path.GetFileName(esmPath);
                    var label = $"[[{i + 1}/{fo3Esms.Count}]] FO3: {Markup.Escape(fileName)}";
                    var task = ctx.AddTask(label, maxValue: 100);

                    // Check cache
                    var cached = cache.TryLoad(esmPath);
                    if (cached != null)
                    {
                        task.Value = 100;
                        task.Description =
                            $"[green]FO3: {Markup.Escape(fileName)}[/] [grey](cached, {cached.TotalRecordCount:N0} records)[/]";
                        fo3Snapshots.Add(cached);
                        continue;
                    }

                    try
                    {
                        var buildInfo = new BuildInfo
                        {
                            Label = $"FO3: {fileName}",
                            SourcePath = esmPath,
                            SourceType = BuildSourceType.Esm
                        };

                        var progress = new Progress<(int percent, string phase)>(p =>
                        {
                            task.Value = p.percent;
                            task.Description = $"FO3: {Markup.Escape(fileName)}: [grey]{Markup.Escape(p.phase)}[/]";
                        });

                        var snapshot =
                            await EsmSnapshotExtractor.ExtractAsync(esmPath, buildInfo, progress, cancellationToken);
                        cache.Save(esmPath, snapshot);

                        task.Value = 100;
                        task.Description =
                            $"[green]FO3: {Markup.Escape(fileName)}[/] [grey]({snapshot.TotalRecordCount:N0} records)[/]";
                        fo3Snapshots.Add(snapshot);
                    }
                    catch (Exception ex)
                    {
                        task.Description =
                            $"[red]FO3: {Markup.Escape(fileName)}[/] [grey]({Markup.Escape(ex.Message)})[/]";
                        task.Value = 100;
                    }
                }
            });

        if (fo3Snapshots.Count == 0)
        {
            AnsiConsole.MarkupLine("  [yellow]Warning: No FO3 snapshots extracted, skipping filtering[/]");
            return [];
        }

        // Merge all FO3 snapshots into one
        var fo3Combined = SnapshotCoalescer.MergeAll(fo3Snapshots, "Fallout 3 (All ESMs)");
        AnsiConsole.MarkupLine($"  FO3 combined: {fo3Combined.TotalRecordCount:N0} records");

        // Merge all FNV dev builds (non-baseline) into one
        var fnvDevSnapshots = coalesced.Where(s => s.Build.SourceType == BuildSourceType.Dmp).ToList();
        if (fnvDevSnapshots.Count == 0)
        {
            // Fall back to all non-final snapshots
            var final = coalesced.LastOrDefault(s => s.Build.IsAuthoritative);
            fnvDevSnapshots = coalesced.Where(s => s != final).ToList();
        }

        if (fnvDevSnapshots.Count == 0)
        {
            return [];
        }

        var fnvCombined = SnapshotCoalescer.MergeAll(fnvDevSnapshots, "FNV Dev (Combined)");

        // Diff FO3 vs FNV dev: identify which FO3 records were changed in FNV
        var fo3VsFnv = SnapshotDiffer.Diff(fo3Combined, fnvCombined);
        var changedFormIds = new HashSet<uint>(
            fo3VsFnv.AllChanges
                .Where(c => c.ChangeType == ChangeType.Changed
                            && HasSignificantFo3Changes(c.FieldChanges))
                .Select(c => c.FormId));

        // FO3 leftover = FO3 FormID present in FNV dev builds but NOT changed
        // "Added" in diff(fo3, fnv) = in fnv, not in fo3 → genuinely new FNV content (not a leftover)
        // "Removed" in diff(fo3, fnv) = in fo3, not in fnv → FO3-only, irrelevant
        // Records in BOTH but unchanged → leftovers to filter
        var fo3FormIds = GetAllFormIds(fo3Combined);
        var fnvFormIds = GetAllFormIds(fnvCombined);

        var leftovers = new HashSet<uint>();
        foreach (var formId in fo3FormIds)
        {
            if (fnvFormIds.Contains(formId) && !changedFormIds.Contains(formId))
            {
                leftovers.Add(formId);
            }
        }

        AnsiConsole.MarkupLine($"  FO3 records in FNV dev builds: {fo3FormIds.Intersect(fnvFormIds).Count():N0}");
        AnsiConsole.MarkupLine($"  Changed from FO3 (keeping): {changedFormIds.Count:N0}");
        AnsiConsole.MarkupLine($"  Unchanged leftovers (filtering): {leftovers.Count:N0}");

        return leftovers;
    }

    private static HashSet<uint> GetAllFormIds(VersionSnapshot snapshot)
    {
        var formIds = new HashSet<uint>();
        foreach (var k in snapshot.Quests.Keys) formIds.Add(k);
        foreach (var k in snapshot.Npcs.Keys) formIds.Add(k);
        foreach (var k in snapshot.Dialogues.Keys) formIds.Add(k);
        foreach (var k in snapshot.Weapons.Keys) formIds.Add(k);
        foreach (var k in snapshot.Armor.Keys) formIds.Add(k);
        foreach (var k in snapshot.Items.Keys) formIds.Add(k);
        foreach (var k in snapshot.Scripts.Keys) formIds.Add(k);
        foreach (var k in snapshot.Locations.Keys) formIds.Add(k);
        foreach (var k in snapshot.Placements.Keys) formIds.Add(k);
        foreach (var k in snapshot.Creatures.Keys) formIds.Add(k);
        foreach (var k in snapshot.Perks.Keys) formIds.Add(k);
        foreach (var k in snapshot.Ammo.Keys) formIds.Add(k);
        foreach (var k in snapshot.LeveledLists.Keys) formIds.Add(k);
        foreach (var k in snapshot.Notes.Keys) formIds.Add(k);
        foreach (var k in snapshot.Terminals.Keys) formIds.Add(k);
        return formIds;
    }

    private static bool HasSignificantFo3Changes(List<FieldChange> changes)
    {
        return changes.Any(c => !Fo3InsignificantFields.Contains(c.FieldName));
    }

    private static async Task GenerateReportsAsync(
        ReportOptions opts, List<VersionSnapshot> coalesced,
        List<VersionDiffResult> diffs, HashSet<uint>? fo3LeftoverFormIds,
        CancellationToken cancellationToken)
    {
        uint? trackFormId = null;
        if (opts.FormId != null)
        {
            trackFormId = CliHelpers.ParseFormId(opts.FormId);
        }

        var format = opts.Format.ToLowerInvariant();

        if (format is "md" or "both" or "all")
        {
            var markdown = MarkdownTimelineWriter.WriteTimeline(coalesced, diffs, trackFormId, fo3LeftoverFormIds);
            var mdPath = Path.Combine(opts.OutputDir, "version_tracking_report.md");
            await File.WriteAllTextAsync(mdPath, markdown, cancellationToken);
            AnsiConsole.MarkupLine($"  [green]Markdown:[/] {mdPath}");

            // Multi-page markdown report
            var pages = MarkdownMultiPageWriter.WritePages(coalesced, diffs, trackFormId, fo3LeftoverFormIds);
            var reportDir = Path.Combine(opts.OutputDir, "report");
            Directory.CreateDirectory(reportDir);
            foreach (var (filename, content) in pages)
            {
                await File.WriteAllTextAsync(Path.Combine(reportDir, filename), content, cancellationToken);
            }

            AnsiConsole.MarkupLine($"  [green]Multi-page Markdown:[/] {reportDir}/ ({pages.Count} pages)");
        }

        if (format is "json" or "both" or "all")
        {
            var json = JsonTimelineWriter.WriteTimeline(coalesced, diffs);
            var jsonPath = Path.Combine(opts.OutputDir, "version_tracking_report.json");
            await File.WriteAllTextAsync(jsonPath, json, cancellationToken);
            AnsiConsole.MarkupLine($"  [green]JSON:[/] {jsonPath}");
        }

        if (format is "wiki" or "all")
        {
            await GenerateWikiReportsAsync(opts, coalesced, fo3LeftoverFormIds, cancellationToken);
        }

        if (format is "codeblock" or "all")
        {
            var codeblock = CodeBlockTimelineWriter.WriteTimeline(coalesced, diffs);
            var cbPath = Path.Combine(opts.OutputDir, "version_tracking_report_codeblock.md");
            await File.WriteAllTextAsync(cbPath, codeblock, cancellationToken);
            AnsiConsole.MarkupLine($"  [green]Code-block:[/] {cbPath}");
        }
    }

    private static async Task GenerateWikiReportsAsync(
        ReportOptions opts, List<VersionSnapshot> coalesced, HashSet<uint>? fo3LeftoverFormIds,
        CancellationToken cancellationToken)
    {
        // Find baseline: explicit path, or auto-detect the last ESM (assumed to be the final build)
        VersionSnapshot? baseline = null;
        if (opts.Baseline != null)
        {
            baseline = coalesced.FirstOrDefault(s =>
                s.Build.SourcePath.Contains(opts.Baseline, StringComparison.OrdinalIgnoreCase));
        }

        baseline ??= coalesced.LastOrDefault(s => s.Build.IsAuthoritative);

        if (baseline == null)
        {
            AnsiConsole.MarkupLine("[yellow]Warning:[/] No baseline ESM found for wiki pages. Skipping wiki output.");
            return;
        }

        AnsiConsole.MarkupLine($"  [grey]Wiki baseline:[/] {Markup.Escape(baseline.Build.Label)}");

        // Separate DMP and ESM snapshots (excluding baseline)
        var dmpSnapshots = coalesced.Where(s => s.Build.SourceType == BuildSourceType.Dmp).ToList();
        var esmSnapshots = coalesced.Where(s => s != baseline && s.Build.IsAuthoritative).ToList();

        // Generate ONE combined page for all memory dumps
        if (dmpSnapshots.Count > 0)
        {
            var combinedDmp = SnapshotCoalescer.MergeAll(dmpSnapshots, "All Memory Dumps (Combined)");

            // Count the total number of original DMP files
            var sourceFileCount = combinedDmp.Build.SourcePath
                .Split(';', StringSplitOptions.TrimEntries)
                .Length;

            // Determine date range
            var dmpDates = dmpSnapshots
                .Where(s => s.Build.BuildDate.HasValue)
                .Select(s => s.Build.BuildDate!.Value)
                .OrderBy(d => d)
                .ToList();

            var dateRange = dmpDates.Count switch
            {
                >= 2 => $"{dmpDates[0]:MMM d, yyyy} – {dmpDates[^1]:MMM d, yyyy}",
                1 => $"{dmpDates[0]:MMM d, yyyy}",
                _ => "Unknown dates"
            };

            var title = $"Memory Dump Builds ({dateRange})";
            var intro = $"Records extracted from {sourceFileCount} memory dumps across {dmpSnapshots.Count} " +
                        $"coalesced development builds spanning {dateRange}.";

            var diff = SnapshotDiffer.Diff(baseline, combinedDmp);
            if (diff.TotalAdded + diff.TotalRemoved + diff.TotalChanged > 0)
            {
                var wiki = MediaWikiTimelineWriter.WriteBuildPage(combinedDmp, baseline, diff, title, intro, true,
                    fo3LeftoverFormIds);
                var fileName = BinaryUtils.SanitizeFilename(title).Replace(' ', '_') + ".mw";
                var wikiPath = Path.Combine(opts.OutputDir, fileName);
                await File.WriteAllTextAsync(wikiPath, wiki, cancellationToken);
                AnsiConsole.MarkupLine($"  [green]Wiki:[/] {wikiPath}");
            }
        }

        // Generate individual pages for non-baseline ESM builds
        foreach (var snapshot in esmSnapshots)
        {
            var diff = SnapshotDiffer.Diff(baseline, snapshot);
            if (diff.TotalAdded + diff.TotalRemoved + diff.TotalChanged == 0)
            {
                continue;
            }

            var title = snapshot.Build.Label;
            var intro = $"Dated {snapshot.Build.BuildDate?.ToString("MMMM d, yyyy") ?? "unknown"}, " +
                        "this build has a number of differences compared to the final release.";

            var wiki = MediaWikiTimelineWriter.WriteBuildPage(snapshot, baseline, diff, title, intro, false,
                fo3LeftoverFormIds);
            var fileName = BinaryUtils.SanitizeFilename(title).Replace(' ', '_') + ".mw";
            var wikiPath = Path.Combine(opts.OutputDir, fileName);
            await File.WriteAllTextAsync(wikiPath, wiki, cancellationToken);
            AnsiConsole.MarkupLine($"  [green]Wiki:[/] {wikiPath}");
        }
    }

    internal sealed record ReportOptions
    {
        public required string BuildsDir { get; init; }
        public required string DumpsDir { get; init; }
        public required string OutputDir { get; init; }
        public required string Format { get; init; }
        public string? Baseline { get; init; }
        public required string CacheDir { get; init; }
        public bool Force { get; init; }
        public string[]? Types { get; init; }
        public string? FormId { get; init; }
        public string? Fo3EsmDir { get; init; }
    }
}
