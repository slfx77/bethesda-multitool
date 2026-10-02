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
    /// <summary>Creates the selected GPU backend on the calling thread, or returns null handles for CPU rendering.</summary>
    /// <param name="s">Existing backend flags passed unchanged to the shared CLI selector.</param>
    /// <returns>The renderer and its borrowed-device prerequisite; release the renderer before the device.</returns>
    private static (GpuDevice12? device, GpuSpriteRenderer12? renderer) TryCreateGpuRenderer(
        NifRenderSettings s)
    {
        // This command uses CPU fallback when no renderer handle is available, including an
        // unsuccessful force-GPU selection; ShouldAbort is not part of its existing result contract.
        var selection = SpriteRenderBackendSelector.Create(s.ForceCpu, s.ForceGpu);
        return (selection.Device, selection.Renderer);
    }

    /// <summary>Renders matching archive entries, keeping the GPU batch on one owner thread and CPU work parallel.</summary>
    /// <param name="s">Archive, output, rendering and backend settings.</param>
    /// <param name="ct">Cancels admission of further entries and summary output.</param>
    /// <returns>Completion after batch processing and ordered native cleanup; cancellation and cleanup failures propagate.</returns>
    internal static async Task RunBsaBatchAsync(NifRenderSettings s, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.BsaPath) || !File.Exists(s.BsaPath))
        {
            var archivePath = s.BsaPath ?? "(null)";
            AnsiConsole.MarkupLine("[red]Error:[/] Archive file not found: {0}", archivePath);
            return;
        }
        if (!RenderNifHelpers.ValidateTextureBsas(s.TexturesBsaPaths)) return;

        Directory.CreateDirectory(s.OutputDir);
        AnsiConsole.MarkupLine("Parsing archive: [cyan]{0}[/]", Path.GetFileName(s.BsaPath));
        using var reader = ArchiveReader.Open(s.BsaPath);
        var nifFiles = RenderNifHelpers.CollectNifFiles(reader.ListFiles(), s.Path);
        AnsiConsole.MarkupLine("Found [green]{0}[/] NIF files to process", nifFiles.Count);
        if (nifFiles.Count == 0) return;

        using var textureResolver = RenderNifHelpers.CreateTextureResolver(s.TexturesBsaPaths);
        var crossRef = RenderNifHelpers.LoadEsmCrossReference(s.EsmPath);
        if (s.EsmPath != null && crossRef == null) return;

        await RunBatchAsync(s, nifFiles, textureResolver, crossRef,
            static file => (file.FullPath, RenderNifHelpers.BsaPathToBaseName(file.FullPath)),
            (file, _) => Task.FromResult(reader.Extract(file)), ct);
    }

    /// <summary>Renders eligible local NIF files using a synchronous GPU owner or the existing parallel CPU path.</summary>
    /// <param name="s">Input directory, output, rendering and backend settings.</param>
    /// <param name="ct">Cancels file reads, further item admission and summary output.</param>
    /// <returns>Completion after processing and cleanup; cancellation and cleanup failures propagate.</returns>
    internal static async Task RunLocalDirectoryAsync(NifRenderSettings s, CancellationToken ct)
    {
        if (!Directory.Exists(s.Path))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Directory not found: {0}", s.Path);
            return;
        }
        if (!RenderNifHelpers.ValidateTextureBsas(s.TexturesBsaPaths)) return;

        Directory.CreateDirectory(s.OutputDir);
        var nifPaths = Directory.GetFiles(s.Path, "*.nif", SearchOption.AllDirectories);
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
        if (nifPaths.Length == 0) return;

        using var textureResolver = RenderNifHelpers.CreateTextureResolver(s.TexturesBsaPaths);
        var crossRef = RenderNifHelpers.LoadEsmCrossReference(s.EsmPath);
        if (s.EsmPath != null && crossRef == null) return;

        await RunBatchAsync(s, nifPaths, textureResolver, crossRef,
            static path => (path, Path.GetFileNameWithoutExtension(path)), File.ReadAllBytesAsync, ct);
    }

    /// <summary>Reads one NIF before creating its GPU owner, then renders and releases native resources synchronously.</summary>
    /// <param name="s">Input file, output, rendering and backend settings.</param>
    /// <param name="ct">Cancels the input read before native renderer ownership begins.</param>
    /// <returns>Completion after output and cleanup; input, rendering and cleanup failures propagate.</returns>
    internal static async Task RunLocalFileAsync(NifRenderSettings s, CancellationToken ct)
    {
        if (!File.Exists(s.Path))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] NIF file not found: {0}", s.Path);
            return;
        }
        if (!RenderNifHelpers.ValidateTextureBsas(s.TexturesBsaPaths)) return;

        Directory.CreateDirectory(s.OutputDir);
        using var textureResolver = RenderNifHelpers.CreateTextureResolver(s.TexturesBsaPaths);
        var crossRef = RenderNifHelpers.LoadEsmCrossReference(s.EsmPath);
        if (s.EsmPath != null && crossRef == null) return;

        var nifData = await File.ReadAllBytesAsync(s.Path, ct);
        var (gpuDevice, gpuRenderer) = TryCreateGpuRenderer(s);
        try
        {
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
        }
        finally
        {
            // A failed renderer drain/release must retain its device prerequisite.
            gpuRenderer?.Dispose();
            gpuDevice?.Dispose();
        }
    }

    /// <summary>Shares batch reporting while enclosing every GPU operation in one synchronous worker action.</summary>
    /// <typeparam name="T">Archive entry or local path identifying one input.</typeparam>
    /// <param name="s">Rendering, output and backend settings.</param>
    /// <param name="items">Ordered inputs; the GPU consumes them serially.</param>
    /// <param name="textureResolver">Borrowed texture source, retained by the outer workflow through completion.</param>
    /// <param name="crossRef">Optional borrowed record-reference index.</param>
    /// <param name="describe">Stable source path and output basename for each item.</param>
    /// <param name="read">Cancellable input read; the GPU worker waits without moving its native owner to a continuation.</param>
    /// <param name="ct">Cancels further item admission and applicable input reads.</param>
    /// <returns>Completion after output and ordered cleanup; item failures remain reported individually.</returns>
    private static async Task RunBatchAsync<T>(NifRenderSettings s, IReadOnlyList<T> items,
        NifTextureResolver? textureResolver, EsmModelCrossReference? crossRef,
        Func<T, (string Path, string BaseName)> describe,
        Func<T, CancellationToken, Task<byte[]>> read, CancellationToken ct)
    {
        var index = new ConcurrentDictionary<string, SpriteIndexEntry>();
        var stats = new ProcessingStats();
        var taskLabel = s.Camera.IsMultiView ? "Rendering sprites (4 views)" : "Rendering sprites";

        // Renders loaded bytes and records per-item output statistics. The item supplies
        // output identity and reference lookup; data stays live through rendering.
        // A non-null renderer must belong to the calling thread; null selects the CPU.
        void RenderLoadedItem(T item, byte[] data, GpuSpriteRenderer12? renderer)
        {
            var (path, baseName) = describe(item);
            var results = ProcessNifData(data, baseName, s.OutputDir, s.Render,
                textureResolver, crossRef, path, s, renderer);
            if (results is null)
            {
                Interlocked.Increment(ref stats.Skipped);
                return;
            }
            foreach (var entry in results) index[entry.File] = entry;
            Interlocked.Increment(ref stats.Rendered);
            Interlocked.Add(ref stats.PngCount, results.Count);
        }

        // Reports the item's path and its read/decode/render/write failure while
        // retaining successful outputs and the established CLI message format.
        void ReportFailure(T item, Exception exception)
        {
            Interlocked.Increment(ref stats.Failed);
            AnsiConsole.MarkupLine("[red]FAIL:[/] {0}: {1}", Markup.Escape(describe(item).Path),
                Markup.Escape(exception.Message));
        }

        await CreateBatchProgress().StartAsync(async ctx =>
        {
            var task = ctx.AddTask(taskLabel, maxValue: items.Count);
            var renderedOnGpu = await Task.Run(() =>
            {
                var (gpuDevice, gpuRenderer) = TryCreateGpuRenderer(s);
                try
                {
                    if (gpuRenderer is null) return false;
                    foreach (var item in items)
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            // This worker has no synchronization context. Waiting here preserves cancellable
                            // file I/O while every native call stays on the renderer's creating thread.
                            var data = read(item, ct).GetAwaiter().GetResult();
                            RenderLoadedItem(item, data, gpuRenderer);
                        }
                        catch (Exception ex)
                        {
                            ReportFailure(item, ex);
                        }
                        task.Increment(1);
                    }
                    ct.ThrowIfCancellationRequested();
                    return true;
                }
                finally
                {
                    // Keep this sequence on the worker even on cancellation or a failed progress callback.
                    // If the renderer cannot drain/release, do not release its device underneath it.
                    gpuRenderer?.Dispose();
                    gpuDevice?.Dispose();
                }
            }, ct);

            if (!renderedOnGpu)
            {
                await ParallelWork.ForEachAsync("render-nif", items,
                    ConcurrencyPolicy.FullCores.WithExplicitOverride(s.Parallelism),
                    async (item, _) =>
                    {
                        try
                        {
                            var data = await read(item, ct);
                            RenderLoadedItem(item, data, null);
                        }
                        catch (Exception ex)
                        {
                            ReportFailure(item, ex);
                        }
                        task.Increment(1);
                    }, cancellationToken: ct);
            }
        });
        RenderNifHelpers.WriteIndexAndSummary(s.OutputDir, index, stats, textureResolver, ct);
    }

    /// <summary>Creates the common progress presentation for synchronous GPU and asynchronous CPU batches.</summary>
    /// <returns>A fresh progress owner with the established columns and retention behavior.</returns>
    private static Progress CreateBatchProgress()
    {
        return AnsiConsole.Progress().AutoRefresh(true).AutoClear(false).Columns(
            new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new SpinnerColumn());
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
