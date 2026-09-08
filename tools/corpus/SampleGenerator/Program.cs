using System.Diagnostics;
using static SampleGenerator.SampleGeneratorConfig;

namespace SampleGenerator;

/// <summary>
///     Builds <c>Sample/Builds</c> from the scattered private media collection: Steam installs,
///     disc images, prototype drops, and the trees already staged in this repository.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        CliOptions options;
        try
        {
            options = ParseArguments(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"Argument error: {ex.Message}");
            Console.Error.WriteLine();
            PrintUsage();
            return 2;
        }

        if (options.Help)
        {
            PrintUsage();
            return 0;
        }

        if (options.SelfTest)
        {
            return SampleGeneratorSelfTests.Run();
        }

        try
        {
            Configure(options.MediaRoots, options.SampleRoot, options.ResearchRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or DirectoryNotFoundException or InvalidOperationException)
        {
            Console.Error.WriteLine($"Configuration error: {ex.Message}");
            Console.Error.WriteLine();
            PrintUsage();
            return 2;
        }

        Console.WriteLine(options.DryRun
            ? "Planning Sample/Builds (dry run — nothing is written)..."
            : "Generating Sample/Builds...");
        Console.WriteLine($"Sample root:    {SampleRoot}");
        Console.WriteLine($"Corpus output:  {SampleBuilds}");
        Console.WriteLine($"Media:          {SampleMedia}");
        Console.WriteLine($"Debug symbols:  {SampleDebugSymbols}");
        Console.WriteLine($"RE binaries:    {SampleReverseEngineering}");
        Console.WriteLine($"Catalog:        {SampleCatalog}");
        Console.WriteLine($"Research cache: {ResearchRoot}");
        Console.WriteLine($"Media roots:    {(MediaRoots.Count == 0 ? "(none found)" : string.Join("; ", MediaRoots))}");
        if (!string.IsNullOrWhiteSpace(options.BuildFilter))
        {
            Console.WriteLine($"Build filter:   {options.BuildFilter}");
        }

        Console.WriteLine();

        if (options.MigrateLayout)
        {
            Console.WriteLine("Regrouping non-build fixtures under Sample/...");
            var (moved, skipped) = SampleGeneratorLayout.Apply(options.DryRun);
            Console.WriteLine($"  {moved} move(s), {skipped} already in place.");
            Console.WriteLine();

            Console.WriteLine("Collecting reverse-engineering binaries...");
            var re = SampleGeneratorReverseEngineering.Apply(options.DryRun);
            Console.WriteLine($"  {re.Copied} copied ({re.Bytes / (double)(1L << 20):N0} MB), " +
                              $"{re.Missing} not present.");
            Console.WriteLine();
        }

        var selected = SampleGeneratorCatalog.Builds
            .Where(entry => string.IsNullOrWhiteSpace(options.BuildFilter) ||
                            SampleGeneratorCatalog.DirectoryName(entry)
                                .Contains(options.BuildFilter, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (selected.Length == 0)
        {
            Console.WriteLine($"No builds matched --build \"{options.BuildFilter}\".");
            return 1;
        }

        var stopwatch = Stopwatch.StartNew();
        var outcomes = new List<(CatalogEntry Entry, BuildResult Result)>(selected.Length);

        // Deliberately sequential: the parts are multi-gigabyte copies off a handful of physical
        // disks, so parallelism here would thrash the drives rather than speed anything up.
        foreach (var entry in selected)
        {
            BuildResult result;
            try
            {
                result = SampleGeneratorBuildOperations.RunBuild(entry, options.Repopulate, options.DryRun);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                           InvalidDataException or InvalidOperationException)
            {
                Console.Error.WriteLine(
                    $"  ERROR: {SampleGeneratorCatalog.DirectoryName(entry)}: {ex.GetType().Name}: {ex.Message}");
                result = new BuildResult(SampleGeneratorCatalog.DirectoryName(entry), 0, 0,
                    Missing: true, Skipped: false, Detail: ex.Message);
            }

            outcomes.Add((entry, result));
            Report(result);
        }

        stopwatch.Stop();

        if (!options.DryRun && string.IsNullOrWhiteSpace(options.BuildFilter))
        {
            SampleGeneratorManifest.WriteCatalog(outcomes);
            Console.WriteLine();
            Console.WriteLine($"Wrote {Path.Combine(SampleCatalog, "catalog.md")} and catalog.json");
        }

        var present = outcomes.Count(o => !o.Result.Missing);
        var totalBytes = outcomes.Where(o => !o.Result.Missing).Sum(o => o.Result.TotalBytes);
        Console.WriteLine();
        Console.WriteLine($"Done. {present}/{outcomes.Count} builds, " +
                          $"{totalBytes / (double)(1L << 30):F1} GB, in {stopwatch.Elapsed.TotalSeconds:F1}s");

        return outcomes.Any(o => o.Result.Missing) ? 1 : 0;
    }

    private static void Report(BuildResult result)
    {
        if (result.Missing)
        {
            Console.WriteLine($"  MISSING  {result.Name}");
            if (result.Detail is not null)
            {
                Console.WriteLine($"           {result.Detail}");
            }

            return;
        }

        if (result.Skipped)
        {
            Console.WriteLine($"  skip     {result.Name} ({result.Detail})");
            return;
        }

        var detail = result.Detail is null ? string.Empty : $" [{result.Detail}]";
        Console.WriteLine($"  ok       {result.Name}: {result.FileCount:N0} files, " +
                          $"{result.TotalBytes / (double)(1L << 30):F2} GB{detail}");
    }

    private static CliOptions ParseArguments(string[] args)
    {
        var mediaRoots = new List<string>();
        string? sampleRoot = null;
        string? researchRoot = null;
        string? buildFilter = null;
        var repopulate = false;
        var migrateLayout = false;
        var dryRun = false;
        var selfTest = false;
        var help = false;

        for (var i = 0; i < args.Length; i++)
        {
            var option = args[i];
            switch (option.ToLowerInvariant())
            {
                case "--media-root":
                    mediaRoots.Add(ReadOptionValue(args, ref i, option));
                    break;
                case "--sample-root":
                    sampleRoot = ReadOptionValue(args, ref i, option);
                    break;
                case "--research-root":
                    researchRoot = ReadOptionValue(args, ref i, option);
                    break;
                case "--build":
                    buildFilter = ReadOptionValue(args, ref i, option);
                    break;
                case "--repopulate":
                    repopulate = true;
                    break;
                case "--migrate-layout":
                    migrateLayout = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--self-test":
                    selfTest = true;
                    break;
                case "--help":
                case "-h":
                    help = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown option: {option}");
            }
        }

        return new CliOptions(mediaRoots, sampleRoot, researchRoot, buildFilter,
            repopulate, migrateLayout, dryRun, selfTest, help);
    }

    private static string ReadOptionValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length || args[index + 1].StartsWith('-'))
        {
            throw new ArgumentException($"{option} requires a value.");
        }

        return args[++index];
    }

    private sealed record CliOptions(
        List<string> MediaRoots,
        string? SampleRoot,
        string? ResearchRoot,
        string? BuildFilter,
        bool Repopulate,
        bool MigrateLayout,
        bool DryRun,
        bool SelfTest,
        bool Help);

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: SampleGenerator [options]");
        Console.WriteLine();
        Console.WriteLine("Builds Sample/Builds from the private media collection, naming each build");
        Console.WriteLine("\"Game Name (yyyy-M-d, Platform - Kind)\" as the NeversoftMultitool corpus does.");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine($"  --media-root <path>    Search root for disc images and drops (repeatable; or {MediaRootsVariable}).");
        Console.WriteLine($"  --sample-root <path>   The repository's Sample directory (or {SampleRootVariable}).");
        Console.WriteLine($"  --research-root <path> Cache for expanded .7z/.zip media (or {ResearchRootVariable}).");
        Console.WriteLine("  --build <text>         Process only builds whose directory name contains this text.");
        Console.WriteLine("  --repopulate           Re-populate matching builds that are already present.");
        Console.WriteLine("  --migrate-layout       Also regroup MemoryDumps/DebugSymbols/Saves under Sample/.");
        Console.WriteLine("  --dry-run              Report what would be done; write nothing.");
        Console.WriteLine("  --self-test            Run the path, catalog and mirror checks without media.");
        Console.WriteLine("  --help, -h             Show this help.");
        Console.WriteLine();
        Console.WriteLine("Expanding .7z/.zip media requires 7z on PATH.");
    }
}
