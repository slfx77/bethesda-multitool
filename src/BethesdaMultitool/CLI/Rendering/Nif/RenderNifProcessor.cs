using System.Collections.Concurrent;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Esm.Analysis.Coverage;
using BethesdaMultitool.Core.Formats.Esm.Analysis.Geometry;
using BethesdaMultitool.Core.Formats.Nif.Conversion;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Rasterization;
using BethesdaMultitool.Core.Orchestration;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Rendering.Nif;

/// <summary>
///     NIF rendering processor supporting BSA batch, local directory, and single file modes.
/// </summary>
internal static class RenderNifProcessor
{
    /// <summary>
    ///     Creates GPU device and renderer if not force-CPU. Returns null if GPU unavailable.
    /// </summary>
    private static (GpuDevice12? device, GpuSpriteRenderer12? renderer) TryCreateGpuRenderer(
        NifRenderSettings s)
    {
        // forcedCpuMessage MUST stay null: a non-null value makes Create short-circuit to CPU
        // unconditionally (ignoring --gpu). Let Create's own ForceCpu/ForceGpu/auto logic run — it
        // prints the CPU message itself when --cpu, and the GPU/fallback messages otherwise.
        var selection = SpriteRenderBackendSelector.Create(
            s.ForceCpu,
            s.ForceGpu,
            null,
            fallbackCpuMessage: "GPU not available -- using [yellow]CPU software renderer[/]");
        return (selection.Device, selection.Renderer);
    }

    internal static async Task RunBsaBatchAsync(NifRenderSettings s, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.BsaPath) || !File.Exists(s.BsaPath))
        {
            var archivePath = s.BsaPath ?? "(null)";
            AnsiConsole.MarkupLine("[red]Error:[/] Archive file not found: {0}", archivePath);
            return;
        }

        if (!RenderNifHelpers.ValidateTextureBsas(s.TexturesBsaPaths))
        {
            return;
        }

        Directory.CreateDirectory(s.OutputDir);

        // Parse the archive (BSA or BA2, dispatched by magic) and collect NIF files.
        AnsiConsole.MarkupLine("Parsing archive: [cyan]{0}[/]", Path.GetFileName(s.BsaPath));
        using var reader = ArchiveReader.Open(s.BsaPath);
        var nifFiles = RenderNifHelpers.CollectNifFiles(reader.ListFiles(), s.Path);

        AnsiConsole.MarkupLine("Found [green]{0}[/] NIF files to process", nifFiles.Count);

        if (nifFiles.Count == 0)
        {
            return;
        }

        // Create texture resolver if textures BSA(s) provided
        var textureResolver = RenderNifHelpers.CreateTextureResolver(s.TexturesBsaPaths);

        // Build ESM cross-reference index if ESM provided
        var crossRef = RenderNifHelpers.LoadEsmCrossReference(s.EsmPath);
        if (s.EsmPath != null && crossRef == null)
        {
            return;
        }

        // GPU renderer (single-threaded when active)
        var (gpuDevice, gpuRenderer) = TryCreateGpuRenderer(s);
        var policy = gpuRenderer != null
            ? ConcurrencyPolicy.Serial
            : ConcurrencyPolicy.FullCores.WithExplicitOverride(s.Parallelism);

        try
        {
            // Process in parallel (or single-threaded with GPU)
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
                    var taskLabel = s.Camera.IsMultiView
                        ? "Rendering sprites (4 views)"
                        : "Rendering sprites";
                    var task = ctx.AddTask(taskLabel, maxValue: nifFiles.Count);

                    await ParallelWork.ForEachAsync("render-nif", nifFiles, policy,
                        async (file, _) =>
                        {
                            try
                            {
                                var nifData = reader.Extract(file);
                                if (nifData.Length == 0)
                                {
                                    Interlocked.Increment(ref stats.Skipped);
                                    task.Increment(1);
                                    return;
                                }

                                var baseName = RenderNifHelpers.BsaPathToBaseName(file.FullPath);
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
                                AnsiConsole.MarkupLine("[red]FAIL:[/] {0}: {1}", Markup.Escape(file.FullPath),
                                    Markup.Escape(ex.Message));
                            }

                            task.Increment(1);
                            await Task.CompletedTask;
                        }, cancellationToken: ct);
                });

            RenderNifHelpers.WriteIndexAndSummary(s.OutputDir, index, stats, textureResolver, ct);
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

        if (!RenderNifHelpers.ValidateTextureBsas(s.TexturesBsaPaths))
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
                   !p.Contains(Path.DirectorySeparatorChar + "lod" + Path.DirectorySeparatorChar,
                       StringComparison.OrdinalIgnoreCase);
        }).ToArray();

        AnsiConsole.MarkupLine("Found [green]{0}[/] NIF files in [cyan]{1}[/]", nifPaths.Length, s.Path);

        if (nifPaths.Length == 0)
        {
            return;
        }

        var textureResolver = RenderNifHelpers.CreateTextureResolver(s.TexturesBsaPaths);
        var crossRef = RenderNifHelpers.LoadEsmCrossReference(s.EsmPath);
        if (s.EsmPath != null && crossRef == null)
        {
            return;
        }

        try
        {
            var (gpuDevice, gpuRenderer) = TryCreateGpuRenderer(s);
            var policy = gpuRenderer != null
                ? ConcurrencyPolicy.Serial
                : ConcurrencyPolicy.FullCores.WithExplicitOverride(s.Parallelism);

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
                    var taskLabel = s.Camera.IsMultiView
                        ? "Rendering sprites (4 views)"
                        : "Rendering sprites";
                    var task = ctx.AddTask(taskLabel, maxValue: nifPaths.Length);

                    await ParallelWork.ForEachAsync("render-nif", nifPaths, policy,
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
                                AnsiConsole.MarkupLine("[red]FAIL:[/] {0}: {1}", Markup.Escape(nifPath),
                                    Markup.Escape(ex.Message));
                            }

                            task.Increment(1);
                            await Task.CompletedTask;
                        }, cancellationToken: ct);
                });

            RenderNifHelpers.WriteIndexAndSummary(s.OutputDir, index, stats, textureResolver, ct);
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

        if (!RenderNifHelpers.ValidateTextureBsas(s.TexturesBsaPaths))
        {
            return;
        }

        Directory.CreateDirectory(s.OutputDir);

        var textureResolver = RenderNifHelpers.CreateTextureResolver(s.TexturesBsaPaths);
        var crossRef = RenderNifHelpers.LoadEsmCrossReference(s.EsmPath);
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
                AnsiConsole.MarkupLine("Rendered [green]{0}[/] PNG(s) for [cyan]{1}[/]", results.Count,
                    Path.GetFileName(s.Path));
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
        GpuSpriteRenderer12? gpuRenderer = null)
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

        foreach (var (suffix, azimuth, elevation) in s.Camera.ResolveViews())
        {
            var sprite = gpuRenderer != null
                ? gpuRenderer.Render(model, textureResolver,
                    renderParams.PixelsPerUnit, renderParams.MinSize, renderParams.MaxSize,
                    azimuth, elevation, s.FixedSize)
                : NifSpriteRenderer.Render(model, textureResolver,
                    renderParams.PixelsPerUnit, renderParams.MinSize, renderParams.MaxSize,
                    azimuth, elevation, s.FixedSize);
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

            RenderNifHelpers.EnrichWithCrossReference(entry, crossRef, modelPath);
            entries.Add(entry);
        }

        return entries.Count > 0 ? entries : null;
    }
}
