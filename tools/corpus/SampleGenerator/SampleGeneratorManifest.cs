using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using static SampleGenerator.SampleGeneratorConfig;
using static SampleGenerator.SampleGeneratorFileOperations;

namespace SampleGenerator;

/// <summary>
///     Writes the per-build <c>build.json</c>, and the corpus-level <c>catalog.json</c> /
///     <c>catalog.md</c>.
///     <para>
///         The manifest is what makes a date in a directory name checkable rather than asserted: it
///         records how the date was established, and — for entries dated from an executable — what
///         that executable actually stamps today. A Steam patch that moves the build on shows up as
///         a mismatch instead of silently leaving a stale name.
///     </para>
/// </summary>
internal static class SampleGeneratorManifest
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal static void Write(
        CatalogEntry entry,
        string buildDir,
        IReadOnlyList<SampleGeneratorBuildOperations.ResolvedPart> parts,
        int files,
        long bytes)
    {
        var manifest = new BuildManifest
        {
            Name = SampleGeneratorCatalog.DirectoryName(entry),
            Game = entry.Game,
            Date = entry.Date,
            Platform = entry.Platform,
            Kind = entry.Kind,
            DateSource = entry.DateSource.ToString(),
            Notes = entry.Notes,
            GeneratedUtc = DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            FileCount = files,
            TotalBytes = bytes,
            Sources = [.. parts.Select(p => new ManifestSource
            {
                Kind = p.Spec.Kind.ToString(),
                Hint = p.Spec.Hint,
                ResolvedFrom = p.Source,
                DestinationSubdir = p.Spec.DestinationSubdir,
            })],
            MeasuredExecutableTimestamp = MeasureExecutable(entry, buildDir, out var mismatch),
            ExecutableTimestampMatchesName = mismatch,
        };

        var mods = SampleGeneratorModAudit.Audit(entry, buildDir);
        manifest.Modified = mods.IsModified;
        manifest.Modifications = mods.Findings.Length > 0 ? mods.Findings : null;
        manifest.ShippedExtras = mods.Shipped.Length > 0 ? mods.Shipped : null;
        manifest.LargeAddressAware = mods.LargeAddressAware;

        // Re-read Steam's manifest so a depot update that moved the build on shows up as a
        // mismatch rather than silently leaving a stale date in the directory name.
        var steamSource = parts.FirstOrDefault(part => part.Spec.Kind == SourceKind.SteamGame);
        if (steamSource.Source is not null &&
            SampleGeneratorSteam.ReadDepotState(steamSource.Source, entry.SteamAppId) is { } depot)
        {
            manifest.SteamAppId = entry.SteamAppId;
            manifest.SteamBuildId = depot.BuildId;
            manifest.SteamLastUpdated = depot.LastUpdatedUtc.ToString("O", CultureInfo.InvariantCulture);
            if (entry.DateSource == DateProvenance.SteamDepot && entry.Date is not null)
            {
                manifest.SteamDepotMatchesName =
                    depot.LastUpdatedUtc.ToString("yyyy-M-d", CultureInfo.InvariantCulture) == entry.Date;
            }
        }

        var path = SampleGeneratorBuildOperations.ManifestPath(manifest.Name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, JsonOptions));
    }

    /// <summary>
    ///     Writes the corpus-level catalog beside the build directories.
    ///     <para>
    ///         Each row carries the <c>Sample/</c>-relative paths the build was migrated FROM. That
    ///         is what lets the test suite map a legacy <c>Full_Builds\...</c> path onto its new
    ///         home without keeping a second copy of this table that could drift from it.
    ///     </para>
    /// </summary>
    internal static void WriteCatalog(IReadOnlyList<(CatalogEntry Entry, BuildResult Result)> outcomes)
    {
        var legacyByName = outcomes.ToDictionary(
            o => SampleGeneratorCatalog.DirectoryName(o.Entry),
            o => o.Entry.Parts
                .Where(p => p.Kind is SourceKind.StagedTree or SourceKind.StagedFile)
                .Select(p => new LegacyRow { Path = p.Hint, DestinationSubdir = p.DestinationSubdir })
                // A build re-sourced from Steam keeps no staged part, so its legacy name is
                // declared on the entry instead — see CatalogEntry.LegacyNames.
                .Concat((o.Entry.LegacyNames ?? [])
                    .Select(name => new LegacyRow { Path = @"Full_Builds\" + name }))
                .ToArray(),
            StringComparer.OrdinalIgnoreCase);

        LegacyRow[]? LegacyFor(string name) =>
            legacyByName.TryGetValue(name, out var rows) && rows.Length > 0 ? rows : null;

        var modsByName = outcomes.ToDictionary(
            o => SampleGeneratorCatalog.DirectoryName(o.Entry),
            o => SampleGeneratorModAudit.Audit(
                o.Entry,
                Path.Combine(SampleBuilds, SampleGeneratorCatalog.DirectoryName(o.Entry))),
            StringComparer.OrdinalIgnoreCase);

        string[]? ModsFor(string name) =>
            modsByName.TryGetValue(name, out var found) && found.Findings.Length > 0 ? found.Findings : null;

        string[]? ShippedFor(string name) =>
            modsByName.TryGetValue(name, out var found) && found.Shipped.Length > 0 ? found.Shipped : null;

        var results = outcomes.Select(o => o.Result).ToArray();
        var present = results.Where(r => !r.Missing).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        var missing = results.Where(r => r.Missing).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToArray();

        var catalog = new CorpusCatalog
        {
            GeneratedUtc = DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            BuildCount = present.Length,
            TotalBytes = present.Sum(r => r.TotalBytes),
            Builds = [.. present.Select(r => new CatalogRow
            {
                Name = r.Name,
                FileCount = r.FileCount,
                TotalBytes = r.TotalBytes,
                LegacySources = LegacyFor(r.Name),
                Modifications = ModsFor(r.Name),
                ShippedExtras = ShippedFor(r.Name),
            })],
            Missing = [.. missing.Select(r => new CatalogRow { Name = r.Name, Detail = r.Detail })],
            Excluded = [.. SampleGeneratorCatalog.Excluded.Select(e => new ExcludedRow
            {
                Game = e.Game,
                Location = e.Location,
                ApproximateGigabytes = e.ApproximateGigabytes,
                Reason = e.Reason,
            })],
        };

        File.WriteAllText(
            Path.Combine(SampleCatalog, "catalog.json"),
            JsonSerializer.Serialize(catalog, JsonOptions));

        using var markdown = new StreamWriter(Path.Combine(SampleCatalog, "catalog.md"));
        markdown.WriteLine("# Sample/Builds catalog");
        markdown.WriteLine();
        markdown.WriteLine($"Generated {catalog.GeneratedUtc}. {present.Length} builds, " +
                           $"{catalog.TotalBytes / (double)(1L << 30):F1} GB.");
        markdown.WriteLine();
        markdown.WriteLine("Naming: `Game Name (yyyy-M-d, Platform - Kind)`, matching the sibling");
        markdown.WriteLine("NeversoftMultitool corpus. A build with no defensible date omits it.");
        markdown.WriteLine("Each build's `build.json` records where its bytes came from and how its");
        markdown.WriteLine("date was established.");
        markdown.WriteLine();
        markdown.WriteLine("| Build | Files | Size |");
        markdown.WriteLine("| --- | ---: | ---: |");
        foreach (var row in catalog.Builds)
        {
            markdown.WriteLine($"| {row.Name} | {row.FileCount:N0} | {row.TotalBytes / (double)(1L << 20):N0} MB |");
        }

        if (missing.Length > 0)
        {
            markdown.WriteLine();
            markdown.WriteLine("## Not present");
            markdown.WriteLine();
            markdown.WriteLine("| Build | Why |");
            markdown.WriteLine("| --- | --- |");
            foreach (var row in catalog.Missing)
            {
                markdown.WriteLine($"| {row.Name} | {row.Detail} |");
            }
        }

        var modified = catalog.Builds.Where(row => row.Modifications is { Length: > 0 }).ToArray();
        if (modified.Length > 0)
        {
            markdown.WriteLine();
            markdown.WriteLine("## Modified builds");
            markdown.WriteLine();
            markdown.WriteLine("These are NOT vanilla. Real-asset work over them must assert structure,");
            markdown.WriteLine("never content counts, and measurements taken here will not reproduce");
            markdown.WriteLine("against a clean install.");
            markdown.WriteLine();
            markdown.WriteLine("| Build | Found |");
            markdown.WriteLine("| --- | --- |");
            foreach (var row in modified)
            {
                markdown.WriteLine($"| {row.Name} | {string.Join("; ", row.Modifications!)} |");
            }
        }

        var bundled = catalog.Builds.Where(row => row.ShippedExtras is { Length: > 0 }).ToArray();
        if (bundled.Length > 0)
        {
            markdown.WriteLine();
            markdown.WriteLine("## Releases that bundle extras");
            markdown.WriteLine();
            markdown.WriteLine("Shipped by the release itself, not added by anyone here — but still a");
            markdown.WriteLine("deviation from the original media, which is why the disc releases are");
            markdown.WriteLine("kept as separate builds.");
            markdown.WriteLine();
            markdown.WriteLine("| Build | Bundled |");
            markdown.WriteLine("| --- | --- |");
            foreach (var row in bundled)
            {
                markdown.WriteLine($"| {row.Name} | {string.Join("; ", row.ShippedExtras!)} |");
            }
        }

        markdown.WriteLine();
        markdown.WriteLine("## Deliberately excluded");
        markdown.WriteLine();
        markdown.WriteLine("PC titles after Skyrim Special Edition are not mirrored — they are large,");
        markdown.WriteLine("still installed, and reachable through the test suite's Steam probes.");
        markdown.WriteLine();
        markdown.WriteLine("| Title | Location | Size | Reason |");
        markdown.WriteLine("| --- | --- | ---: | --- |");
        foreach (var row in catalog.Excluded)
        {
            markdown.WriteLine($"| {row.Game} | `{row.Location}` | {row.ApproximateGigabytes:N1} GB | {row.Reason} |");
        }
    }

    private static string? MeasureExecutable(CatalogEntry entry, string buildDir, out bool? matches)
    {
        matches = null;
        if (entry.MainExecutable is null)
        {
            return null;
        }

        var candidate = Directory
            .EnumerateFiles(buildDir, entry.MainExecutable, SearchOption.AllDirectories)
            .FirstOrDefault();
        if (candidate is null)
        {
            return null;
        }

        var stamp = ReadCoffTimestamp(candidate);
        if (stamp is null)
        {
            return null;
        }

        if (entry.Date is not null && entry.DateSource == DateProvenance.ExecutableTimestamp)
        {
            matches = stamp.Value.ToString("yyyy-M-d", System.Globalization.CultureInfo.InvariantCulture) == entry.Date;
        }

        return stamp.Value.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class BuildManifest
    {
        public string Name { get; init; } = string.Empty;
        public string Game { get; init; } = string.Empty;
        public string? Date { get; init; }
        public string Platform { get; init; } = string.Empty;
        public string Kind { get; init; } = string.Empty;
        public string DateSource { get; init; } = string.Empty;
        public string? Notes { get; init; }
        public string GeneratedUtc { get; init; } = string.Empty;
        public int FileCount { get; init; }
        public long TotalBytes { get; init; }
        public ManifestSource[] Sources { get; init; } = [];
        public string? MeasuredExecutableTimestamp { get; init; }
        public bool? ExecutableTimestampMatchesName { get; init; }
        public string? SteamAppId { get; set; }
        public string? SteamBuildId { get; set; }
        public string? SteamLastUpdated { get; set; }
        public bool? SteamDepotMatchesName { get; set; }

        /// <summary>True when the build carries a script extender, wrapper or patched executable.</summary>
        public bool Modified { get; set; }
        public string[]? Modifications { get; set; }

        /// <summary>Extras the release itself bundles — a deviation from the original media, not a user mod.</summary>
        public string[]? ShippedExtras { get; set; }
        public bool? LargeAddressAware { get; set; }
    }

    private sealed class ManifestSource
    {
        public string Kind { get; init; } = string.Empty;
        public string Hint { get; init; } = string.Empty;
        public string ResolvedFrom { get; init; } = string.Empty;
        public string? DestinationSubdir { get; init; }
    }

    private sealed class CorpusCatalog
    {
        public string GeneratedUtc { get; init; } = string.Empty;
        public int BuildCount { get; init; }
        public long TotalBytes { get; init; }
        public CatalogRow[] Builds { get; init; } = [];
        public CatalogRow[] Missing { get; init; } = [];
        public ExcludedRow[] Excluded { get; init; } = [];
    }

    private sealed class CatalogRow
    {
        public string Name { get; init; } = string.Empty;
        public int FileCount { get; init; }
        public long TotalBytes { get; init; }
        public string? Detail { get; init; }

        /// <summary>Sample-relative paths this build was migrated from, for legacy path rewriting.</summary>
        public LegacyRow[]? LegacySources { get; init; }

        /// <summary>Script extenders, wrappers or executable patches found in this build.</summary>
        public string[]? Modifications { get; init; }

        /// <summary>Extras the release itself bundles.</summary>
        public string[]? ShippedExtras { get; init; }
    }

    private sealed class LegacyRow
    {
        public string Path { get; init; } = string.Empty;
        public string? DestinationSubdir { get; init; }
    }

    private sealed class ExcludedRow
    {
        public string Game { get; init; } = string.Empty;
        public string Location { get; init; } = string.Empty;
        public double ApproximateGigabytes { get; init; }
        public string Reason { get; init; } = string.Empty;
    }
}
