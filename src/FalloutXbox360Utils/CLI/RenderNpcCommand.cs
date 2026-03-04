using System.CommandLine;
using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Bsa;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis;
using FalloutXbox360Utils.Core.Formats.Nif;
using FalloutXbox360Utils.Core.Formats.Nif.Conversion;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu;
using FalloutXbox360Utils.Core.Minidump;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI;

/// <summary>
///     CLI command for rendering NPC head sprites from BSA + ESM data.
///     Phase 2: Base head mesh + EGM morph application.
/// </summary>
public static class RenderNpcCommand
{
    private static readonly Logger Log = Logger.Instance;
    public static Command Create()
    {
        var command = new Command("npc", "Render NPC head sprites from BSA + ESM data");

        var inputArg = new Argument<string>("meshes-bsa") { Description = "Path to meshes BSA file" };
        var esmOption = new Option<string>("--esm") { Description = "Path to ESM file", Required = true };
        var texturesBsaOption = new Option<string?>("--textures-bsa") { Description = "Path to textures BSA file (auto-detected from meshes BSA directory if omitted)" };
        var outputOption = new Option<string>("-o", "--output") { Description = "Output directory for sprites", Required = true };
        var npcOption = new Option<string[]?>("--npc") { Description = "Render specific NPCs by FormID or EditorID (e.g., --npc 0x00104C0C --npc CraigBoone)", AllowMultipleArgumentsPerToken = true };
        var sizeOption = new Option<int>("--size") { Description = "Sprite size in pixels (longest edge)", DefaultValueFactory = _ => 512 };
        var verboseOption = new Option<bool>("-v", "--verbose") { Description = "Show debug output (bone transforms, EGM details, bounds)" };
        var dmpOption = new Option<string?>("--dmp") { Description = "Path to Xbox 360 memory dump (.dmp) — uses DMP-sourced FaceGen coefficients" };
        var exportEgtOption = new Option<bool>("--export-egt") { Description = "Export EGT debug textures (native + upscaled deltas) to output dir" };
        var noBilinearOption = new Option<bool>("--no-bilinear") { Description = "Use nearest-neighbor instead of bilinear for EGT upscaling" };
        var noEgmOption = new Option<bool>("--no-egm") { Description = "Skip EGM mesh morphing (debug: isolate texture issues)" };
        var noEgtOption = new Option<bool>("--no-egt") { Description = "Skip EGT texture morphing (debug: isolate mesh issues)" };
        var noBumpOption = new Option<bool>("--no-bump") { Description = "Disable normal map / bump mapping" };
        var noTexOption = new Option<bool>("--no-tex") { Description = "Replace textures with flat white (debug: show lighting only)" };
        var bumpStrengthOption = new Option<float?>("--bump-strength") { Description = "Normal map bump strength (0=flat, 1=full, default 0.5)" };
        var headOnlyOption = new Option<bool>("--head-only") { Description = "Render head only (legacy mode)" };
        var noEquipOption = new Option<bool>("--no-equip") { Description = "Render full body but skip equipment" };
        var gpuOption = new Option<bool>("--gpu") { Description = "Force GPU rendering (Vulkan/D3D11)" };
        var cpuOption = new Option<bool>("--cpu") { Description = "Force CPU software rendering" };

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
                ForceCpu = parseResult.GetValue(cpuOption)
            };

            Run(settings);
            return Task.CompletedTask;
        });

        return command;
    }

    private static void Run(NpcRenderSettings s)
    {
        // Validate inputs
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

        // Resolve texture BSA paths: explicit or auto-discovered from meshes BSA directory
        var texturesBsaPaths = ResolveTexturesBsaPaths(s.MeshesBsaPath, s.ExplicitTexturesBsaPath);
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

        // Enable EGT debug export if requested
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
        if (s.NoBilinear)
            AnsiConsole.MarkupLine("Texture bilinear sampling [yellow]disabled[/] (nearest-neighbor)");

        NifSpriteRenderer.DisableBumpMapping = s.NoBump;
        if (s.NoBump)
            AnsiConsole.MarkupLine("Normal map / bump mapping [yellow]disabled[/]");

        NifSpriteRenderer.DisableTextures = s.NoTex;
        if (s.NoTex)
            AnsiConsole.MarkupLine("Textures [yellow]disabled[/] (flat white lighting only)");

        if (s.BumpStrength.HasValue)
        {
            NifSpriteRenderer.BumpStrength = s.BumpStrength.Value;
            AnsiConsole.MarkupLine("Bump strength set to [cyan]{0:F2}[/]", s.BumpStrength.Value);
        }

        // Load ESM
        AnsiConsole.MarkupLine("Loading ESM: [cyan]{0}[/]", Path.GetFileName(s.EsmPath));
        var esm = EsmFileLoader.Load(s.EsmPath, printStatus: false);
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

        // Create texture resolver (searches BSAs in reverse order so higher-numbered BSAs override)
        foreach (var tp in texturesBsaPaths)
            AnsiConsole.MarkupLine("Loading textures BSA: [cyan]{0}[/]", Path.GetFileName(tp));
        using var textureResolver = new NifTextureResolver(texturesBsaPaths);

        // Determine plugin name for FaceGen paths
        var pluginName = Path.GetFileName(s.EsmPath);

        // Resolve appearances — either from DMP or ESM
        List<NpcAppearance> appearances;
        if (s.DmpPath != null)
        {
            var dmpFormId = s.NpcFilters != null ? ParseFormId(s.NpcFilters.FirstOrDefault()) : null;
            appearances = ResolveFromDmp(s.DmpPath, resolver, pluginName, dmpFormId);
            if (appearances.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No NPCs resolved from DMP[/]");
                return;
            }
        }
        else if (s.NpcFilters is { Length: > 0 })
        {
            // Resolve all NPCs first, then filter by FormID or EditorID
            var allAppearances = resolver.ResolveAllHeadOnly(pluginName, filterNamed: false);
            var formIdSet = new HashSet<uint>();
            var editorIdSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var filter in s.NpcFilters)
            {
                var formId = ParseFormId(filter);
                if (formId.HasValue)
                    formIdSet.Add(formId.Value);
                else
                    editorIdSet.Add(filter.Trim());
            }

            appearances = allAppearances
                .Where(a => formIdSet.Contains(a.NpcFormId) ||
                            (a.EditorId != null && editorIdSet.Contains(a.EditorId)))
                .ToList();

            if (appearances.Count == 0)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] None of the specified NPCs found in ESM");
                AnsiConsole.MarkupLine("  Filters: {0}", string.Join(", ", s.NpcFilters));
                return;
            }

            AnsiConsole.MarkupLine("Matched [green]{0}[/] NPCs from {1} filter(s)",
                appearances.Count, s.NpcFilters.Length);
        }
        else
        {
            appearances = resolver.ResolveAllHeadOnly(pluginName, filterNamed: true);
            AnsiConsole.MarkupLine("Resolved [green]{0}[/] named NPCs", appearances.Count);
        }

        // Cache head meshes, EGM morphs, and EGT morphs per race/gender to avoid re-extracting
        var headMeshCache = new Dictionary<string, NifRenderableModel?>(StringComparer.OrdinalIgnoreCase);
        var egmCache = new Dictionary<string, EgmParser?>(StringComparer.OrdinalIgnoreCase);
        var egtCache = new Dictionary<string, EgtParser?>(StringComparer.OrdinalIgnoreCase);

        using var meshExtractor = new BsaExtractor(s.MeshesBsaPath);
        var rendered = 0;
        var skipped = 0;
        var failed = 0;

        // Initialize GPU renderer (unless forced CPU)
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

        // Cache skeleton bone transforms for full-body rendering
        Dictionary<string, System.Numerics.Matrix4x4>? skeletonBoneCache = null;

        if (gpuRenderer != null)
        {
            // GPU pipelined path: build NPC[i+1]'s model while GPU renders NPC[i].
            // After SubmitRender(), GPU executes asynchronously — the CPU builds the next
            // NPC's model (EGT morph, mesh load, etc.) before calling CompleteRender().
            // This overlaps ~5-15ms GPU work with ~200-400ms CPU work per NPC.
            RenderNpcsPipelinedGpu(appearances, gpuRenderer, meshesArchive, meshExtractor,
                textureResolver, headMeshCache, egmCache, egtCache, ref skeletonBoneCache, s,
                ref rendered, ref skipped, ref failed);
        }
        else
        {
            // CPU sequential path
            foreach (var npc in appearances)
            {
                try
                {
                    SpriteResult? result;
                    if (s.HeadOnly)
                    {
                        result = RenderNpcHead(npc, meshesArchive, meshExtractor, textureResolver,
                            headMeshCache, egmCache, egtCache, s);
                    }
                    else
                    {
                        result = RenderNpcFullBody(npc, meshesArchive, meshExtractor, textureResolver,
                            headMeshCache, egmCache, egtCache, ref skeletonBoneCache, s);
                    }

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

        // Cleanup GPU resources
        gpuRenderer?.Dispose();
        gpuDevice?.Dispose();
    }

    /// <summary>
    ///     GPU pipelined render loop: overlaps GPU render of NPC[i] with CPU model build of NPC[i+1].
    ///     After SubmitRender(), GPU executes asynchronously. The CPU immediately starts building
    ///     the next NPC's model (EGT morph, mesh load, hair, etc.). By the time CompleteRender()
    ///     is called, the GPU is typically already finished (~5ms GPU vs ~200ms CPU build).
    /// </summary>
    private static void RenderNpcsPipelinedGpu(
        List<NpcAppearance> appearances,
        GpuSpriteRenderer gpuRenderer,
        BsaArchive meshesArchive,
        BsaExtractor meshExtractor,
        NifTextureResolver textureResolver,
        Dictionary<string, NifRenderableModel?> headMeshCache,
        Dictionary<string, EgmParser?> egmCache,
        Dictionary<string, EgtParser?> egtCache,
        ref Dictionary<string, System.Numerics.Matrix4x4>? skeletonBoneCache,
        NpcRenderSettings s,
        ref int rendered, ref int skipped, ref int failed)
    {
        var elevationDeg = s.HeadOnly ? 0f : 5f;
        var azimuthDeg = 90f;

        // Build first NPC model
        NifRenderableModel? currentModel = null;
        if (appearances.Count > 0)
        {
            currentModel = BuildNpcModel(appearances[0], meshesArchive, meshExtractor,
                textureResolver, headMeshCache, egmCache, egtCache, ref skeletonBoneCache, s);
        }

        for (var i = 0; i < appearances.Count; i++)
        {
            var npc = appearances[i];
            NifRenderableModel? nextModel = null;
            try
            {
                // Submit GPU render for current NPC (non-blocking after submit)
                GpuSpriteRenderer.PendingRender? pending = null;
                if (currentModel != null && currentModel.HasGeometry)
                {
                    pending = gpuRenderer.SubmitRender(currentModel, textureResolver,
                        pixelsPerUnit: 1.0f, minSize: 32, maxSize: s.SpriteSize,
                        azimuthDeg: azimuthDeg, elevationDeg: elevationDeg,
                        fixedSize: s.SpriteSize);
                }

                // While GPU executes: build next NPC's model (CPU overlaps with GPU)
                if (i + 1 < appearances.Count)
                {
                    nextModel = BuildNpcModel(appearances[i + 1], meshesArchive, meshExtractor,
                        textureResolver, headMeshCache, egmCache, egtCache, ref skeletonBoneCache, s);
                }

                // Complete GPU render (GPU should be done by now — typically <5ms vs ~200ms build)
                SpriteResult? result = pending != null ? gpuRenderer.CompleteRender(pending) : null;
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

                // Advance pipeline: use pre-built next model, or build fresh if it failed
                currentModel = nextModel;
            }
        }
    }

    /// <summary>
    ///     Builds either a head-only or full-body NPC model (without rendering).
    /// </summary>
    private static NifRenderableModel? BuildNpcModel(
        NpcAppearance npc,
        BsaArchive meshesArchive,
        BsaExtractor meshExtractor,
        NifTextureResolver textureResolver,
        Dictionary<string, NifRenderableModel?> headMeshCache,
        Dictionary<string, EgmParser?> egmCache,
        Dictionary<string, EgtParser?> egtCache,
        ref Dictionary<string, System.Numerics.Matrix4x4>? skeletonBoneCache,
        NpcRenderSettings s)
    {
        if (s.HeadOnly)
        {
            return BuildNpcHeadModel(npc, meshesArchive, meshExtractor, textureResolver,
                headMeshCache, egmCache, egtCache, s);
        }

        return BuildNpcFullBodyModel(npc, meshesArchive, meshExtractor, textureResolver,
            headMeshCache, egmCache, egtCache, ref skeletonBoneCache, s);
    }

    /// <summary>
    ///     Validates and saves an NPC render result, updating counters.
    /// </summary>
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

        // Validate pixel data before PNG write
        var expectedLen = result.Width * result.Height * 4;
        if (result.Pixels.Length != expectedLen)
        {
            failed++;
            AnsiConsole.MarkupLine("[red]FAIL:[/] 0x{0:X8} {1}: pixel buffer mismatch ({2} bytes, expected {3} for {4}x{5})",
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

    /// <summary>
    ///     Loads NPC records from a DMP file and resolves their appearance using ESM asset data.
    /// </summary>
    private static List<NpcAppearance> ResolveFromDmp(
        string dmpPath, NpcAppearanceResolver resolver, string pluginName, uint? filterFormId)
    {
        if (!File.Exists(dmpPath))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] DMP file not found: {0}", dmpPath);
            return [];
        }

        AnsiConsole.MarkupLine("Loading DMP: [cyan]{0}[/]", Path.GetFileName(dmpPath));
        var fileInfo = new FileInfo(dmpPath);

        using var mmf = MemoryMappedFile.CreateFromFile(dmpPath, FileMode.Open, null, 0,
            MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);

        // Parse minidump header
        var minidumpInfo = MinidumpParser.Parse(dmpPath);
        if (!minidumpInfo.IsValid)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Invalid minidump format");
            return [];
        }

        // Scan for runtime Editor IDs (walks pAllForms hash table)
        var scanResult = EsmRecordScanner.ScanForRecordsMemoryMapped(accessor, fileInfo.Length);
        EsmEditorIdExtractor.ExtractRuntimeEditorIds(accessor, fileInfo.Length, minidumpInfo, scanResult, false);

        // Filter NPC_ entries (FormType 0x2A)
        var npcEntries = scanResult.RuntimeEditorIds
            .Where(e => e.FormType == 0x2A)
            .ToList();

        if (npcEntries.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No NPC_ entries found in DMP runtime hash table[/]");
            return [];
        }

        AnsiConsole.MarkupLine("Found [green]{0}[/] NPC_ entries in DMP", npcEntries.Count);

        // Read NPC structs from DMP memory
        var structReader = new RuntimeStructReader(accessor, fileInfo.Length, minidumpInfo);
        var appearances = new List<NpcAppearance>();

        foreach (var entry in npcEntries)
        {
            if (filterFormId.HasValue && entry.FormId != filterFormId.Value)
                continue;

            var npcRecord = structReader.ReadRuntimeNpc(entry);
            if (npcRecord == null)
            {
                Log.Debug("Failed to read NPC struct for 0x{0:X8} ({1})", entry.FormId, entry.EditorId);
                continue;
            }

            var appearance = resolver.ResolveFromDmpRecord(npcRecord, pluginName);
            if (appearance == null)
            {
                Log.Debug("Failed to resolve appearance for 0x{0:X8} ({1})", entry.FormId, entry.EditorId);
                continue;
            }

            // Coefficient comparison diagnostic (verbose)
            CompareWithEsmCoefficients(resolver, appearance, npcRecord, pluginName);

            appearances.Add(appearance);
        }

        AnsiConsole.MarkupLine("Resolved [green]{0}[/] NPC appearances from DMP", appearances.Count);
        return appearances;
    }

    /// <summary>
    ///     Compares DMP-sourced coefficients against ESM-sourced coefficients for validation.
    ///     Only emits output when verbose logging is enabled.
    /// </summary>
    private static void CompareWithEsmCoefficients(
        NpcAppearanceResolver resolver, NpcAppearance dmpAppearance,
        Core.Formats.Esm.Models.NpcRecord npcRecord, string pluginName)
    {
        var esmAppearance = resolver.ResolveHeadOnly(npcRecord.FormId, pluginName);
        if (esmAppearance == null)
        {
            Log.Debug("NPC 0x{0:X8} ({1}): not found in ESM — no coefficient comparison",
                npcRecord.FormId, npcRecord.EditorId ?? "?");
            return;
        }

        var fggsMatch = CountMatches(dmpAppearance.FaceGenSymmetricCoeffs, esmAppearance.FaceGenSymmetricCoeffs);
        var fggaMatch = CountMatches(dmpAppearance.FaceGenAsymmetricCoeffs, esmAppearance.FaceGenAsymmetricCoeffs);
        var fgtsMatch = CountMatches(dmpAppearance.FaceGenTextureCoeffs, esmAppearance.FaceGenTextureCoeffs);

        Log.Debug("NPC 0x{0:X8} ({1}): FGGS match={2}/{3}, FGGA match={4}/{5}, FGTS match={6}/{7}",
            npcRecord.FormId, npcRecord.EditorId ?? "?",
            fggsMatch.matched, fggsMatch.total,
            fggaMatch.matched, fggaMatch.total,
            fgtsMatch.matched, fgtsMatch.total);
    }

    private static (int matched, int total) CountMatches(float[]? a, float[]? b)
    {
        if (a == null && b == null) return (0, 0);
        if (a == null || b == null) return (0, Math.Max(a?.Length ?? 0, b?.Length ?? 0));

        var total = Math.Min(a.Length, b.Length);
        var matched = 0;
        for (var i = 0; i < total; i++)
        {
            if (Math.Abs(a[i] - b[i]) < 0.001f)
                matched++;
        }

        return (matched, total);
    }

    private static SpriteResult? RenderNpcHead(
        NpcAppearance npc,
        BsaArchive meshesArchive,
        BsaExtractor meshExtractor,
        NifTextureResolver textureResolver,
        Dictionary<string, NifRenderableModel?> headMeshCache,
        Dictionary<string, EgmParser?> egmCache,
        Dictionary<string, EgtParser?> egtCache,
        NpcRenderSettings s,
        GpuSpriteRenderer? gpuRenderer = null)
    {
        var model = BuildNpcHeadModel(npc, meshesArchive, meshExtractor, textureResolver,
            headMeshCache, egmCache, egtCache, s);
        if (model == null || !model.HasGeometry)
            return null;

        // Render front view (azimuth 90° = camera at +Y looking toward face)
        if (gpuRenderer != null)
        {
            return gpuRenderer.Render(model, textureResolver,
                pixelsPerUnit: 1.0f, minSize: 32, maxSize: s.SpriteSize,
                azimuthDeg: 90f, elevationDeg: 0f,
                fixedSize: s.SpriteSize);
        }

        return NifSpriteRenderer.Render(model, textureResolver,
            pixelsPerUnit: 1.0f, minSize: 32, maxSize: s.SpriteSize,
            azimuthDeg: 90f, elevationDeg: 0f,
            fixedSize: s.SpriteSize);
    }

    /// <summary>
    ///     Builds the composited head model (head + hair + eyes + head parts)
    ///     without rendering. Used by both head-only and full-body render paths.
    /// </summary>
    private static NifRenderableModel? BuildNpcHeadModel(
        NpcAppearance npc,
        BsaArchive meshesArchive,
        BsaExtractor meshExtractor,
        NifTextureResolver textureResolver,
        Dictionary<string, NifRenderableModel?> headMeshCache,
        Dictionary<string, EgmParser?> egmCache,
        Dictionary<string, EgtParser?> egtCache,
        NpcRenderSettings s,
        string? hairFilterOverride = null)
    {
        // Try to load the base head mesh from the race (primary path)
        NifRenderableModel? model = null;
        var headTexturePath = npc.HeadDiffuseOverride;
        var usedBaseRaceMesh = false;
        Dictionary<string, System.Numerics.Matrix4x4>? headBoneTransforms = null;

        if (npc.BaseHeadNifPath != null)
        {
            // Cache key: race head mesh path (shared by all NPCs of same race/gender)
            var cacheKey = npc.BaseHeadNifPath;

            if (!headMeshCache.TryGetValue(cacheKey, out var cached))
            {
                cached = LoadNifFromBsa(npc.BaseHeadNifPath, meshesArchive, meshExtractor, textureResolver);
                headMeshCache[cacheKey] = cached;
            }

            if (cached != null)
            {
                // Deep-clone positions to avoid mutating shared cache when morphs are applied
                model = DeepCloneModel(cached);
                usedBaseRaceMesh = true;

                // Extract bone transforms from head NIF for positioning attachments (hair, eyes)
                var headRaw = LoadNifRawFromBsa(npc.BaseHeadNifPath, meshesArchive, meshExtractor);
                if (headRaw != null)
                {
                    headBoneTransforms = NifGeometryExtractor.ExtractNamedBoneTransforms(
                        headRaw.Value.Data, headRaw.Value.Info);

                    if (headBoneTransforms.Count == 0)
                    {
                        Log.Warn("Head NIF has 0 named bone transforms: {0}", npc.BaseHeadNifPath);
                    }
                }
                else
                {
                    Log.Warn("Failed to load raw head NIF for bone extraction: {0}", npc.BaseHeadNifPath);
                }
            }
        }

        // Fallback: try per-NPC FaceGen mesh (already pre-morphed, skip EGM)
        if (model == null && npc.FaceGenNifPath != null)
        {
            model = LoadNifFromBsa(npc.FaceGenNifPath, meshesArchive, meshExtractor, textureResolver);
        }

        if (model == null || !model.HasGeometry)
            return null;

        // Apply EGM morphs only when using the base race mesh (FaceGen fallback is pre-morphed)
        if (usedBaseRaceMesh && npc.BaseHeadNifPath != null &&
            (npc.FaceGenSymmetricCoeffs != null || npc.FaceGenAsymmetricCoeffs != null))
        {
            var egmPath = Path.ChangeExtension(npc.BaseHeadNifPath, ".egm");

            if (!egmCache.TryGetValue(egmPath, out var egm))
            {
                egm = LoadEgmFromBsa(egmPath, meshesArchive, meshExtractor);
                egmCache[egmPath] = egm;
            }

            if (egm != null && !s.NoEgm)
            {
                FaceGenMeshMorpher.Apply(model, egm,
                    npc.FaceGenSymmetricCoeffs, npc.FaceGenAsymmetricCoeffs);
            }
        }

        // Apply head texture override from RACE INDX 0 ICON
        string? fullTexPath = null;
        if (headTexturePath != null)
        {
            fullTexPath = "textures\\" + headTexturePath;
            foreach (var submesh in model.Submeshes)
            {
                submesh.DiffuseTexturePath = fullTexPath;
            }
        }

        // Apply EGT texture morphs (only when using base race mesh and have FGTS coefficients)
        if (!s.NoEgt && usedBaseRaceMesh && npc.BaseHeadNifPath != null &&
            npc.FaceGenTextureCoeffs != null && fullTexPath != null)
        {
            var egtPath = Path.ChangeExtension(npc.BaseHeadNifPath, ".egt");

            if (!egtCache.TryGetValue(egtPath, out var egt))
            {
                egt = LoadEgtFromBsa(egtPath, meshesArchive, meshExtractor);
                egtCache[egtPath] = egt;
            }

            if (egt != null)
            {
                // Set debug label for EGT export (uses EditorID or FormID)
                FaceGenTextureMorpher.DebugLabel = npc.EditorId ?? $"{npc.NpcFormId:X8}";

                // Load the base texture, apply EGT morphs, inject per-NPC result
                var baseTexture = textureResolver.GetTexture(fullTexPath);
                if (baseTexture != null)
                {
                    var morphed = FaceGenTextureMorpher.Apply(baseTexture, egt, npc.FaceGenTextureCoeffs);
                    if (morphed != null)
                    {
                        // Export base and morphed face textures for comparison
                        if (s.ExportEgt)
                        {
                            var egtDir = Path.Combine(s.OutputDir, "egt_debug");
                            var label = npc.EditorId ?? $"{npc.NpcFormId:X8}";
                            PngWriter.SaveRgba(baseTexture.Pixels, baseTexture.Width, baseTexture.Height,
                                Path.Combine(egtDir, $"{label}_base_{baseTexture.Width}x{baseTexture.Height}.png"));
                            PngWriter.SaveRgba(morphed.Pixels, morphed.Width, morphed.Height,
                                Path.Combine(egtDir, $"{label}_morphed_{morphed.Width}x{morphed.Height}.png"));
                        }

                        // Inject under a unique per-NPC key so it doesn't pollute the shared cache
                        var npcTexKey = $"facegen_egt\\{npc.NpcFormId:X8}.dds";
                        textureResolver.InjectTexture(npcTexKey, morphed);
                        foreach (var submesh in model.Submeshes)
                        {
                            submesh.DiffuseTexturePath = npcTexKey;
                        }
                    }
                    else
                    {
                        Log.Warn("EGT texture morph returned null for NPC 0x{0:X8} (base texture: {1})",
                            npc.NpcFormId, fullTexPath);
                    }
                }
                else
                {
                    Log.Warn("Base head texture not found for EGT morph: {0}", fullTexPath);
                }
            }
        }

        Log.Debug("Head bounds: ({0:F2}, {1:F2}, {2:F2}) → ({3:F2}, {4:F2}, {5:F2})",
            model.MinX, model.MinY, model.MinZ, model.MaxX, model.MaxY, model.MaxZ);

        // Load and attach hair mesh
        if (npc.HairNifPath != null)
        {
            // Hair NIFs contain both "NoHat" (full hair) and "Hat" (trimmed for headgear) shapes.
            // Pass filterShapeName: "NoHat" to keep only the no-hat variant.
            // Hair styles with only one shape (no hat/nohat naming) keep all shapes.
            var hairBaseName = Path.GetFileNameWithoutExtension(npc.HairNifPath);
            var hairDir = Path.GetDirectoryName(npc.HairNifPath) ?? "";

            var hairModel = LoadNifFromBsa(npc.HairNifPath, meshesArchive, meshExtractor, textureResolver,
                filterShapeName: hairFilterOverride ?? "NoHat");
            if (hairModel != null && hairModel.HasGeometry)
            {
                // Hair NIFs are NOT skinned — vertices are in Bip01 Head local space.
                // Engine pipeline (AttachHairToHead, VA 0x8248BC58):
                //   1. Apply -π/2 Y-axis rotation to hair local transform (NiMatrix3::MakeYRotation)
                //   2. Attach hair as child of head NiNode → inherits Bip01 Head world transform
                // In our pipeline, head mesh is already in world space after skinning, so we only
                // need to translate hair to the Bip01 Head bone position. The engine's -π/2 rotation
                // + bone rotation compose to identity relative to our coordinate frame.
                if (headBoneTransforms != null &&
                    headBoneTransforms.TryGetValue("Bip01 Head", out var headBoneMatrix))
                {
                    var offset = headBoneMatrix.Translation;
                    Log.Debug("Offsetting hair by Bip01 Head: ({0:F4}, {1:F4}, {2:F4})",
                        offset.X, offset.Y, offset.Z);
                    foreach (var sub in hairModel.Submeshes)
                    {
                        for (var i = 0; i < sub.Positions.Length; i += 3)
                        {
                            sub.Positions[i] += offset.X;
                            sub.Positions[i + 1] += offset.Y;
                            sub.Positions[i + 2] += offset.Z;
                        }
                    }
                }
                else
                {
                    Log.Warn("'Bip01 Head' bone not found — hair will render at origin. " +
                             "Available bones: {0}",
                        headBoneTransforms != null
                            ? string.Join(", ", headBoneTransforms.Keys)
                            : "(headBoneTransforms is null)");
                }

                // Apply same EGM morphs to hair (engine uses same FGGS/FGGA coefficients)
                if (usedBaseRaceMesh &&
                    (npc.FaceGenSymmetricCoeffs != null || npc.FaceGenAsymmetricCoeffs != null))
                {
                    // Hair EGMs use "{name}nohat.egm" / "{name}hat.egm" naming, not "{name}.egm"
                    var egmSuffix = hairFilterOverride == "Hat" ? "hat.egm" : "nohat.egm";
                    var hairEgmPath = Path.Combine(hairDir, hairBaseName + egmSuffix);

                    if (!egmCache.TryGetValue(hairEgmPath, out var hairEgm))
                    {
                        hairEgm = LoadEgmFromBsa(hairEgmPath, meshesArchive, meshExtractor);
                        egmCache[hairEgmPath] = hairEgm;
                    }

                    if (hairEgm != null)
                    {
                        Log.Debug("Hair EGM '{0}': {1} sym + {2} asym morphs, {3} vertices",
                            hairEgmPath, hairEgm.SymmetricMorphs.Length,
                            hairEgm.AsymmetricMorphs.Length, hairEgm.VertexCount);
                        FaceGenMeshMorpher.Apply(hairModel, hairEgm,
                            npc.FaceGenSymmetricCoeffs, npc.FaceGenAsymmetricCoeffs);
                    }
                }

                // Merge hair submeshes into head model, injecting alpha property where needed
                // Unpack HCLR hair color tint (0x00BBGGRR → float RGB)
                var hairTint = UnpackHairColor(npc.HairColor);

                foreach (var sub in hairModel.Submeshes)
                {
                    // Engine injects NiAlphaProperty on hair if slot 0 is empty
                    if (!sub.HasAlphaBlend && !sub.HasAlphaTest)
                    {
                        sub.HasAlphaBlend = true;
                        sub.HasAlphaTest = true;
                        sub.AlphaTestThreshold = 0;
                    }

                    sub.TintColor = hairTint;

                    // Override diffuse texture with HAIR record ICON (per-style texture).
                    // Different hair styles sharing the same NIF differentiate via ICON texture
                    // (e.g., HairBalding uses HairBase.NIF but HairBalding.dds texture).
                    if (npc.HairTexturePath != null)
                        sub.DiffuseTexturePath = npc.HairTexturePath;

                    // Engine renders hair AFTER head (scene graph child order).
                    // RenderOrder=1 ensures hair triangles sort after all head triangles.
                    sub.RenderOrder = 1;
                    model.Submeshes.Add(sub);
                    model.ExpandBounds(sub.Positions);
                }
            }
            else
            {
                Log.Warn("Hair NIF failed to load or has no geometry: {0}", npc.HairNifPath);
            }
        }

        // Load and attach eye meshes (left and right independently)
        // Eye NIFs are in Bip01 Head local space — use same bone offset as hair.
        // Head NIF's truncated skeleton doesn't include eye-specific bones.
        Log.Debug("Eyes: L={0}, R={1}, texture={2}",
            npc.LeftEyeNifPath ?? "(none)", npc.RightEyeNifPath ?? "(none)",
            npc.EyeTexturePath ?? "(none — no ENAM/EYES)");
        AttachEyeMesh(npc.LeftEyeNifPath, "Bip01 Head", npc, model, headBoneTransforms,
            usedBaseRaceMesh, meshesArchive, meshExtractor, textureResolver, egmCache);
        AttachEyeMesh(npc.RightEyeNifPath, "Bip01 Head", npc, model, headBoneTransforms,
            usedBaseRaceMesh, meshesArchive, meshExtractor, textureResolver, egmCache);

        // Load and attach head part meshes (eyebrows, beards, teeth, etc. from PNAM → HDPT)
        if (npc.HeadPartNifPaths != null)
        {
            foreach (var partPath in npc.HeadPartNifPaths)
            {
                var partModel = LoadNifFromBsa(partPath, meshesArchive, meshExtractor, textureResolver);
                if (partModel == null || !partModel.HasGeometry)
                {
                    Log.Warn("Head part NIF failed to load: {0}", partPath);
                    continue;
                }

                // Head parts are NOT skinned — vertices are in Bip01 Head local space.
                // Engine attaches as child of head NiNode; we translate by bone position.
                if (headBoneTransforms != null &&
                    headBoneTransforms.TryGetValue("Bip01 Head", out var headBone))
                {
                    var offset = headBone.Translation;
                    foreach (var sub in partModel.Submeshes)
                    {
                        for (var i = 0; i < sub.Positions.Length; i += 3)
                        {
                            sub.Positions[i] += offset.X;
                            sub.Positions[i + 1] += offset.Y;
                            sub.Positions[i + 2] += offset.Z;
                        }
                    }
                }

                // Apply EGM morphs (same FGGS/FGGA coefficients as head)
                if (usedBaseRaceMesh &&
                    (npc.FaceGenSymmetricCoeffs != null || npc.FaceGenAsymmetricCoeffs != null))
                {
                    var egmPath = Path.ChangeExtension(partPath, ".egm");
                    if (!egmCache.TryGetValue(egmPath, out var egm))
                    {
                        egm = LoadEgmFromBsa(egmPath, meshesArchive, meshExtractor);
                        egmCache[egmPath] = egm;
                    }

                    if (egm != null)
                    {
                        FaceGenMeshMorpher.Apply(partModel, egm,
                            npc.FaceGenSymmetricCoeffs, npc.FaceGenAsymmetricCoeffs);
                    }
                }

                // Merge into head model. RenderOrder=0 (same group as head, before hair).
                // Engine attaches head parts as children of the head NiNode, so they render
                // in the same pass as the head mesh, before hair. This ensures hair covers
                // any DXT edge fringe on eyebrow textures (prevents black line artifacts).
                var partTint = UnpackHairColor(npc.HairColor);
                foreach (var sub in partModel.Submeshes)
                {
                    Log.Info("HeadPart '{0}' sub: tex={1}, alphaTest={2} func={3} thresh={4}, " +
                        "alphaBlend={5}, matAlpha={6:F2}, vcol={7}, doubleSided={8}, verts={9}",
                        partPath, sub.DiffuseTexturePath ?? "(none)",
                        sub.HasAlphaTest, sub.AlphaTestFunction, sub.AlphaTestThreshold,
                        sub.HasAlphaBlend, sub.MaterialAlpha, sub.UseVertexColors,
                        sub.IsDoubleSided, sub.VertexCount);
                    sub.TintColor = partTint;
                    sub.RenderOrder = 0;
                    model.Submeshes.Add(sub);
                    model.ExpandBounds(sub.Positions);
                }
            }
        }

        return model;
    }

    /// <summary>
    ///     Renders an NPC full body: skeleton + body meshes + head + equipment.
    /// </summary>
    /// <summary>
    ///     Builds the composited full-body model (body + equipment + head) without rendering.
    /// </summary>
    private static NifRenderableModel? BuildNpcFullBodyModel(
        NpcAppearance npc,
        BsaArchive meshesArchive,
        BsaExtractor meshExtractor,
        NifTextureResolver textureResolver,
        Dictionary<string, NifRenderableModel?> headMeshCache,
        Dictionary<string, EgmParser?> egmCache,
        Dictionary<string, EgtParser?> egtCache,
        ref Dictionary<string, System.Numerics.Matrix4x4>? skeletonBoneCache,
        NpcRenderSettings s)
    {
        // Load skeleton bone transforms (cached across NPCs — same skeleton for all humans)
        if (skeletonBoneCache == null && npc.SkeletonNifPath != null)
        {
            var skelRaw = LoadNifRawFromBsa(npc.SkeletonNifPath, meshesArchive, meshExtractor);
            if (skelRaw != null)
            {
                skeletonBoneCache = NifGeometryExtractor.ExtractNamedBoneTransforms(
                    skelRaw.Value.Data, skelRaw.Value.Info);
                Log.Debug("Skeleton loaded: {0} bones from {1}",
                    skeletonBoneCache.Count, npc.SkeletonNifPath);
            }
            else
            {
                Log.Warn("Failed to load skeleton: {0}", npc.SkeletonNifPath);
            }
        }

        var skeletonBones = skeletonBoneCache;

        // Determine which body slots are covered by equipment
        var coveredSlots = 0u;
        if (!s.NoEquip && npc.EquippedItems != null)
        {
            foreach (var item in npc.EquippedItems)
                coveredSlots |= item.BipedFlags;
        }

        Log.Debug("NPC 0x{0:X8} ({1}): coveredSlots=0x{2:X}, equipment={3}, bodyTex={4}, upperBody={5}",
            npc.NpcFormId, npc.EditorId ?? "?", coveredSlots,
            npc.EquippedItems != null ? string.Join(", ", npc.EquippedItems.Select(e => e.MeshPath)) : "(none)",
            npc.BodyTexturePath ?? "(null)", npc.UpperBodyNifPath ?? "(null)");

        // Build composited body model
        var bodyModel = new NifRenderableModel();

        // Pre-compute EGT-morphed body/hand textures — needed for both exposed body parts
        // AND skin submeshes embedded in equipment NIFs (e.g., Fiend armor showing arms/chest).
        // Compute these before the coverage check so they're available regardless of equipment.
        var effectiveBodyTex = npc.BodyTexturePath;
        var effectiveHandTex = npc.HandTexturePath;
        if (!s.NoEgt && npc.FaceGenTextureCoeffs != null)
        {
            if (npc.BodyEgtPath != null && npc.BodyTexturePath != null)
            {
                var key = ApplyBodyEgtMorph(npc.BodyEgtPath, npc.BodyTexturePath,
                    npc.FaceGenTextureCoeffs, npc.NpcFormId, "upperbody",
                    meshesArchive, meshExtractor, textureResolver, egtCache);
                if (key != null) effectiveBodyTex = key;
            }

            if (npc.LeftHandEgtPath != null && npc.HandTexturePath != null)
            {
                var key = ApplyBodyEgtMorph(npc.LeftHandEgtPath, npc.HandTexturePath,
                    npc.FaceGenTextureCoeffs, npc.NpcFormId, "lefthand",
                    meshesArchive, meshExtractor, textureResolver, egtCache);
                if (key != null) effectiveHandTex = key;
            }

            // Right hand: different EGT morph but same base texture as left hand
            if (npc.RightHandEgtPath != null && npc.HandTexturePath != null)
            {
                ApplyBodyEgtMorph(npc.RightHandEgtPath, npc.HandTexturePath,
                    npc.FaceGenTextureCoeffs, npc.NpcFormId, "righthand",
                    meshesArchive, meshExtractor, textureResolver, egtCache);
            }
        }

        // Load body parts only if not covered by equipment
        if ((coveredSlots & 0x04) == 0 && npc.UpperBodyNifPath != null)
        {
            LoadAndMergeBodyPart(npc.UpperBodyNifPath, effectiveBodyTex, 0,
                meshesArchive, meshExtractor, textureResolver, skeletonBones, bodyModel);
        }

        if ((coveredSlots & 0x08) == 0 && npc.LeftHandNifPath != null)
        {
            LoadAndMergeBodyPart(npc.LeftHandNifPath, effectiveHandTex, 0,
                meshesArchive, meshExtractor, textureResolver, skeletonBones, bodyModel);
        }

        if ((coveredSlots & 0x10) == 0 && npc.RightHandNifPath != null)
        {
            LoadAndMergeBodyPart(npc.RightHandNifPath, effectiveHandTex, 0,
                meshesArchive, meshExtractor, textureResolver, skeletonBones, bodyModel);
        }

        // Load equipment meshes (replace body parts they cover)
        // Head equipment may be unskinned — detect and apply head bone transform.
        const uint headEquipFlags = 0x01 | 0x02 | 0x200 | 0x400 | 0x800 | 0x4000;
        // Head | Hair | Headband | Hat | EyeGlasses | Mask
        if (!s.NoEquip && npc.EquippedItems != null)
        {
            foreach (var item in npc.EquippedItems)
            {
                var isHeadEquip = (item.BipedFlags & headEquipFlags) != 0;
                var equipModel = LoadNifFromBsa(item.MeshPath, meshesArchive, meshExtractor,
                    textureResolver, externalBoneTransforms: skeletonBones);
                if (equipModel == null || !equipModel.HasGeometry)
                {
                    Log.Warn("Equipment NIF failed to load: {0}", item.MeshPath);
                    continue;
                }

                // Detect unskinned head equipment: if max Z < 30 it wasn't positioned by skeleton
                // skinning (head bone is at Z ~112). Apply Bip01 Head translation to position correctly.
                // Head equipment NIFs (glasses, hats) are in the same coordinate convention as hair —
                // the engine's -π/2 Y rotation + bone rotation compose to identity, so only translation
                // is needed (same pattern as hair positioning in BuildNpcHeadModel).
                if (isHeadEquip && equipModel.MaxZ < 30f && skeletonBones != null &&
                    skeletonBones.TryGetValue("Bip01 Head", out var headBoneMatrix))
                {
                    var offset = headBoneMatrix.Translation;
                    Log.Debug("Repositioning unskinned head equipment '{0}' via Bip01 Head offset: ({1:F4}, {2:F4}, {3:F4})",
                        item.MeshPath, offset.X, offset.Y, offset.Z);
                    foreach (var sub in equipModel.Submeshes)
                    {
                        for (var i = 0; i < sub.Positions.Length; i += 3)
                        {
                            sub.Positions[i] += offset.X;
                            sub.Positions[i + 1] += offset.Y;
                            sub.Positions[i + 2] += offset.Z;
                        }
                    }
                }

                Log.Debug("Equipment '{0}': {1} submeshes, bounds ({2:F2},{3:F2},{4:F2})→({5:F2},{6:F2},{7:F2})",
                    item.MeshPath, equipModel.Submeshes.Count,
                    equipModel.MinX, equipModel.MinY, equipModel.MinZ,
                    equipModel.MaxX, equipModel.MaxY, equipModel.MaxZ);

                foreach (var sub in equipModel.Submeshes)
                {
                    // Apply body skin tint to skin submeshes within equipment NIFs.
                    // Equipment armor (raider/fiend gear etc.) often includes exposed skin submeshes
                    // that reference the default body/hand texture and need race-specific tinting.
                    // Body skin textures live under "characters\_male\" or "characters\_female\".
                    // Don't override hair/glasses textures ("characters\hair\"), underwear,
                    // null paths (glass lenses), or armor/clothing textures.
                    if (effectiveBodyTex != null &&
                        IsEquipmentSkinSubmesh(sub.DiffuseTexturePath))
                    {
                        sub.DiffuseTexturePath = sub.DiffuseTexturePath!.Contains("hand", StringComparison.OrdinalIgnoreCase)
                            ? effectiveHandTex ?? effectiveBodyTex
                            : effectiveBodyTex;
                    }

                    sub.RenderOrder = 5;
                    bodyModel.Submeshes.Add(sub);
                    bodyModel.ExpandBounds(sub.Positions);
                }
            }
        }

        // Build head model with hat-aware hair filtering.
        // Use "Hat" variant for any head-covering equipment (Head, Hat, or Mask biped slots).
        var hasHat = (coveredSlots & (0x01 | 0x400 | 0x4000)) != 0;
        var hairFilter = hasHat ? "Hat" : null;

        var headModel = BuildNpcHeadModel(npc, meshesArchive, meshExtractor, textureResolver,
            headMeshCache, egmCache, egtCache, s, hairFilterOverride: hairFilter);

        if (headModel != null && headModel.HasGeometry)
        {
            foreach (var sub in headModel.Submeshes)
            {
                sub.RenderOrder += 1;
                bodyModel.Submeshes.Add(sub);
                bodyModel.ExpandBounds(sub.Positions);
            }
        }

        return bodyModel.HasGeometry ? bodyModel : null;
    }

    private static SpriteResult? RenderNpcFullBody(
        NpcAppearance npc,
        BsaArchive meshesArchive,
        BsaExtractor meshExtractor,
        NifTextureResolver textureResolver,
        Dictionary<string, NifRenderableModel?> headMeshCache,
        Dictionary<string, EgmParser?> egmCache,
        Dictionary<string, EgtParser?> egtCache,
        ref Dictionary<string, System.Numerics.Matrix4x4>? skeletonBoneCache,
        NpcRenderSettings s,
        GpuSpriteRenderer? gpuRenderer = null)
    {
        var bodyModel = BuildNpcFullBodyModel(npc, meshesArchive, meshExtractor, textureResolver,
            headMeshCache, egmCache, egtCache, ref skeletonBoneCache, s);
        if (bodyModel == null)
            return null;

        // Render front view for full body
        if (gpuRenderer != null)
        {
            return gpuRenderer.Render(bodyModel, textureResolver,
                pixelsPerUnit: 1.0f, minSize: 32, maxSize: s.SpriteSize,
                azimuthDeg: 90f, elevationDeg: 5f,
                fixedSize: s.SpriteSize);
        }

        return NifSpriteRenderer.Render(bodyModel, textureResolver,
            pixelsPerUnit: 1.0f, minSize: 32, maxSize: s.SpriteSize,
            azimuthDeg: 90f, elevationDeg: 5f,
            fixedSize: s.SpriteSize);
    }

    /// <summary>
    ///     Loads a body part or equipment NIF with skeleton-driven skinning and merges into target model.
    /// </summary>
    private static void LoadAndMergeBodyPart(
        string nifPath,
        string? textureOverride,
        int renderOrder,
        BsaArchive meshesArchive,
        BsaExtractor meshExtractor,
        NifTextureResolver textureResolver,
        Dictionary<string, System.Numerics.Matrix4x4>? skeletonBones,
        NifRenderableModel targetModel)
    {
        var partModel = LoadNifFromBsa(nifPath, meshesArchive, meshExtractor, textureResolver,
            externalBoneTransforms: skeletonBones);
        if (partModel == null || !partModel.HasGeometry)
        {
            Log.Warn("Body part NIF failed to load: {0}", nifPath);
            return;
        }

        Log.Debug("Body part '{0}': {1} submeshes, bounds ({2:F2},{3:F2},{4:F2})→({5:F2},{6:F2},{7:F2})",
            nifPath, partModel.Submeshes.Count,
            partModel.MinX, partModel.MinY, partModel.MinZ,
            partModel.MaxX, partModel.MaxY, partModel.MaxZ);

        foreach (var sub in partModel.Submeshes)
        {
            // Only override textures on skin submeshes (not underwear, hands, or other parts).
            // Body NIFs have skin submeshes referencing "characters\male\*" or "characters\female\*",
            // and underwear submeshes referencing "armor\underwear\*". The RACE body texture should
            // only replace the skin texture — underwear and hands keep their NIF-embedded textures.
            if (textureOverride != null && ShouldApplyBodyTextureOverride(sub.DiffuseTexturePath, textureOverride))
                sub.DiffuseTexturePath = textureOverride;
            sub.RenderOrder = renderOrder;
            targetModel.Submeshes.Add(sub);
            targetModel.ExpandBounds(sub.Positions);
        }
    }

    /// <summary>
    ///     Loads a body EGT from BSA (with cache), morphs the base texture using FaceGen texture
    ///     coefficients, and injects the result into the texture resolver under a unique per-NPC key.
    ///     Returns the injected key, or null if any step fails.
    /// </summary>
    private static string? ApplyBodyEgtMorph(
        string egtPath,
        string baseTexturePath,
        float[] textureCoeffs,
        uint npcFormId,
        string partLabel,
        BsaArchive meshesArchive,
        BsaExtractor meshExtractor,
        NifTextureResolver textureResolver,
        Dictionary<string, EgtParser?> egtCache)
    {
        if (!egtCache.TryGetValue(egtPath, out var egt))
        {
            egt = LoadEgtFromBsa(egtPath, meshesArchive, meshExtractor);
            egtCache[egtPath] = egt;
        }

        if (egt == null)
            return null;

        var baseTexture = textureResolver.GetTexture(baseTexturePath);
        if (baseTexture == null)
        {
            Log.Warn("Base body texture not found for EGT morph: {0}", baseTexturePath);
            return null;
        }

        var morphed = FaceGenTextureMorpher.Apply(baseTexture, egt, textureCoeffs);
        if (morphed == null)
        {
            Log.Warn("Body EGT morph returned null for NPC 0x{0:X8} (part: {1})", npcFormId, partLabel);
            return null;
        }

        var morphedKey = $"body_egt\\{npcFormId:X8}_{partLabel}.dds";
        textureResolver.InjectTexture(morphedKey, morphed);
        Log.Debug("Body EGT morph applied: NPC 0x{0:X8} {1} → {2}", npcFormId, partLabel, egtPath);
        return morphedKey;
    }

    /// <summary>
    ///     Determines whether an equipment submesh is a body skin submesh that needs tinting.
    ///     Body skin textures reference "upperbody" or "hand" filenames under various "characters"
    ///     subdirectories. Male paths use "characters\_male\", female paths use "characters\female\".
    ///     Hair, glasses, eyes, and head textures are NOT body skin.
    /// </summary>
    private static bool IsEquipmentSkinSubmesh(string? texturePath)
    {
        if (string.IsNullOrEmpty(texturePath))
            return false;

        // Body skin textures live under "characters\_male\", "characters\male\",
        // "characters\_female\", or "characters\female\" directories.
        // Exclude known non-skin paths: hair, eyes, headhuman, underwear.
        if (texturePath.Contains("hair", StringComparison.OrdinalIgnoreCase) ||
            texturePath.Contains("eyes", StringComparison.OrdinalIgnoreCase) ||
            texturePath.Contains("headhuman", StringComparison.OrdinalIgnoreCase) ||
            texturePath.Contains("underwear", StringComparison.OrdinalIgnoreCase))
            return false;

        // Match body/hand texture paths: "characters\_male\", "characters\male\",
        // "characters\_female\", "characters\female\"
        return texturePath.Contains("characters\\_male", StringComparison.OrdinalIgnoreCase) ||
               texturePath.Contains("characters\\male", StringComparison.OrdinalIgnoreCase) ||
               texturePath.Contains("characters\\_female", StringComparison.OrdinalIgnoreCase) ||
               texturePath.Contains("characters\\female", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Determines whether the RACE body texture override should replace a submesh's texture.
    ///     Returns true for skin submeshes and false for underwear or other non-skin parts.
    ///     The RACE body texture (from ICON at body INDX 0) replaces the default body skin texture
    ///     but should not replace underwear or other distinct textures embedded in the body NIF.
    /// </summary>
    private static bool ShouldApplyBodyTextureOverride(string? existingPath, string overridePath)
    {
        if (string.IsNullOrEmpty(existingPath))
            return true;

        // Underwear submeshes reference textures in "armor\underwear\" — never override these
        if (existingPath.Contains("underwear", StringComparison.OrdinalIgnoreCase))
            return false;

        // Skin submeshes reference textures containing "UpperBody" or "Body" in "characters\" paths
        // (e.g., characters\male\UpperBodyMale.dds, characters\female\UpperBodyFemale.dds).
        // Override these with the RACE-specific body texture.
        if (existingPath.Contains("characters", StringComparison.OrdinalIgnoreCase))
            return true;

        // Unknown texture — don't override to avoid breaking custom submeshes
        return false;
    }

    /// <summary>
    ///     Unpacks HCLR hair color (0x00BBGGRR) into a float RGB tint tuple.
    ///     Returns null if no hair color is set.
    /// </summary>
    private static (float R, float G, float B)? UnpackHairColor(uint? hclr)
    {
        if (hclr == null)
            return null;

        var v = hclr.Value;
        var r = (v & 0xFF) / 255f;
        var g = ((v >> 8) & 0xFF) / 255f;
        var b = ((v >> 16) & 0xFF) / 255f;
        return (r, g, b);
    }

    private static EgmParser? LoadEgmFromBsa(string bsaPath, BsaArchive archive, BsaExtractor extractor)
    {
        var fileRecord = archive.FindFile(bsaPath);
        if (fileRecord == null)
        {
            Log.Warn("EGM not found in BSA: {0}", bsaPath);
            return null;
        }

        var data = extractor.ExtractFile(fileRecord);
        if (data.Length == 0)
        {
            Log.Warn("EGM extracted but empty (0 bytes): {0}", bsaPath);
            return null;
        }

        return EgmParser.Parse(data);
    }

    private static EgtParser? LoadEgtFromBsa(string bsaPath, BsaArchive archive, BsaExtractor extractor)
    {
        var fileRecord = archive.FindFile(bsaPath);
        if (fileRecord == null)
        {
            Log.Warn("EGT not found in BSA: {0}", bsaPath);
            return null;
        }

        var data = extractor.ExtractFile(fileRecord);
        if (data.Length == 0)
        {
            Log.Warn("EGT extracted but empty (0 bytes): {0}", bsaPath);
            return null;
        }

        return EgtParser.Parse(data);
    }

    /// <summary>
    ///     Loads an eye NIF, positions it via bone transform, applies EGM morphs,
    ///     overrides eye texture, and merges into the head model.
    /// </summary>
    private static void AttachEyeMesh(
        string? eyeNifPath,
        string boneName,
        NpcAppearance npc,
        NifRenderableModel model,
        Dictionary<string, System.Numerics.Matrix4x4>? headBoneTransforms,
        bool usedBaseRaceMesh,
        BsaArchive meshesArchive,
        BsaExtractor meshExtractor,
        NifTextureResolver textureResolver,
        Dictionary<string, EgmParser?> egmCache)
    {
        if (eyeNifPath == null)
            return;

        var eyeModel = LoadNifFromBsa(eyeNifPath, meshesArchive, meshExtractor, textureResolver);
        if (eyeModel == null || !eyeModel.HasGeometry)
        {
            Log.Warn("Eye NIF failed to load or has no geometry: {0}", eyeNifPath);
            return;
        }

        Log.Debug("Eye '{0}' bounds (local): ({1:F2}, {2:F2}, {3:F2}) → ({4:F2}, {5:F2}, {6:F2})",
            eyeNifPath, eyeModel.MinX, eyeModel.MinY, eyeModel.MinZ,
            eyeModel.MaxX, eyeModel.MaxY, eyeModel.MaxZ);

        // Eye NIFs are NOT skinned — transform from eye-NIF-local space to world space.
        // The engine attaches eye NiNode as child of head NiNode, so:
        //   world_pos = head_bone_world_matrix × eye_NIF_pos
        // We must apply the FULL bone matrix (rotation + translation), not just translation,
        // because the eye vertex offsets from bone center are directional (forward/up/left-right)
        // and the bone rotation maps these directions to world axes.
        if (headBoneTransforms != null &&
            headBoneTransforms.TryGetValue(boneName, out var eyeBoneMatrix))
        {
            var t = eyeBoneMatrix.Translation;
            Log.Debug("Transforming eye by {0} matrix: T=({1:F4}, {2:F4}, {3:F4}), " +
                      "R0=({4:F4}, {5:F4}, {6:F4}), R1=({7:F4}, {8:F4}, {9:F4}), R2=({10:F4}, {11:F4}, {12:F4})",
                boneName, t.X, t.Y, t.Z,
                eyeBoneMatrix.M11, eyeBoneMatrix.M12, eyeBoneMatrix.M13,
                eyeBoneMatrix.M21, eyeBoneMatrix.M22, eyeBoneMatrix.M23,
                eyeBoneMatrix.M31, eyeBoneMatrix.M32, eyeBoneMatrix.M33);
            foreach (var sub in eyeModel.Submeshes)
            {
                for (var i = 0; i < sub.Positions.Length; i += 3)
                {
                    var v = System.Numerics.Vector3.Transform(
                        new System.Numerics.Vector3(sub.Positions[i], sub.Positions[i + 1], sub.Positions[i + 2]),
                        eyeBoneMatrix);
                    sub.Positions[i] = v.X;
                    sub.Positions[i + 1] = v.Y;
                    sub.Positions[i + 2] = v.Z;
                }
            }
        }
        else
        {
            Log.Warn("'{0}' bone not found — eye will render at origin. Available bones: {1}",
                boneName,
                headBoneTransforms != null
                    ? string.Join(", ", headBoneTransforms.Keys)
                    : "(headBoneTransforms is null)");
        }

        // Apply EGM morphs to eye (same FGGS/FGGA coefficients as head)
        if (usedBaseRaceMesh &&
            (npc.FaceGenSymmetricCoeffs != null || npc.FaceGenAsymmetricCoeffs != null))
        {
            var eyeEgmPath = Path.ChangeExtension(eyeNifPath, ".egm");

            if (!egmCache.TryGetValue(eyeEgmPath, out var eyeEgm))
            {
                eyeEgm = LoadEgmFromBsa(eyeEgmPath, meshesArchive, meshExtractor);
                egmCache[eyeEgmPath] = eyeEgm;
            }

            if (eyeEgm != null)
            {
                Log.Debug("Eye EGM '{0}': {1} sym + {2} asym morphs, {3} vertices",
                    eyeEgmPath, eyeEgm.SymmetricMorphs.Length,
                    eyeEgm.AsymmetricMorphs.Length, eyeEgm.VertexCount);
                FaceGenMeshMorpher.Apply(eyeModel, eyeEgm,
                    npc.FaceGenSymmetricCoeffs, npc.FaceGenAsymmetricCoeffs);
            }
        }

        // Override eye texture from EYES record (if NPC has one)
        if (npc.EyeTexturePath != null)
        {
            foreach (var sub in eyeModel.Submeshes)
            {
                sub.DiffuseTexturePath = npc.EyeTexturePath;
            }
        }

        // Merge eye submeshes into head model.
        // Engine renders eyes AFTER hair (scene graph order). RenderOrder=2.
        foreach (var sub in eyeModel.Submeshes)
        {
            sub.RenderOrder = 2;
            model.Submeshes.Add(sub);
            model.ExpandBounds(sub.Positions);
        }
    }

    /// <summary>
    ///     Loads a NIF from BSA, converts BE→LE if needed, and extracts renderable geometry.
    /// </summary>
    private static NifRenderableModel? LoadNifFromBsa(
        string bsaPath,
        BsaArchive archive,
        BsaExtractor extractor,
        NifTextureResolver textureResolver,
        Dictionary<string, System.Numerics.Matrix4x4>? externalBoneTransforms = null,
        string? filterShapeName = null)
    {
        var result = LoadNifRawFromBsa(bsaPath, archive, extractor);
        if (result == null)
            return null;

        return NifGeometryExtractor.Extract(result.Value.Data, result.Value.Info, textureResolver,
            externalBoneTransforms: externalBoneTransforms, filterShapeName: filterShapeName);
    }

    /// <summary>
    ///     Loads and converts a NIF from BSA, returning the raw byte data and parsed NifInfo
    ///     for use with ExtractNamedBoneTransforms() or other NIF inspection.
    /// </summary>
    private static (byte[] Data, NifInfo Info)? LoadNifRawFromBsa(
        string bsaPath,
        BsaArchive archive,
        BsaExtractor extractor)
    {
        var fileRecord = archive.FindFile(bsaPath);
        if (fileRecord == null)
        {
            Log.Warn("NIF not found in BSA: {0}", bsaPath);
            return null;
        }

        var nifData = extractor.ExtractFile(fileRecord);
        if (nifData.Length == 0)
        {
            Log.Warn("NIF extracted but empty (0 bytes): {0}", bsaPath);
            return null;
        }

        var nif = NifParser.Parse(nifData);
        if (nif == null)
        {
            Log.Warn("NIF parse failed ({0} bytes): {1}", nifData.Length, bsaPath);
            return null;
        }

        // Convert Xbox 360 big-endian NIFs
        if (nif.IsBigEndian)
        {
            var converted = NifConverter.Convert(nifData);
            if (!converted.Success || converted.OutputData == null)
            {
                Log.Warn("NIF BE→LE conversion failed: {0}", bsaPath);
                return null;
            }

            nifData = converted.OutputData;
            nif = NifParser.Parse(nifData);
            if (nif == null)
            {
                Log.Warn("NIF re-parse failed after BE→LE conversion: {0}", bsaPath);
                return null;
            }
        }

        return (nifData, nif);
    }

    private static NifRenderableModel DeepCloneModel(NifRenderableModel source)
    {
        var clone = new NifRenderableModel
        {
            MinX = source.MinX,
            MinY = source.MinY,
            MinZ = source.MinZ,
            MaxX = source.MaxX,
            MaxY = source.MaxY,
            MaxZ = source.MaxZ
        };

        foreach (var sub in source.Submeshes)
        {
            clone.Submeshes.Add(new RenderableSubmesh
            {
                Positions = (float[])sub.Positions.Clone(),
                Triangles = sub.Triangles,
                Normals = sub.Normals != null ? (float[])sub.Normals.Clone() : null,
                UVs = sub.UVs,
                VertexColors = sub.VertexColors,
                Tangents = sub.Tangents,
                Bitangents = sub.Bitangents,
                DiffuseTexturePath = sub.DiffuseTexturePath,
                NormalMapTexturePath = sub.NormalMapTexturePath,
                IsEmissive = sub.IsEmissive,
                UseVertexColors = sub.UseVertexColors,
                IsDoubleSided = sub.IsDoubleSided,
                HasAlphaBlend = sub.HasAlphaBlend,
                HasAlphaTest = sub.HasAlphaTest,
                AlphaTestThreshold = sub.AlphaTestThreshold,
                AlphaTestFunction = sub.AlphaTestFunction,
                IsEyeEnvmap = sub.IsEyeEnvmap,
                EnvMapScale = sub.EnvMapScale
            });
        }

        return clone;
    }

    private static uint? ParseFormId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var s = value.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            s = s[2..];

        return uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var id) ? id : null;
    }

    /// <summary>
    ///     Resolves texture BSA paths. If an explicit path is given, uses that.
    ///     Otherwise, auto-discovers all *Texture* BSA files in the meshes BSA directory,
    ///     sorted so higher-numbered BSAs (with overrides/higher-res) come last.
    /// </summary>
    private static string[] ResolveTexturesBsaPaths(string meshesBsaPath, string? explicitPath)
    {
        if (!string.IsNullOrEmpty(explicitPath))
        {
            if (!File.Exists(explicitPath))
            {
                AnsiConsole.MarkupLine("[red]Error:[/] Textures BSA not found: {0}", explicitPath);
                return [];
            }
            return [explicitPath];
        }

        // Auto-discover: find all *Texture* BSA files in the same directory as meshes BSA
        var dir = Path.GetDirectoryName(Path.GetFullPath(meshesBsaPath));
        if (dir == null || !Directory.Exists(dir))
            return [];

        var found = Directory.GetFiles(dir, "*Texture*.bsa")
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (found.Length == 0)
            AnsiConsole.MarkupLine("[yellow]Warning:[/] No texture BSA files found in {0}", dir);
        else
            AnsiConsole.MarkupLine("Auto-detected [green]{0}[/] texture BSA(s) in [cyan]{1}[/]", found.Length, dir);

        return found;
    }

    private sealed class NpcRenderSettings
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
    }
}
