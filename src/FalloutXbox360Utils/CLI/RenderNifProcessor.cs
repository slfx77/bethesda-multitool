using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using FalloutXbox360Utils.Core.Formats.Bsa;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis;
using FalloutXbox360Utils.Core.Formats.Nif;
using FalloutXbox360Utils.Core.Formats.Nif.Conversion;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI;

/// <summary>
///     NIF rendering processor supporting BSA batch, local directory, and single file modes.
/// </summary>
internal static partial class RenderNifProcessor
{
    private static readonly JsonSerializerOptions JsonOptions = RenderIndexJsonContext.Default.Options;

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

    /// <summary>
    ///     Creates GPU device and renderer if not force-CPU. Returns null if GPU unavailable.
    /// </summary>
    private static (GpuDevice? device, GpuSpriteRenderer? renderer) TryCreateGpuRenderer(NifRenderSettings s)
    {
        if (s.ForceCpu)
        {
            AnsiConsole.MarkupLine("Using CPU software renderer ([yellow]--cpu[/])");
            return (null, null);
        }

        var gpuDevice = GpuDevice.Create();
        if (gpuDevice != null)
        {
            var renderer = new GpuSpriteRenderer(gpuDevice);
            AnsiConsole.MarkupLine("GPU rendering: [green]{0}[/] ({1})",
                gpuDevice.Backend, gpuDevice.Device.DeviceName);
            return (gpuDevice, renderer);
        }

        if (s.ForceGpu)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] --gpu specified but no GPU backend available");
        }

        return (null, null);
    }

    internal static async Task RunBsaBatchAsync(NifRenderSettings s, CancellationToken ct)
    {
        if (!File.Exists(s.BsaPath))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] BSA file not found: {0}", s.BsaPath);
            return;
        }

        if (!ValidateTextureBsas(s.TexturesBsaPaths))
        {
            return;
        }

        Directory.CreateDirectory(s.OutputDir);

        // Parse BSA and collect NIF files
        AnsiConsole.MarkupLine("Parsing BSA: [cyan]{0}[/]", Path.GetFileName(s.BsaPath));
        var archive = BsaParser.Parse(s.BsaPath);
        var nifFiles = CollectNifFiles(archive, s.Path);

        AnsiConsole.MarkupLine("Found [green]{0}[/] NIF files to process", nifFiles.Count);

        if (nifFiles.Count == 0)
        {
            return;
        }

        // Create texture resolver if textures BSA(s) provided
        var textureResolver = CreateTextureResolver(s.TexturesBsaPaths);

        // Build ESM cross-reference index if ESM provided
        var crossRef = LoadEsmCrossReference(s.EsmPath);
        if (s.EsmPath != null && crossRef == null)
        {
            return;
        }

        // GPU renderer (single-threaded when active)
        var (gpuDevice, gpuRenderer) = TryCreateGpuRenderer(s);
        var parallelism = gpuRenderer != null ? 1 : s.Parallelism;

        try
        {
            // Process in parallel (or single-threaded with GPU)
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
                        new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct },
                        async (file, _) =>
                        {
                            try
                            {
                                var nifData = extractor.ExtractFile(file);
                                if (nifData.Length == 0)
                                {
                                    Interlocked.Increment(ref stats.Skipped);
                                    task.Increment(1);
                                    return;
                                }

                                var baseName = BsaPathToBaseName(file.FullPath);
                                var results = ProcessNifData(nifData, baseName, s.OutputDir, s.Render,
                                    textureResolver, crossRef, file.FullPath, s, gpuRenderer);
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

            WriteIndexAndSummary(s.OutputDir, index, stats, textureResolver, ct);
        }
        finally
        {
            gpuRenderer?.Dispose();
            gpuDevice?.Dispose();
            textureResolver?.Dispose();
        }
    }

    internal static async Task RunLocalDirectoryAsync(NifRenderSettings s, CancellationToken ct)
    {
        if (!Directory.Exists(s.Path))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Directory not found: {0}", s.Path);
            return;
        }

        if (!ValidateTextureBsas(s.TexturesBsaPaths))
        {
            return;
        }

        Directory.CreateDirectory(s.OutputDir);

        var nifPaths = Directory.GetFiles(s.Path, "*.nif", SearchOption.AllDirectories);

        // Filter out LOD/marker meshes
        nifPaths = nifPaths.Where(p =>
        {
            var fileName = Path.GetFileName(p);
            return !fileName.StartsWith("marker", StringComparison.OrdinalIgnoreCase) &&
                   !fileName.EndsWith("_far.nif", StringComparison.OrdinalIgnoreCase) &&
                   !fileName.EndsWith("_lod.nif", StringComparison.OrdinalIgnoreCase) &&
                   !p.Contains(Path.DirectorySeparatorChar + "lod" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }).ToArray();

        AnsiConsole.MarkupLine("Found [green]{0}[/] NIF files in [cyan]{1}[/]", nifPaths.Length, s.Path);

        if (nifPaths.Length == 0)
        {
            return;
        }

        var textureResolver = CreateTextureResolver(s.TexturesBsaPaths);
        var crossRef = LoadEsmCrossReference(s.EsmPath);
        if (s.EsmPath != null && crossRef == null)
        {
            return;
        }

        try
        {
            var (gpuDevice, gpuRenderer) = TryCreateGpuRenderer(s);
            var parallelism = gpuRenderer != null ? 1 : s.Parallelism;

            var index = new ConcurrentDictionary<string, SpriteIndexEntry>();
            var stats = new ProcessingStats();

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
                    var task = ctx.AddTask(taskLabel, maxValue: nifPaths.Length);

                    await Parallel.ForEachAsync(nifPaths,
                        new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct },
                        async (nifPath, _) =>
                        {
                            try
                            {
                                var nifData = await File.ReadAllBytesAsync(nifPath, ct);
                                var baseName = Path.GetFileNameWithoutExtension(nifPath);
                                var results = ProcessNifData(nifData, baseName, s.OutputDir, s.Render,
                                    textureResolver, crossRef, nifPath, s, gpuRenderer);
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
                                AnsiConsole.MarkupLine("[red]FAIL:[/] {0}: {1}", Markup.Escape(nifPath), Markup.Escape(ex.Message));
                            }

                            task.Increment(1);
                            await Task.CompletedTask;
                        });
                });

            WriteIndexAndSummary(s.OutputDir, index, stats, textureResolver, ct);
            gpuRenderer?.Dispose();
            gpuDevice?.Dispose();
        }
        finally
        {
            textureResolver?.Dispose();
        }
    }

    internal static async Task RunLocalFileAsync(NifRenderSettings s, CancellationToken ct)
    {
        if (!File.Exists(s.Path))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] NIF file not found: {0}", s.Path);
            return;
        }

        if (!ValidateTextureBsas(s.TexturesBsaPaths))
        {
            return;
        }

        Directory.CreateDirectory(s.OutputDir);

        var textureResolver = CreateTextureResolver(s.TexturesBsaPaths);
        var crossRef = LoadEsmCrossReference(s.EsmPath);
        if (s.EsmPath != null && crossRef == null)
        {
            return;
        }

        try
        {
            var (gpuDevice, gpuRenderer) = TryCreateGpuRenderer(s);

            var nifData = await File.ReadAllBytesAsync(s.Path, ct);
            var baseName = Path.GetFileNameWithoutExtension(s.Path);
            var results = ProcessNifData(nifData, baseName, s.OutputDir, s.Render,
                textureResolver, crossRef, s.Path, s, gpuRenderer);

            if (results != null)
            {
                AnsiConsole.MarkupLine("Rendered [green]{0}[/] PNG(s) for [cyan]{1}[/]", results.Count, Path.GetFileName(s.Path));
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]Skipped:[/] {0} (no renderable geometry)", Path.GetFileName(s.Path));
            }

            gpuRenderer?.Dispose();
            gpuDevice?.Dispose();
        }
        finally
        {
            textureResolver?.Dispose();
        }
    }

    private static List<SpriteIndexEntry>? ProcessNifData(byte[] nifData, string baseName,
        string outputDir, RenderParams renderParams, NifTextureResolver? textureResolver,
        EsmModelCrossReference? crossRef, string modelPath, NifRenderSettings s,
        GpuSpriteRenderer? gpuRenderer = null)
    {
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

        var entries = new List<SpriteIndexEntry>();

        if (s.Isometric || s.SideProfile || s.Trimetric)
        {
            var views = IsoViews;
            if (s.Trimetric)
            {
                views = TrimetricViews;
            }
            else if (s.SideProfile)
            {
                views = SideViews;
            }

            var viewElevation = s.ElevationDeg;
            if (s.SideProfile)
            {
                viewElevation = 0f;
            }
            else if (s.Trimetric && !s.ElevationOverridden)
            {
                viewElevation = TrimetricDefaultElevation;
            }

            foreach (var (suffix, azimuth) in views)
            {
                var sprite = gpuRenderer != null
                    ? gpuRenderer.Render(model, textureResolver,
                        renderParams.PixelsPerUnit, renderParams.MinSize, renderParams.MaxSize,
                        azimuth, viewElevation, s.FixedSize)
                    : NifSpriteRenderer.Render(model, textureResolver,
                        renderParams.PixelsPerUnit, renderParams.MinSize, renderParams.MaxSize,
                        azimuth, viewElevation, s.FixedSize);
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

                EnrichWithCrossReference(entry, crossRef, modelPath);
                entries.Add(entry);
            }
        }
        else
        {
            // Default: front-facing view (azimuth 0°, elevation 0°)
            var sprite = gpuRenderer != null
                ? gpuRenderer.Render(model, textureResolver,
                    renderParams.PixelsPerUnit, renderParams.MinSize, renderParams.MaxSize,
                    0f, 0f, s.FixedSize)
                : NifSpriteRenderer.Render(model, textureResolver,
                    renderParams.PixelsPerUnit, renderParams.MinSize, renderParams.MaxSize,
                    0f, 0f, s.FixedSize);
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

            EnrichWithCrossReference(entry, crossRef, modelPath);
            entries.Add(entry);
        }

        return entries.Count > 0 ? entries : null;
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

    /// <summary>Convert BSA path to base filename: meshes\foo\bar.nif → meshes__foo__bar</summary>
    private static string BsaPathToBaseName(string bsaPath)
    {
        var baseName = bsaPath
            .Replace('\\', '_')
            .Replace('/', '_');
        return Path.GetFileNameWithoutExtension(baseName);
    }

    private static bool ValidateTextureBsas(string[] textureBsaPaths)
    {
        foreach (var texBsa in textureBsaPaths)
        {
            if (!File.Exists(texBsa))
            {
                AnsiConsole.MarkupLine("[red]Error:[/] Textures BSA not found: {0}", texBsa);
                return false;
            }
        }

        return true;
    }

    private static NifTextureResolver? CreateTextureResolver(string[] textureBsaPaths)
    {
        if (textureBsaPaths.Length == 0)
        {
            return null;
        }

        foreach (var texBsa in textureBsaPaths)
        {
            AnsiConsole.MarkupLine("Loading textures BSA: [cyan]{0}[/]", Path.GetFileName(texBsa));
        }

        return new NifTextureResolver(textureBsaPaths);
    }

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

    private static void WriteIndexAndSummary(string outputDir,
        ConcurrentDictionary<string, SpriteIndexEntry> index,
        ProcessingStats stats, NifTextureResolver? textureResolver, CancellationToken ct)
    {
        // Write index file
        var indexPath = Path.Combine(outputDir, "sprite-index.json");
        var sortedIndex = new SortedDictionary<string, SpriteIndexEntry>(index);
        var json = JsonSerializer.Serialize(sortedIndex, JsonOptions);
        File.WriteAllText(indexPath, json);

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

    internal sealed class NifRenderSettings
    {
        public required string Path { get; init; }
        public required string OutputDir { get; init; }
        public required RenderParams Render { get; init; }
        public string? BsaPath { get; init; }
        public int Parallelism { get; init; }
        public string[] TexturesBsaPaths { get; init; } = [];
        public string? EsmPath { get; init; }
        public bool Isometric { get; init; }
        public float ElevationDeg { get; init; } = 30f;
        public bool ElevationOverridden { get; init; }
        public bool SideProfile { get; init; }
        public bool Trimetric { get; init; }
        public int? FixedSize { get; init; }
        public bool ForceGpu { get; init; }
        public bool ForceCpu { get; init; }
    }

    internal sealed record RenderParams(float PixelsPerUnit, int MinSize, int MaxSize);

    private sealed class ProcessingStats
    {
        public int Rendered;
        public int Skipped;
        public int Failed;
        public int PngCount;
    }

    internal sealed class SpriteIndexEntry
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

    internal sealed class BaseRecordValue
    {
        public string? EditorId { get; init; }
        public required string Type { get; init; }
    }

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(SortedDictionary<string, SpriteIndexEntry>))]
    [JsonSerializable(typeof(Dictionary<string, BaseRecordValue>))]
    [JsonSerializable(typeof(Dictionary<string, string?>))]
    private sealed partial class RenderIndexJsonContext : JsonSerializerContext;
}
