using System.CommandLine;
using System.Numerics;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Bsa;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI;

/// <summary>
///     CLI command for rendering NPC sprites from BSA + ESM data.
///     Delegates model building to NpcHeadBuilder and NpcBodyBuilder.
/// </summary>
public static class RenderNpcCommand
{
    private static readonly Logger Log = Logger.Instance;

    public static Command Create()
    {
        var command = new Command("npc", "Render NPC head sprites from BSA + ESM data");

        var inputArg = new Argument<string>("meshes-bsa") { Description = "Path to meshes BSA file" };
        var esmOption = new Option<string>("--esm") { Description = "Path to ESM file", Required = true };
        var texturesBsaOption = new Option<string?>("--textures-bsa")
            { Description = "Path to textures BSA file (auto-detected from meshes BSA directory if omitted)" };
        var outputOption = new Option<string>("-o", "--output")
            { Description = "Output directory for sprites", Required = true };
        var npcOption = new Option<string[]?>("--npc")
        {
            Description = "Render specific NPCs by FormID or EditorID (e.g., --npc 0x00104C0C --npc CraigBoone)",
            AllowMultipleArgumentsPerToken = true
        };
        var sizeOption = new Option<int>("--size")
            { Description = "Sprite size in pixels (longest edge)", DefaultValueFactory = _ => 512 };
        var verboseOption = new Option<bool>("-v", "--verbose")
            { Description = "Show debug output (bone transforms, EGM details, bounds)" };
        var dmpOption = new Option<string?>("--dmp")
            { Description = "Path to Xbox 360 memory dump (.dmp) — uses DMP-sourced FaceGen coefficients" };
        var exportEgtOption = new Option<bool>("--export-egt")
            { Description = "Export EGT debug textures (native + upscaled deltas) to output dir" };
        var noBilinearOption = new Option<bool>("--no-bilinear")
            { Description = "Use nearest-neighbor instead of bilinear for EGT upscaling" };
        var noEgmOption = new Option<bool>("--no-egm")
            { Description = "Skip EGM mesh morphing (debug: isolate texture issues)" };
        var noEgtOption = new Option<bool>("--no-egt")
            { Description = "Skip EGT texture morphing (debug: isolate mesh issues)" };
        var noBumpOption = new Option<bool>("--no-bump") { Description = "Disable normal map / bump mapping" };
        var noTexOption = new Option<bool>("--no-tex")
            { Description = "Replace textures with flat white (debug: show lighting only)" };
        var bumpStrengthOption = new Option<float?>("--bump-strength")
            { Description = "Normal map bump strength (0=flat, 1=full, default 0.5)" };
        var headOnlyOption = new Option<bool>("--head-only") { Description = "Render head only (legacy mode)" };
        var noEquipOption = new Option<bool>("--no-equip") { Description = "Render full body but skip equipment" };
        var gpuOption = new Option<bool>("--gpu") { Description = "Force GPU rendering (Vulkan/D3D11)" };
        var cpuOption = new Option<bool>("--cpu") { Description = "Force CPU software rendering" };
        var skeletonOption = new Option<bool>("--skeleton")
            { Description = "Render skeleton bones only (debug visualization)" };
        var bindPoseOption = new Option<bool>("--bind-pose")
            { Description = "Use bind pose (T-pose) instead of idle animation" };

        command.Arguments.Add(inputArg);
        command.Options.Add(esmOption);
        command.Options.Add(texturesBsaOption);
        command.Options.Add(outputOption);
        command.Options.Add(npcOption);
        command.Options.Add(sizeOption);
        command.Options.Add(verboseOption);
        command.Options.Add(dmpOption);
        command.Options.Add(exportEgtOption);
        command.Options.Add(noBilinearOption);
        command.Options.Add(noEgmOption);
        command.Options.Add(noEgtOption);
        command.Options.Add(noBumpOption);
        command.Options.Add(noTexOption);
        command.Options.Add(bumpStrengthOption);
        command.Options.Add(headOnlyOption);
        command.Options.Add(noEquipOption);
        command.Options.Add(gpuOption);
        command.Options.Add(cpuOption);
        command.Options.Add(skeletonOption);
        command.Options.Add(bindPoseOption);

        command.SetAction((parseResult, _) =>
        {
            Log.SetVerbose(parseResult.GetValue(verboseOption));

            var settings = new NpcRenderSettings
            {
                MeshesBsaPath = parseResult.GetValue(inputArg)!,
                EsmPath = parseResult.GetValue(esmOption)!,
                ExplicitTexturesBsaPath = parseResult.GetValue(texturesBsaOption),
                OutputDir = parseResult.GetValue(outputOption)!,
                NpcFilters = parseResult.GetValue(npcOption),
                SpriteSize = parseResult.GetValue(sizeOption),
                DmpPath = parseResult.GetValue(dmpOption),
                ExportEgt = parseResult.GetValue(exportEgtOption),
                NoBilinear = parseResult.GetValue(noBilinearOption),
                NoEgm = parseResult.GetValue(noEgmOption),
                NoEgt = parseResult.GetValue(noEgtOption),
                NoBump = parseResult.GetValue(noBumpOption),
                NoTex = parseResult.GetValue(noTexOption),
                BumpStrength = parseResult.GetValue(bumpStrengthOption),
                HeadOnly = parseResult.GetValue(headOnlyOption),
                NoEquip = parseResult.GetValue(noEquipOption),
                ForceGpu = parseResult.GetValue(gpuOption),
                ForceCpu = parseResult.GetValue(cpuOption),
                Skeleton = parseResult.GetValue(skeletonOption),
                BindPose = parseResult.GetValue(bindPoseOption)
            };

            Run(settings);
            return Task.CompletedTask;
        });

        return command;
    }

    private static void Run(NpcRenderSettings s)
    {
        if (!File.Exists(s.MeshesBsaPath))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Meshes BSA not found: {0}", s.MeshesBsaPath);
            return;
        }

        if (!File.Exists(s.EsmPath))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] ESM file not found: {0}", s.EsmPath);
            return;
        }

        var texturesBsaPaths = NpcRenderHelpers.ResolveTexturesBsaPaths(s.MeshesBsaPath, s.ExplicitTexturesBsaPath);
        if (texturesBsaPaths.Length == 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No texture BSA files found");
            return;
        }

        if (s.DmpPath != null && !File.Exists(s.DmpPath))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] DMP file not found: {0}", s.DmpPath);
            return;
        }

        Directory.CreateDirectory(s.OutputDir);

        // Configure rendering options
        if (s.ExportEgt)
        {
            var egtDir = Path.Combine(s.OutputDir, "egt_debug");
            FaceGenTextureMorpher.DebugExportDir = egtDir;
            AnsiConsole.MarkupLine("EGT debug export enabled → [cyan]{0}[/]", egtDir);
        }
        else
        {
            FaceGenTextureMorpher.DebugExportDir = null;
        }

        NifSpriteRenderer.DisableBilinear = s.NoBilinear;
        if (s.NoBilinear) AnsiConsole.MarkupLine("Texture bilinear sampling [yellow]disabled[/]");

        NifSpriteRenderer.DisableBumpMapping = s.NoBump;
        if (s.NoBump) AnsiConsole.MarkupLine("Normal map / bump mapping [yellow]disabled[/]");

        NifSpriteRenderer.DisableTextures = s.NoTex;
        if (s.NoTex) AnsiConsole.MarkupLine("Textures [yellow]disabled[/] (flat white lighting only)");

        if (s.BumpStrength.HasValue)
        {
            NifSpriteRenderer.BumpStrength = s.BumpStrength.Value;
            AnsiConsole.MarkupLine("Bump strength set to [cyan]{0:F2}[/]", s.BumpStrength.Value);
        }

        // Load ESM
        AnsiConsole.MarkupLine("Loading ESM: [cyan]{0}[/]", Path.GetFileName(s.EsmPath));
        var esm = EsmFileLoader.Load(s.EsmPath, false);
        if (esm == null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Failed to load ESM file");
            return;
        }

        // Build NPC appearance resolver
        AnsiConsole.MarkupLine("Scanning NPC_ and RACE records...");
        var resolver = NpcAppearanceResolver.Build(esm.Data, esm.IsBigEndian);
        AnsiConsole.MarkupLine("Found [green]{0}[/] NPCs, [green]{1}[/] races", resolver.NpcCount, resolver.RaceCount);

        // Parse meshes BSA
        AnsiConsole.MarkupLine("Parsing meshes BSA: [cyan]{0}[/]", Path.GetFileName(s.MeshesBsaPath));
        var meshesArchive = BsaParser.Parse(s.MeshesBsaPath);

        foreach (var tp in texturesBsaPaths)
            AnsiConsole.MarkupLine("Loading textures BSA: [cyan]{0}[/]", Path.GetFileName(tp));
        using var textureResolver = new NifTextureResolver(texturesBsaPaths);

        var pluginName = Path.GetFileName(s.EsmPath);

        // Resolve appearances
        var appearances = ResolveAppearances(s, resolver, pluginName);
        if (appearances == null)
            return;

        // Caches
        var headMeshCache = new Dictionary<string, NifRenderableModel?>(StringComparer.OrdinalIgnoreCase);
        var egmCache = new Dictionary<string, EgmParser?>(StringComparer.OrdinalIgnoreCase);
        var egtCache = new Dictionary<string, EgtParser?>(StringComparer.OrdinalIgnoreCase);

        using var meshExtractor = new BsaExtractor(s.MeshesBsaPath);
        var rendered = 0;
        var skipped = 0;
        var failed = 0;

        // Initialize GPU renderer
        GpuDevice? gpuDevice = null;
        GpuSpriteRenderer? gpuRenderer = null;
        if (!s.ForceCpu)
        {
            gpuDevice = GpuDevice.Create();
            if (gpuDevice != null)
            {
                gpuRenderer = new GpuSpriteRenderer(gpuDevice);
                AnsiConsole.MarkupLine("GPU rendering: [green]{0}[/] ({1})",
                    gpuDevice.Backend, gpuDevice.Device.DeviceName);
            }
            else if (s.ForceGpu)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] --gpu specified but no GPU backend available");
                return;
            }
            else
            {
                AnsiConsole.MarkupLine("GPU not available — using [yellow]CPU software renderer[/]");
            }
        }
        else
        {
            AnsiConsole.MarkupLine("Using [yellow]CPU software renderer[/] (--cpu)");
        }

        Dictionary<string, Matrix4x4>? skeletonBoneCache = null;
        Dictionary<string, Matrix4x4>? poseDeltaCache = null;

        if (gpuRenderer != null)
        {
            RenderNpcsPipelinedGpu(appearances, gpuRenderer, meshesArchive, meshExtractor,
                textureResolver, headMeshCache, egmCache, egtCache, ref skeletonBoneCache,
                ref poseDeltaCache, s, ref rendered, ref skipped, ref failed);
        }
        else
        {
            foreach (var npc in appearances)
            {
                try
                {
                    var result = s.HeadOnly
                        ? RenderNpcHead(npc, meshesArchive, meshExtractor, textureResolver,
                            headMeshCache, egmCache, egtCache, s)
                        : RenderNpcFullBody(npc, meshesArchive, meshExtractor, textureResolver,
                            headMeshCache, egmCache, egtCache, ref skeletonBoneCache,
                            ref poseDeltaCache, s);

                    SaveNpcResult(npc, result, s, appearances.Count, ref rendered, ref skipped, ref failed);
                }
                catch (Exception ex)
                {
                    failed++;
                    AnsiConsole.MarkupLine("[red]FAIL:[/] 0x{0:X8} {1}: {2}",
                        npc.NpcFormId, npc.EditorId ?? "?", Markup.Escape(ex.Message));
                }
                finally
                {
                    var npcTexKey = $"facegen_egt\\{npc.NpcFormId:X8}.dds";
                    textureResolver.EvictTexture(npcTexKey);
                }
            }
        }

        AnsiConsole.MarkupLine("\nRendered: [green]{0}[/]  Skipped: [yellow]{1}[/]  Failed: [red]{2}[/]",
            rendered, skipped, failed);

        gpuRenderer?.Dispose();
        gpuDevice?.Dispose();
    }

    private static List<NpcAppearance>? ResolveAppearances(
        NpcRenderSettings s, NpcAppearanceResolver resolver, string pluginName)
    {
        if (s.DmpPath != null)
        {
            var dmpFormId = s.NpcFilters != null ? NpcRenderHelpers.ParseFormId(s.NpcFilters.FirstOrDefault()) : null;
            var appearances = NpcRenderHelpers.ResolveFromDmp(s.DmpPath, resolver, pluginName, dmpFormId);
            if (appearances.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No NPCs resolved from DMP[/]");
                return null;
            }

            return appearances;
        }

        if (s.NpcFilters is { Length: > 0 })
        {
            var allAppearances = resolver.ResolveAllHeadOnly(pluginName);
            var formIdSet = new HashSet<uint>();
            var editorIdSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var filter in s.NpcFilters)
            {
                var formId = NpcRenderHelpers.ParseFormId(filter);
                if (formId.HasValue)
                    formIdSet.Add(formId.Value);
                else
                    editorIdSet.Add(filter.Trim());
            }

            var filtered = allAppearances
                .Where(a => formIdSet.Contains(a.NpcFormId) ||
                            (a.EditorId != null && editorIdSet.Contains(a.EditorId)))
                .ToList();

            if (filtered.Count == 0)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] None of the specified NPCs found in ESM");
                AnsiConsole.MarkupLine("  Filters: {0}", string.Join(", ", s.NpcFilters));
                return null;
            }

            AnsiConsole.MarkupLine("Matched [green]{0}[/] NPCs from {1} filter(s)",
                filtered.Count, s.NpcFilters.Length);
            return filtered;
        }

        var all = resolver.ResolveAllHeadOnly(pluginName, true);
        AnsiConsole.MarkupLine("Resolved [green]{0}[/] named NPCs", all.Count);
        return all;
    }

    /// <summary>
    ///     GPU pipelined render loop: overlaps GPU render of NPC[i] with CPU model build of NPC[i+1].
    /// </summary>
    private static void RenderNpcsPipelinedGpu(
        List<NpcAppearance> appearances,
        GpuSpriteRenderer gpuRenderer,
        BsaArchive meshesArchive, BsaExtractor meshExtractor,
        NifTextureResolver textureResolver,
        Dictionary<string, NifRenderableModel?> headMeshCache,
        Dictionary<string, EgmParser?> egmCache,
        Dictionary<string, EgtParser?> egtCache,
        ref Dictionary<string, Matrix4x4>? skeletonBoneCache,
        ref Dictionary<string, Matrix4x4>? poseDeltaCache,
        NpcRenderSettings s,
        ref int rendered, ref int skipped, ref int failed)
    {
        var elevationDeg = s.HeadOnly ? 0f : 5f;
        var azimuthDeg = 90f;

        NifRenderableModel? currentModel = null;
        if (appearances.Count > 0)
        {
            currentModel = BuildNpcModel(appearances[0], meshesArchive, meshExtractor,
                textureResolver, headMeshCache, egmCache, egtCache, ref skeletonBoneCache,
                ref poseDeltaCache, s);
        }

        for (var i = 0; i < appearances.Count; i++)
        {
            var npc = appearances[i];
            NifRenderableModel? nextModel = null;
            try
            {
                GpuSpriteRenderer.PendingRender? pending = null;
                if (currentModel != null && currentModel.HasGeometry)
                {
                    pending = gpuRenderer.SubmitRender(currentModel, textureResolver,
                        1.0f, 32, s.SpriteSize,
                        azimuthDeg, elevationDeg,
                        s.SpriteSize);
                }

                if (i + 1 < appearances.Count)
                {
                    nextModel = BuildNpcModel(appearances[i + 1], meshesArchive, meshExtractor,
                        textureResolver, headMeshCache, egmCache, egtCache, ref skeletonBoneCache,
                        ref poseDeltaCache, s);
                }

                var result = pending != null ? gpuRenderer.CompleteRender(pending) : null;
                SaveNpcResult(npc, result, s, appearances.Count, ref rendered, ref skipped, ref failed);
            }
            catch (Exception ex)
            {
                failed++;
                AnsiConsole.MarkupLine("[red]FAIL:[/] 0x{0:X8} {1}: {2}",
                    npc.NpcFormId, npc.EditorId ?? "?", Markup.Escape(ex.Message));
            }
            finally
            {
                var npcTexKey = $"facegen_egt\\{npc.NpcFormId:X8}.dds";
                textureResolver.EvictTexture(npcTexKey);
                gpuRenderer.EvictTexture(npcTexKey);
                currentModel = nextModel;
            }
        }
    }

    private static NifRenderableModel? BuildNpcModel(
        NpcAppearance npc,
        BsaArchive meshesArchive, BsaExtractor meshExtractor,
        NifTextureResolver textureResolver,
        Dictionary<string, NifRenderableModel?> headMeshCache,
        Dictionary<string, EgmParser?> egmCache,
        Dictionary<string, EgtParser?> egtCache,
        ref Dictionary<string, Matrix4x4>? skeletonBoneCache,
        ref Dictionary<string, Matrix4x4>? poseDeltaCache,
        NpcRenderSettings s)
    {
        if (s.HeadOnly)
        {
            return NpcHeadBuilder.Build(npc, meshesArchive, meshExtractor, textureResolver,
                headMeshCache, egmCache, egtCache, s);
        }

        return NpcBodyBuilder.Build(npc, meshesArchive, meshExtractor, textureResolver,
            headMeshCache, egmCache, egtCache, ref skeletonBoneCache, ref poseDeltaCache, s);
    }

    private static SpriteResult? RenderNpcHead(
        NpcAppearance npc,
        BsaArchive meshesArchive, BsaExtractor meshExtractor,
        NifTextureResolver textureResolver,
        Dictionary<string, NifRenderableModel?> headMeshCache,
        Dictionary<string, EgmParser?> egmCache,
        Dictionary<string, EgtParser?> egtCache,
        NpcRenderSettings s)
    {
        var model = NpcHeadBuilder.Build(npc, meshesArchive, meshExtractor, textureResolver,
            headMeshCache, egmCache, egtCache, s);
        if (model == null || !model.HasGeometry)
            return null;

        return NifSpriteRenderer.Render(model, textureResolver, 1.0f, 32, s.SpriteSize, 90f, 0f, s.SpriteSize);
    }

    private static SpriteResult? RenderNpcFullBody(
        NpcAppearance npc,
        BsaArchive meshesArchive, BsaExtractor meshExtractor,
        NifTextureResolver textureResolver,
        Dictionary<string, NifRenderableModel?> headMeshCache,
        Dictionary<string, EgmParser?> egmCache,
        Dictionary<string, EgtParser?> egtCache,
        ref Dictionary<string, Matrix4x4>? skeletonBoneCache,
        ref Dictionary<string, Matrix4x4>? poseDeltaCache,
        NpcRenderSettings s)
    {
        var bodyModel = NpcBodyBuilder.Build(npc, meshesArchive, meshExtractor, textureResolver,
            headMeshCache, egmCache, egtCache, ref skeletonBoneCache, ref poseDeltaCache, s);
        if (bodyModel == null)
            return null;

        return NifSpriteRenderer.Render(bodyModel, textureResolver, 1.0f, 32, s.SpriteSize, 90f, 5f, s.SpriteSize);
    }

    private static void SaveNpcResult(
        NpcAppearance npc, SpriteResult? result, NpcRenderSettings s, int totalCount,
        ref int rendered, ref int skipped, ref int failed)
    {
        if (result == null)
        {
            skipped++;
            if (s.NpcFilters != null)
            {
                AnsiConsole.MarkupLine("[yellow]Skipped:[/] 0x{0:X8} {1} — no geometry",
                    npc.NpcFormId, npc.FullName ?? npc.EditorId ?? "unknown");
            }

            return;
        }

        var name = npc.EditorId ?? $"{npc.NpcFormId:X8}";
        var fileName = $"{name}.png";
        var outputPath = Path.Combine(s.OutputDir, fileName);

        var expectedLen = result.Width * result.Height * 4;
        if (result.Pixels.Length != expectedLen)
        {
            failed++;
            AnsiConsole.MarkupLine(
                "[red]FAIL:[/] 0x{0:X8} {1}: pixel buffer mismatch ({2} bytes, expected {3} for {4}x{5})",
                npc.NpcFormId, npc.EditorId ?? "?", result.Pixels.Length, expectedLen, result.Width, result.Height);
            return;
        }

        PngWriter.SaveRgba(result.Pixels, result.Width, result.Height, outputPath);
        rendered++;

        if (s.NpcFilters != null || totalCount <= 20)
        {
            AnsiConsole.MarkupLine("[green]OK:[/] 0x{0:X8} {1} → {2} ({3}x{4})",
                npc.NpcFormId, npc.FullName ?? "?", fileName, result.Width, result.Height);
        }
    }

    internal sealed class NpcRenderSettings
    {
        public required string MeshesBsaPath { get; init; }
        public required string EsmPath { get; init; }
        public string? ExplicitTexturesBsaPath { get; init; }
        public required string OutputDir { get; init; }
        public string[]? NpcFilters { get; init; }
        public int SpriteSize { get; init; } = 512;
        public string? DmpPath { get; init; }
        public bool ExportEgt { get; init; }
        public bool NoBilinear { get; init; }
        public bool NoEgm { get; init; }
        public bool NoEgt { get; init; }
        public bool NoBump { get; init; }
        public bool NoTex { get; init; }
        public float? BumpStrength { get; init; }
        public bool HeadOnly { get; init; }
        public bool NoEquip { get; init; }
        public bool ForceGpu { get; init; }
        public bool ForceCpu { get; init; }
        public bool Skeleton { get; init; }
        public bool BindPose { get; init; }
    }
}
