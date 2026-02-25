using System.Collections.Concurrent;
using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using FalloutXbox360Utils.Core.Formats.Bsa;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis;
using FalloutXbox360Utils.Core.Formats.Nif;
using FalloutXbox360Utils.Core.Formats.Nif.Conversion;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI;

/// <summary>
///     CLI command for batch-rendering NIF models from BSA archives to top-down PNG sprites.
/// </summary>
public static partial class SpriteGenCommand
{
    private static readonly JsonSerializerOptions JsonOptions = SpriteIndexJsonContext.Default.Options;

    public static Command Create()
    {
        var command = new Command("sprite-gen", "Batch-render NIF models from a BSA to top-down PNG sprites");

        var inputArg = new Argument<string>("bsa-path") { Description = "Path to meshes BSA file" };
        var outputOption = new Option<string>("-o", "--output") { Description = "Output directory for sprites", Required = true };
        var ppuOption = new Option<float>("--ppu") { Description = "Pixels per game unit (default: 1.0)", DefaultValueFactory = _ => 1.0f };
        var minSizeOption = new Option<int>("--min-size") { Description = "Minimum sprite dimension (default: 32)", DefaultValueFactory = _ => 32 };
        var maxSizeOption = new Option<int>("--max-size") { Description = "Maximum sprite dimension (default: 1024)", DefaultValueFactory = _ => 1024 };
        var filterOption = new Option<string?>("--filter") { Description = "Filter by folder prefix (e.g., meshes\\architecture)" };
        var parallelismOption = new Option<int>("-j", "--parallelism") { Description = "Max parallel tasks (default: processor count)", DefaultValueFactory = _ => Environment.ProcessorCount };
        var texturesBsaOption = new Option<string[]>("--textures-bsa") { Description = "Path to textures BSA file(s) for texture-mapped rendering (can specify multiple)", AllowMultipleArgumentsPerToken = true };
        var esmOption = new Option<string?>("--esm") { Description = "ESM file for cross-referencing FormIDs, EditorIDs, and RefIDs" };
        var isoOption = new Option<bool>("--iso") { Description = "Render 4 isometric views (NE, NW, SW, SE) instead of top-down", DefaultValueFactory = _ => false };
        var elevationOption = new Option<float>("--elevation") { Description = "Isometric camera elevation in degrees from horizontal (default: 30)", DefaultValueFactory = _ => 30f };
        var sideOption = new Option<bool>("--side") { Description = "Render 4 side profile views (front, back, left, right) at 0° elevation", DefaultValueFactory = _ => false };
        var trimetricOption = new Option<bool>("--trimetric") { Description = "Render 4 trimetric axonometric views (unequal axis foreshortening)", DefaultValueFactory = _ => false };
        var sizeOption = new Option<int?>("--size") { Description = "Force all sprites to this size (longest edge), regardless of model scale" };

        command.Arguments.Add(inputArg);
        command.Options.Add(outputOption);
        command.Options.Add(ppuOption);
        command.Options.Add(minSizeOption);
        command.Options.Add(maxSizeOption);
        command.Options.Add(filterOption);
        command.Options.Add(parallelismOption);
        command.Options.Add(texturesBsaOption);
        command.Options.Add(esmOption);
        command.Options.Add(isoOption);
        command.Options.Add(elevationOption);
        command.Options.Add(sideOption);
        command.Options.Add(trimetricOption);
        command.Options.Add(sizeOption);

        command.SetAction(async (parseResult, ct) =>
        {
            var isIso = parseResult.GetValue(isoOption);
            var isSide = parseResult.GetValue(sideOption);
            var isTrimetric = parseResult.GetValue(trimetricOption);

            var viewCount = (isIso ? 1 : 0) + (isSide ? 1 : 0) + (isTrimetric ? 1 : 0);
            if (viewCount > 1)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] --iso, --side, and --trimetric are mutually exclusive");
                return;
            }

            var elevationExplicit = parseResult.GetResult(elevationOption) != null;

            var settings = new SpriteGenSettings
            {
                BsaPath = parseResult.GetValue(inputArg)!,
                OutputDir = parseResult.GetValue(outputOption)!,
                Render = new RenderSettings(
                    parseResult.GetValue(ppuOption),
                    parseResult.GetValue(minSizeOption),
                    parseResult.GetValue(maxSizeOption)),
                Filter = parseResult.GetValue(filterOption),
                Parallelism = parseResult.GetValue(parallelismOption),
                TexturesBsaPaths = parseResult.GetValue(texturesBsaOption) ?? [],
                EsmPath = parseResult.GetValue(esmOption),
                Isometric = isIso,
                ElevationDeg = parseResult.GetValue(elevationOption),
                ElevationOverridden = elevationExplicit,
                SideProfile = isSide,
                Trimetric = isTrimetric,
                FixedSize = parseResult.GetValue(sizeOption)
            };

            await RunSpriteGen(settings, ct);
        });

        return command;
    }

    private static async Task RunSpriteGen(SpriteGenSettings s, CancellationToken ct)
    {
        if (!File.Exists(s.BsaPath))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] BSA file not found: {0}", s.BsaPath);
            return;
        }

        foreach (var texBsa in s.TexturesBsaPaths)
        {
            if (!File.Exists(texBsa))
            {
                AnsiConsole.MarkupLine("[red]Error:[/] Textures BSA not found: {0}", texBsa);
                return;
            }
        }

        Directory.CreateDirectory(s.OutputDir);

        // Parse BSA and collect NIF files
        AnsiConsole.MarkupLine("Parsing BSA: [cyan]{0}[/]", Path.GetFileName(s.BsaPath));
        var archive = BsaParser.Parse(s.BsaPath);
        var nifFiles = CollectNifFiles(archive, s.Filter);

        AnsiConsole.MarkupLine("Found [green]{0}[/] NIF files to process", nifFiles.Count);

        if (nifFiles.Count == 0)
        {
            return;
        }

        // Create texture resolver if textures BSA(s) provided
        NifTextureResolver? textureResolver = null;
        if (s.TexturesBsaPaths.Length > 0)
        {
            foreach (var texBsa in s.TexturesBsaPaths)
            {
                AnsiConsole.MarkupLine("Loading textures BSA: [cyan]{0}[/]", Path.GetFileName(texBsa));
            }

            textureResolver = new NifTextureResolver(s.TexturesBsaPaths);
        }

        // Build ESM cross-reference index if ESM provided
        var crossRef = LoadEsmCrossReference(s.EsmPath);
        if (s.EsmPath != null && crossRef == null)
        {
            return;
        }

        try
        {
            // Process in parallel
            var index = new ConcurrentDictionary<string, SpriteIndexEntry>();
            var stats = new ProcessingStats();

            using var extractor = new BsaExtractor(s.BsaPath);

            await AnsiConsole.Progress()
                .AutoRefresh(true)
                .AutoClear(false)
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new SpinnerColumn())
                .StartAsync(async ctx =>
                {
                    var taskLabel = s.Isometric || s.SideProfile || s.Trimetric ? "Rendering sprites (4 views)" : "Rendering sprites";
                    var task = ctx.AddTask(taskLabel, maxValue: nifFiles.Count);

                    await Parallel.ForEachAsync(nifFiles,
                        new ParallelOptions { MaxDegreeOfParallelism = s.Parallelism, CancellationToken = ct },
                        async (file, _) =>
                        {
                            try
                            {
                                var results = ProcessNifFile(extractor, file, s.OutputDir, s.Render,
                                    textureResolver, crossRef, s.Isometric, s.ElevationDeg,
                                    s.SideProfile, s.Trimetric, s.ElevationOverridden, s.FixedSize);
                                if (results != null)
                                {
                                    foreach (var entry in results)
                                    {
                                        index[entry.File] = entry;
                                    }

                                    Interlocked.Increment(ref stats.Rendered);
                                    Interlocked.Add(ref stats.PngCount, results.Count);
                                }
                                else
                                {
                                    Interlocked.Increment(ref stats.Skipped);
                                }
                            }
                            catch (Exception ex)
                            {
                                Interlocked.Increment(ref stats.Failed);
                                AnsiConsole.MarkupLine("[red]FAIL:[/] {0}: {1}", Markup.Escape(file.FullPath), Markup.Escape(ex.Message));
                            }

                            task.Increment(1);
                            await Task.CompletedTask;
                        });
                });

            // Write index file
            var indexPath = Path.Combine(s.OutputDir, "sprite-index.json");
            var sortedIndex = new SortedDictionary<string, SpriteIndexEntry>(index);
            var json = JsonSerializer.Serialize(sortedIndex, JsonOptions);
            await File.WriteAllTextAsync(indexPath, json, ct);

            // Summary
            var pngSuffix = stats.PngCount != stats.Rendered ? $" ({stats.PngCount} PNGs)" : "";
            AnsiConsole.MarkupLine("\nRendered: [green]{0}[/]{1}  Skipped: [yellow]{2}[/]  Failed: [red]{3}[/]",
                stats.Rendered, pngSuffix, stats.Skipped, stats.Failed);

            if (textureResolver != null)
            {
                var textured = index.Values.Count(e => e.HasTexture);
                AnsiConsole.MarkupLine("Textured: [cyan]{0}[/]  Texture cache: [green]{1}[/] hits, [yellow]{2}[/] misses",
                    textured, textureResolver.CacheHits, textureResolver.CacheMisses);
            }

            AnsiConsole.MarkupLine("Index written to: [cyan]{0}[/]", indexPath);
        }
        finally
        {
            textureResolver?.Dispose();
        }
    }

    private static List<BsaFileRecord> CollectNifFiles(BsaArchive archive, string? filter)
    {
        var nifFiles = new List<BsaFileRecord>();

        foreach (var folder in archive.Folders)
        {
            foreach (var file in folder.Files)
            {
                var fullPath = file.FullPath;
                if (!fullPath.EndsWith(".nif", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Skip LOD and marker meshes
                var fileName = Path.GetFileName(fullPath);
                if (fileName.StartsWith("marker", StringComparison.OrdinalIgnoreCase) ||
                    fileName.EndsWith("_far.nif", StringComparison.OrdinalIgnoreCase) ||
                    fileName.EndsWith("_lod.nif", StringComparison.OrdinalIgnoreCase) ||
                    fullPath.Contains("\\lod\\", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Apply folder filter
                if (filter != null && !fullPath.StartsWith(filter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                nifFiles.Add(file);
            }
        }

        return nifFiles;
    }

    /// <summary>Isometric view directions: name suffix → azimuth angle in degrees.</summary>
    private static readonly (string Suffix, float Azimuth)[] IsoViews =
    [
        ("_ne", 45f),
        ("_nw", 135f),
        ("_sw", 225f),
        ("_se", 315f)
    ];

    /// <summary>Side profile view directions: name suffix → azimuth angle in degrees (0° elevation).</summary>
    private static readonly (string Suffix, float Azimuth)[] SideViews =
    [
        ("_front", 0f),
        ("_back", 180f),
        ("_left", 90f),
        ("_right", 270f)
    ];

    /// <summary>
    ///     Trimetric axonometric view directions matching Fallout 1/2 camera setup.
    ///     Azimuth 30° (vs isometric 45°) produces unequal X/Y axis foreshortening.
    ///     Default elevation: 25.66° = asin(cos(30°)/2), per original Fallout engine.
    /// </summary>
    private const float TrimetricDefaultElevation = 25.65891f;

    private static readonly (string Suffix, float Azimuth)[] TrimetricViews =
    [
        ("_tri_ne", 30f),
        ("_tri_nw", 120f),
        ("_tri_sw", 210f),
        ("_tri_se", 300f)
    ];

    private static List<SpriteIndexEntry>? ProcessNifFile(BsaExtractor extractor, BsaFileRecord file,
        string outputDir, RenderSettings settings, NifTextureResolver? textureResolver,
        EsmModelCrossReference? crossRef, bool isometric, float elevationDeg,
        bool sideProfile = false, bool trimetric = false, bool elevationOverridden = false,
        int? fixedSize = null)
    {
        var nifData = extractor.ExtractFile(file);
        if (nifData.Length == 0)
        {
            return null;
        }

        var nif = NifParser.Parse(nifData);
        if (nif == null)
        {
            return null;
        }

        // Convert Xbox 360 big-endian NIFs to PC format before extracting geometry
        if (nif.IsBigEndian)
        {
            var converted = NifConverter.Convert(nifData);
            if (!converted.Success || converted.OutputData == null)
            {
                return null;
            }

            nifData = converted.OutputData;
            nif = NifParser.Parse(nifData);
            if (nif == null)
            {
                return null;
            }
        }

        var model = NifGeometryExtractor.Extract(nifData, nif, textureResolver);
        if (model == null || !model.HasGeometry)
        {
            return null;
        }

        // Convert BSA path to base filename: meshes\foo\bar.nif → meshes__foo__bar
        var baseName = file.FullPath
            .Replace('\\', '_')
            .Replace('/', '_');
        baseName = Path.GetFileNameWithoutExtension(baseName);

        var entries = new List<SpriteIndexEntry>();

        if (isometric || sideProfile || trimetric)
        {
            var views = IsoViews;
            if (trimetric)
            {
                views = TrimetricViews;
            }
            else if (sideProfile)
            {
                views = SideViews;
            }

            var viewElevation = elevationDeg;
            if (sideProfile)
            {
                viewElevation = 0f;
            }
            else if (trimetric && !elevationOverridden)
            {
                viewElevation = TrimetricDefaultElevation;
            }

            foreach (var (suffix, azimuth) in views)
            {
                var sprite = NifSpriteRenderer.Render(model, textureResolver,
                    settings.PixelsPerUnit, settings.MinSize, settings.MaxSize,
                    azimuth, viewElevation, fixedSize);
                if (sprite == null)
                {
                    continue;
                }

                var spriteFileName = baseName + suffix + ".png";
                var spritePath = Path.Combine(outputDir, spriteFileName);
                PngWriter.SaveRgba(sprite.Pixels, sprite.Width, sprite.Height, spritePath);

                var entry = new SpriteIndexEntry
                {
                    File = spriteFileName,
                    Width = sprite.Width,
                    Height = sprite.Height,
                    BoundsWidth = sprite.BoundsWidth,
                    BoundsHeight = sprite.BoundsHeight,
                    HasTexture = sprite.HasTexture
                };

                EnrichWithCrossReference(entry, crossRef, file.FullPath);
                entries.Add(entry);
            }
        }
        else
        {
            var sprite = NifSpriteRenderer.Render(model, textureResolver,
                settings.PixelsPerUnit, settings.MinSize, settings.MaxSize,
                fixedSize: fixedSize);
            if (sprite == null)
            {
                return null;
            }

            var spriteFileName = baseName + ".png";
            var spritePath = Path.Combine(outputDir, spriteFileName);
            PngWriter.SaveRgba(sprite.Pixels, sprite.Width, sprite.Height, spritePath);

            var entry = new SpriteIndexEntry
            {
                File = spriteFileName,
                Width = sprite.Width,
                Height = sprite.Height,
                BoundsWidth = sprite.BoundsWidth,
                BoundsHeight = sprite.BoundsHeight,
                HasTexture = sprite.HasTexture
            };

            EnrichWithCrossReference(entry, crossRef, file.FullPath);
            entries.Add(entry);
        }

        return entries.Count > 0 ? entries : null;
    }

    /// <summary>
    ///     Loads and builds an ESM cross-reference index. Returns null if the path is invalid or loading fails.
    /// </summary>
    private static EsmModelCrossReference? LoadEsmCrossReference(string? esmPath)
    {
        if (esmPath == null)
        {
            return null;
        }

        if (!File.Exists(esmPath))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] ESM file not found: {0}", esmPath);
            return null;
        }

        var esm = EsmFileLoader.Load(esmPath, printStatus: false);
        if (esm == null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Failed to load ESM file");
            return null;
        }

        AnsiConsole.MarkupLine("Building ESM cross-reference: [cyan]{0}[/]", Path.GetFileName(esmPath));
        var crossRef = EsmModelCrossReference.Build(esm.Data, esm.IsBigEndian);
        AnsiConsole.MarkupLine("Indexed [green]{0}[/] base records, [green]{1}[/] placed references",
            crossRef.BaseRecordCount, crossRef.RefCount);
        return crossRef;
    }

    /// <summary>
    ///     Populates BaseRecords and Refs on a sprite index entry from the ESM cross-reference.
    /// </summary>
    private static void EnrichWithCrossReference(SpriteIndexEntry entry, EsmModelCrossReference? crossRef,
        string modelPath)
    {
        var xref = crossRef?.Lookup(modelPath);
        if (xref == null)
        {
            return;
        }

        if (xref.BaseRecords.Count > 0)
        {
            entry.BaseRecords = new Dictionary<string, BaseRecordValue>();
            foreach (var br in xref.BaseRecords)
            {
                entry.BaseRecords[br.FormId.ToString("X8")] = new BaseRecordValue
                {
                    EditorId = br.EditorId,
                    Type = br.RecordType
                };
            }
        }

        if (xref.Refs.Count > 0)
        {
            entry.Refs = new Dictionary<string, string?>();
            foreach (var r in xref.Refs)
            {
                entry.Refs[r.FormId.ToString("X8")] = r.EditorId;
            }
        }
    }

    private sealed class SpriteGenSettings
    {
        public required string BsaPath { get; init; }
        public required string OutputDir { get; init; }
        public required RenderSettings Render { get; init; }
        public string? Filter { get; init; }
        public int Parallelism { get; init; }
        public string[] TexturesBsaPaths { get; init; } = [];
        public string? EsmPath { get; init; }
        public bool Isometric { get; init; }
        public float ElevationDeg { get; init; } = 30f;
        public bool ElevationOverridden { get; init; }
        public bool SideProfile { get; init; }
        public bool Trimetric { get; init; }
        public int? FixedSize { get; init; }
    }

    private sealed record RenderSettings(float PixelsPerUnit, int MinSize, int MaxSize);

    private sealed class ProcessingStats
    {
        public int Rendered;
        public int Skipped;
        public int Failed;
        public int PngCount;
    }

    private sealed class SpriteIndexEntry
    {
        public required string File { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required float BoundsWidth { get; init; }
        public required float BoundsHeight { get; init; }
        public bool HasTexture { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, BaseRecordValue>? BaseRecords { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, string?>? Refs { get; set; }
    }

    private sealed class BaseRecordValue
    {
        public string? EditorId { get; init; }
        public required string Type { get; init; }
    }

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(SortedDictionary<string, SpriteIndexEntry>))]
    [JsonSerializable(typeof(Dictionary<string, BaseRecordValue>))]
    [JsonSerializable(typeof(Dictionary<string, string?>))]
    private sealed partial class SpriteIndexJsonContext : JsonSerializerContext;
}
