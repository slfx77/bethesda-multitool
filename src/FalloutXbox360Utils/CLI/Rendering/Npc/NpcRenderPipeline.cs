using FalloutXbox360Utils.CLI;
using FalloutXbox360Utils.Core.Formats.Bsa;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assembly;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assets;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Rendering.Npc;

internal static class NpcRenderPipeline
{
    internal static void Run(NpcRenderSettings settings)
    {
        if (!ValidateInputPaths(settings, out var texturesBsaPaths))
        {
            return;
        }

        Directory.CreateDirectory(settings.OutputDir);
        ConfigureRenderer(settings);

        AnsiConsole.MarkupLine(
            "Loading ESM: [cyan]{0}[/]",
            Path.GetFileName(settings.EsmPath));
        var esm = EsmFileLoader.Load(settings.EsmPath, false);
        if (esm == null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Failed to load ESM file");
            return;
        }

        AnsiConsole.MarkupLine("Scanning NPC_ and RACE records...");
        var resolver = NpcAppearanceResolver.Build(esm.Data, esm.IsBigEndian);
        AnsiConsole.MarkupLine(
            "Found [green]{0}[/] NPCs, [green]{1}[/] races",
            resolver.NpcCount,
            resolver.RaceCount);

        AnsiConsole.MarkupLine(
            "Parsing meshes BSA: [cyan]{0}[/]",
            Path.GetFileName(settings.MeshesBsaPath));
        var meshesArchive = BsaParser.Parse(settings.MeshesBsaPath);

        foreach (var path in texturesBsaPaths)
        {
            AnsiConsole.MarkupLine(
                "Loading textures BSA: [cyan]{0}[/]",
                Path.GetFileName(path));
        }

        using var textureResolver = new NifTextureResolver(texturesBsaPaths);
        var pluginName = Path.GetFileName(settings.EsmPath);
        var appearances = ResolveAppearances(settings, resolver, pluginName);
        if (appearances == null)
        {
            return;
        }

        using var meshExtractor = new BsaExtractor(settings.MeshesBsaPath);
        var caches = new NpcRenderCaches();
        var rendered = 0;
        var skipped = 0;
        var failed = 0;

        using var gpuResources = NpcGpuRenderResources.Create(settings);
        if (gpuResources.ShouldAbort)
        {
            return;
        }

        if (gpuResources.Renderer != null)
        {
            RenderNpcsPipelinedGpu(
                appearances,
                gpuResources.Renderer,
                meshesArchive,
                meshExtractor,
                textureResolver,
                caches,
                settings,
                ref rendered,
                ref skipped,
                ref failed);
        }
        else
        {
            RenderNpcsCpu(
                appearances,
                meshesArchive,
                meshExtractor,
                textureResolver,
                caches,
                settings,
                ref rendered,
                ref skipped,
                ref failed);
        }

        AnsiConsole.MarkupLine(
            "\nRendered: [green]{0}[/]  Skipped: [yellow]{1}[/]  Failed: [red]{2}[/]",
            rendered,
            skipped,
            failed);
    }

    private static bool ValidateInputPaths(
        NpcRenderSettings settings,
        out string[] texturesBsaPaths)
    {
        texturesBsaPaths = Array.Empty<string>();

        if (!File.Exists(settings.MeshesBsaPath))
        {
            AnsiConsole.MarkupLine(
                "[red]Error:[/] Meshes BSA not found: {0}",
                settings.MeshesBsaPath);
            return false;
        }

        if (!File.Exists(settings.EsmPath))
        {
            AnsiConsole.MarkupLine(
                "[red]Error:[/] ESM file not found: {0}",
                settings.EsmPath);
            return false;
        }

        texturesBsaPaths = NpcRenderHelpers.ResolveTexturesBsaPaths(
            settings.MeshesBsaPath,
            settings.ExplicitTexturesBsaPath);
        if (texturesBsaPaths.Length == 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No texture BSA files found");
            return false;
        }

        if (settings.DmpPath != null && !File.Exists(settings.DmpPath))
        {
            AnsiConsole.MarkupLine(
                "[red]Error:[/] DMP file not found: {0}",
                settings.DmpPath);
            return false;
        }

        return true;
    }

    private static void ConfigureRenderer(NpcRenderSettings settings)
    {
        if (settings.ExportEgt)
        {
            var egtDir = Path.Combine(settings.OutputDir, "egt_debug");
            FaceGenTextureMorpher.DebugExportDir = egtDir;
            AnsiConsole.MarkupLine(
                "EGT debug export enabled -> [cyan]{0}[/]",
                egtDir);
        }
        else
        {
            FaceGenTextureMorpher.DebugExportDir = null;
        }

        NifSpriteRenderer.DisableBilinear = settings.NoBilinear;
        if (settings.NoBilinear)
        {
            AnsiConsole.MarkupLine(
                "Texture bilinear sampling [yellow]disabled[/]");
        }

        NifSpriteRenderer.DisableBumpMapping = settings.NoBump;
        if (settings.NoBump)
        {
            AnsiConsole.MarkupLine(
                "Normal map / bump mapping [yellow]disabled[/]");
        }

        NifSpriteRenderer.DisableTextures = settings.NoTex;
        if (settings.NoTex)
        {
            AnsiConsole.MarkupLine(
                "Textures [yellow]disabled[/] (flat white lighting only)");
        }

        NifSpriteRenderer.DrawWireframeOverlay = settings.Wireframe;
        if (settings.Wireframe)
        {
            AnsiConsole.MarkupLine(
                "Wireframe overlay [yellow]enabled[/] (eyes highlighted in cyan)");
        }

        if (settings.BumpStrength.HasValue)
        {
            NifSpriteRenderer.BumpStrength = settings.BumpStrength.Value;
            AnsiConsole.MarkupLine(
                "Bump strength set to [cyan]{0:F2}[/]",
                settings.BumpStrength.Value);
        }
    }

    private static void RenderNpcsCpu(
        List<NpcAppearance> appearances,
        BsaArchive meshesArchive,
        BsaExtractor meshExtractor,
        NifTextureResolver textureResolver,
        NpcRenderCaches caches,
        NpcRenderSettings settings,
        ref int rendered,
        ref int skipped,
        ref int failed)
    {
        var views = settings.Camera.ResolveViews(
            defaultAzimuth: 90f,
            defaultElevation: 0f);

        foreach (var npc in appearances)
        {
            try
            {
                foreach (var (suffix, azimuth, elevation) in views)
                {
                    var result = settings.HeadOnly
                        ? RenderNpcHead(
                            npc,
                            meshesArchive,
                            meshExtractor,
                            textureResolver,
                            caches,
                            settings,
                            azimuth,
                            elevation)
                        : RenderNpcFullBody(
                            npc,
                            meshesArchive,
                            meshExtractor,
                            textureResolver,
                            caches,
                            settings,
                            azimuth,
                            elevation);

                    SaveNpcResult(
                        npc,
                        result,
                        settings,
                        appearances.Count,
                        ref rendered,
                        ref skipped,
                        ref failed,
                        suffix);
                }
            }
            catch (Exception ex)
            {
                failed++;
                AnsiConsole.MarkupLine(
                    "[red]FAIL:[/] 0x{0:X8} {1}: {2}",
                    npc.NpcFormId,
                    npc.EditorId ?? "?",
                    Markup.Escape(ex.Message));
            }
            finally
            {
                EvictNpcFaceTexture(textureResolver, npc);
            }
        }
    }

    private static List<NpcAppearance>? ResolveAppearances(
        NpcRenderSettings settings,
        NpcAppearanceResolver resolver,
        string pluginName)
    {
        if (settings.DmpPath != null)
        {
            var dmpFormId = settings.NpcFilters != null
                ? NpcRenderHelpers.ParseFormId(settings.NpcFilters.FirstOrDefault())
                : null;
            var dmpAppearances = NpcRenderHelpers.ResolveFromDmp(
                settings.DmpPath,
                resolver,
                pluginName,
                dmpFormId);
            if (dmpAppearances.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No NPCs resolved from DMP[/]");
                return null;
            }

            return dmpAppearances;
        }

        if (settings.NpcFilters is { Length: > 0 })
        {
            var allAppearances = resolver.ResolveAllHeadOnly(pluginName);
            var formIdSet = new HashSet<uint>();
            var editorIdSet = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var filter in settings.NpcFilters)
            {
                var formId = NpcRenderHelpers.ParseFormId(filter);
                if (formId.HasValue)
                {
                    formIdSet.Add(formId.Value);
                    continue;
                }

                editorIdSet.Add(filter.Trim());
            }

            var filtered = allAppearances
                .Where(appearance =>
                    formIdSet.Contains(appearance.NpcFormId) ||
                    (appearance.EditorId != null &&
                     editorIdSet.Contains(appearance.EditorId)))
                .ToList();

            if (filtered.Count == 0)
            {
                AnsiConsole.MarkupLine(
                    "[red]Error:[/] None of the specified NPCs found in ESM");
                AnsiConsole.MarkupLine(
                    "  Filters: {0}",
                    string.Join(", ", settings.NpcFilters));
                return null;
            }

            AnsiConsole.MarkupLine(
                "Matched [green]{0}[/] NPCs from {1} filter(s)",
                filtered.Count,
                settings.NpcFilters.Length);
            return filtered;
        }

        var allNamed = resolver.ResolveAllHeadOnly(pluginName, true);
        AnsiConsole.MarkupLine(
            "Resolved [green]{0}[/] named NPCs",
            allNamed.Count);
        return allNamed;
    }

    private static void RenderNpcsPipelinedGpu(
        List<NpcAppearance> appearances,
        GpuSpriteRenderer gpuRenderer,
        BsaArchive meshesArchive,
        BsaExtractor meshExtractor,
        NifTextureResolver textureResolver,
        NpcRenderCaches caches,
        NpcRenderSettings settings,
        ref int rendered,
        ref int skipped,
        ref int failed)
    {
        var views = settings.Camera.ResolveViews(
            defaultAzimuth: 90f,
            defaultElevation: 0f);

        NifRenderableModel? currentModel = null;
        if (appearances.Count > 0)
        {
            currentModel = BuildNpcModel(
                appearances[0],
                meshesArchive,
                meshExtractor,
                textureResolver,
                caches,
                settings);
        }

        for (var i = 0; i < appearances.Count; i++)
        {
            var npc = appearances[i];
            NifRenderableModel? nextModel = null;

            try
            {
                for (var viewIndex = 0; viewIndex < views.Length; viewIndex++)
                {
                    var (suffix, azimuth, elevation) = views[viewIndex];
                    GpuSpriteRenderer.PendingRender? pending = null;

                    if (currentModel is { HasGeometry: true })
                    {
                        var renderModel = PrepareModelForView(
                            currentModel,
                            cloneForRender: viewIndex < views.Length - 1);
                        pending = gpuRenderer.SubmitRender(
                            renderModel,
                            textureResolver,
                            1.0f,
                            32,
                            settings.SpriteSize,
                            azimuth,
                            elevation,
                            settings.SpriteSize);
                    }

                    if (viewIndex == views.Length - 1 && i + 1 < appearances.Count)
                    {
                        nextModel = BuildNpcModel(
                            appearances[i + 1],
                            meshesArchive,
                            meshExtractor,
                            textureResolver,
                            caches,
                            settings);
                    }

                    var result = pending != null
                        ? gpuRenderer.CompleteRender(pending)
                        : null;
                    SaveNpcResult(
                        npc,
                        result,
                        settings,
                        appearances.Count,
                        ref rendered,
                        ref skipped,
                        ref failed,
                        suffix);
                }
            }
            catch (Exception ex)
            {
                failed++;
                AnsiConsole.MarkupLine(
                    "[red]FAIL:[/] 0x{0:X8} {1}: {2}",
                    npc.NpcFormId,
                    npc.EditorId ?? "?",
                    Markup.Escape(ex.Message));
            }
            finally
            {
                EvictNpcFaceTexture(textureResolver, npc);
                gpuRenderer.EvictTexture($"facegen_egt\\{npc.NpcFormId:X8}.dds");
                currentModel = nextModel;
            }
        }
    }

    private static NifRenderableModel? BuildNpcModel(
        NpcAppearance npc,
        BsaArchive meshesArchive,
        BsaExtractor meshExtractor,
        NifTextureResolver textureResolver,
        NpcRenderCaches caches,
        NpcRenderSettings settings)
    {
        if (settings.HeadOnly)
        {
            return NpcHeadBuilder.Build(
                npc,
                meshesArchive,
                meshExtractor,
                textureResolver,
                caches.HeadMeshes,
                caches.EgmFiles,
                caches.EgtFiles,
                settings);
        }

        return NpcBodyBuilder.Build(
            npc,
            meshesArchive,
            meshExtractor,
            textureResolver,
            caches.HeadMeshes,
            caches.EgmFiles,
            caches.EgtFiles,
            ref caches.SkeletonBones,
            ref caches.PoseDeltas,
            settings);
    }

    private static NifRenderableModel PrepareModelForView(
        NifRenderableModel model,
        bool cloneForRender)
    {
        var renderModel = cloneForRender ? NpcRenderHelpers.DeepCloneModel(model) : model;
        return renderModel;
    }

    private static SpriteResult? RenderNpcHead(
        NpcAppearance npc,
        BsaArchive meshesArchive,
        BsaExtractor meshExtractor,
        NifTextureResolver textureResolver,
        NpcRenderCaches caches,
        NpcRenderSettings settings,
        float azimuth,
        float elevation)
    {
        var model = NpcHeadBuilder.Build(
            npc,
            meshesArchive,
            meshExtractor,
            textureResolver,
            caches.HeadMeshes,
            caches.EgmFiles,
            caches.EgtFiles,
            settings);
        if (model == null || !model.HasGeometry)
        {
            return null;
        }

        return NifSpriteRenderer.Render(
            model,
            textureResolver,
            1.0f,
            32,
            settings.SpriteSize,
            azimuth,
            elevation,
            settings.SpriteSize);
    }

    private static SpriteResult? RenderNpcFullBody(
        NpcAppearance npc,
        BsaArchive meshesArchive,
        BsaExtractor meshExtractor,
        NifTextureResolver textureResolver,
        NpcRenderCaches caches,
        NpcRenderSettings settings,
        float azimuth,
        float elevation)
    {
        var model = NpcBodyBuilder.Build(
            npc,
            meshesArchive,
            meshExtractor,
            textureResolver,
            caches.HeadMeshes,
            caches.EgmFiles,
            caches.EgtFiles,
            ref caches.SkeletonBones,
            ref caches.PoseDeltas,
            settings);
        if (model == null)
        {
            return null;
        }

        return NifSpriteRenderer.Render(
            model,
            textureResolver,
            1.0f,
            32,
            settings.SpriteSize,
            azimuth,
            elevation,
            settings.SpriteSize);
    }

    private static void SaveNpcResult(
        NpcAppearance npc,
        SpriteResult? result,
        NpcRenderSettings settings,
        int totalCount,
        ref int rendered,
        ref int skipped,
        ref int failed,
        string viewSuffix = "")
    {
        if (result == null)
        {
            skipped++;
            if (settings.NpcFilters != null)
            {
                AnsiConsole.MarkupLine(
                    "[yellow]Skipped:[/] 0x{0:X8} {1} -- no geometry",
                    npc.NpcFormId,
                    npc.FullName ?? npc.EditorId ?? "unknown");
            }

            return;
        }

        var name = npc.EditorId ?? $"{npc.NpcFormId:X8}";
        var fileName = $"{name}{viewSuffix}.png";
        var outputPath = Path.Combine(settings.OutputDir, fileName);
        var expectedLength = result.Width * result.Height * 4;

        if (result.Pixels.Length != expectedLength)
        {
            failed++;
            AnsiConsole.MarkupLine(
                "[red]FAIL:[/] 0x{0:X8} {1}: pixel buffer mismatch ({2} bytes, expected {3} for {4}x{5})",
                npc.NpcFormId,
                npc.EditorId ?? "?",
                result.Pixels.Length,
                expectedLength,
                result.Width,
                result.Height);
            return;
        }

        PngWriter.SaveRgba(
            result.Pixels,
            result.Width,
            result.Height,
            outputPath);
        rendered++;

        if (settings.NpcFilters != null || totalCount <= 20)
        {
            AnsiConsole.MarkupLine(
                "[green]OK:[/] 0x{0:X8} {1} -> {2} ({3}x{4})",
                npc.NpcFormId,
                npc.FullName ?? "?",
                fileName,
                result.Width,
                result.Height);
        }
    }

    private static void EvictNpcFaceTexture(
        NifTextureResolver textureResolver,
        NpcAppearance npc)
    {
        textureResolver.EvictTexture($"facegen_egt\\{npc.NpcFormId:X8}.dds");
    }

    private sealed class NpcGpuRenderResources : IDisposable
    {
        private NpcGpuRenderResources(
            GpuDevice? device,
            GpuSpriteRenderer? renderer,
            bool shouldAbort)
        {
            Device = device;
            Renderer = renderer;
            ShouldAbort = shouldAbort;
        }

        internal GpuDevice? Device { get; }

        internal GpuSpriteRenderer? Renderer { get; }

        internal bool ShouldAbort { get; }

        public void Dispose()
        {
            Renderer?.Dispose();
            Device?.Dispose();
        }

        internal static NpcGpuRenderResources Create(NpcRenderSettings settings)
        {
            if (settings.Wireframe)
            {
                if (settings.ForceGpu)
                {
                    AnsiConsole.MarkupLine(
                        "[yellow]Wireframe overlay currently uses the CPU renderer; ignoring --gpu[/]");
                }

                AnsiConsole.MarkupLine(
                    "Using [yellow]CPU software renderer[/] (--wireframe)");
                return new NpcGpuRenderResources(null, null, false);
            }

            if (settings.ForceCpu)
            {
                AnsiConsole.MarkupLine(
                    "Using [yellow]CPU software renderer[/] (--cpu)");
                return new NpcGpuRenderResources(null, null, false);
            }

            var device = GpuDevice.Create();
            if (device == null)
            {
                if (settings.ForceGpu)
                {
                    AnsiConsole.MarkupLine(
                        "[red]Error:[/] --gpu specified but no GPU backend available");
                    return new NpcGpuRenderResources(null, null, true);
                }

                AnsiConsole.MarkupLine(
                    "GPU not available -- using [yellow]CPU software renderer[/]");
                return new NpcGpuRenderResources(null, null, false);
            }

            var renderer = new GpuSpriteRenderer(device);
            AnsiConsole.MarkupLine(
                "GPU rendering: [green]{0}[/] ({1})",
                device.Backend,
                device.Device.DeviceName);
            return new NpcGpuRenderResources(device, renderer, false);
        }
    }
}
