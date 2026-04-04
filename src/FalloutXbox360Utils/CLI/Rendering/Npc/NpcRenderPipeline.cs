using System.Numerics;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Animation;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Appearance;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Appearance.Scanning;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assembly;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assets;
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

        AnsiConsole.MarkupLine("Scanning NPC_, CREA, and RACE records...");
        var resolver = NpcAppearanceResolver.Build(esm.Data, esm.IsBigEndian);
        AnsiConsole.MarkupLine(
            "Found [green]{0}[/] NPCs, [green]{1}[/] creatures, [green]{2}[/] races",
            resolver.NpcCount,
            resolver.CreatureCount,
            resolver.RaceCount);

        AnsiConsole.MarkupLine(
            "Parsing meshes BSA: [cyan]{0}[/]",
            Path.GetFileName(settings.MeshesBsaPath));
        using var meshArchives = NpcMeshArchiveSet.Open(settings.MeshesBsaPath, settings.ExtraMeshesBsaPaths);
        foreach (var extraMeshesBsaPath in meshArchives.ArchivePaths.Skip(1))
        {
            AnsiConsole.MarkupLine(
                "Loading fallback meshes BSA: [cyan]{0}[/]",
                Path.GetFileName(extraMeshesBsaPath));
        }

        foreach (var path in texturesBsaPaths)
        {
            AnsiConsole.MarkupLine(
                "Loading textures BSA: [cyan]{0}[/]",
                Path.GetFileName(path));
        }

        using var textureResolver = new NifTextureResolver(texturesBsaPaths);
        var pluginName = Path.GetFileName(settings.EsmPath);
        var appearances = ResolveAppearances(settings, resolver, pluginName);
        var creatures = ResolveCreatures(settings, resolver);

        var npcCount = appearances?.Count ?? 0;
        var creatureCount = creatures?.Count ?? 0;
        if (npcCount == 0 && creatureCount == 0)
        {
            if (settings.NpcFilters is { Length: > 0 })
            {
                AnsiConsole.MarkupLine(
                    "[red]Error:[/] None of the specified NPCs or creatures found in ESM");
                AnsiConsole.MarkupLine(
                    "  Filters: {0}",
                    string.Join(", ", settings.NpcFilters));
            }

            return;
        }

        if (appearances != null && settings.CompareRaceTextureFgts)
        {
            appearances = BuildTextureComparisonVariants(appearances);
            AnsiConsole.MarkupLine(
                "Expanded to [green]{0}[/] NPC render variants ([cyan]npc_only[/] + [cyan]npc_plus_race[/])",
                appearances.Count);
        }

        var caches = new NpcRenderCaches();
        var rendered = 0;
        var skipped = 0;
        var failed = 0;

        using var gpuResources = NpcGpuRenderResources.Create(settings);
        if (gpuResources.ShouldAbort)
        {
            return;
        }

        // Render NPCs
        if (appearances is { Count: > 0 })
        {
            if (gpuResources.Renderer != null)
            {
                RenderNpcsPipelinedGpu(
                    appearances,
                    gpuResources.Renderer,
                    meshArchives,
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
                    meshArchives,
                    textureResolver,
                    caches,
                    settings,
                    ref rendered,
                    ref skipped,
                    ref failed);
            }
        }

        // Render creatures
        if (creatures is { Count: > 0 })
        {
            RenderCreaturesCpu(
                creatures,
                meshArchives,
                textureResolver,
                resolver,
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

        if (settings.ExtraMeshesBsaPaths is { Length: > 0 })
        {
            foreach (var extraMeshesBsaPath in settings.ExtraMeshesBsaPaths)
            {
                if (!File.Exists(extraMeshesBsaPath))
                {
                    AnsiConsole.MarkupLine(
                        "[red]Error:[/] Extra meshes BSA not found: {0}",
                        extraMeshesBsaPath);
                    return false;
                }
            }
        }

        if (!File.Exists(settings.EsmPath))
        {
            AnsiConsole.MarkupLine(
                "[red]Error:[/] ESM file not found: {0}",
                settings.EsmPath);
            return false;
        }

        texturesBsaPaths = NpcTextureHelpers.ResolveTexturesBsaPaths(
            settings.MeshesBsaPath,
            settings.ExplicitTexturesBsaPaths);
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
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        NpcRenderCaches caches,
        NpcRenderSettings settings,
        ref int rendered,
        ref int skipped,
        ref int failed)
    {
        var views = settings.Camera.ResolveViews(
            90f);

        foreach (var npc in appearances)
        {
            try
            {
                foreach (var (suffix, azimuth, elevation) in views)
                {
                    var result = settings.HeadOnly
                        ? RenderNpcHead(
                            npc,
                            meshArchives,
                            textureResolver,
                            caches,
                            settings,
                            azimuth,
                            elevation)
                        : RenderNpcFullBody(
                            npc,
                            meshArchives,
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
                EvictNpcTextures(textureResolver, npc);
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
            var dmpAppearances = NpcRenderHelpers.ResolveFromDmp(
                settings.DmpPath,
                resolver,
                pluginName,
                settings.NpcFilters);
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
                var formId = NpcTextureHelpers.ParseFormId(filter);
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
                // Don't error — filters may match creatures instead
                return new List<NpcAppearance>();
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

    private static List<(uint FormId, CreatureScanEntry Creature)>? ResolveCreatures(
        NpcRenderSettings settings,
        NpcAppearanceResolver resolver)
    {
        if (settings.DmpPath != null)
        {
            return null; // DMP mode doesn't support creatures yet
        }

        var allCreatures = resolver.GetAllCreatures();
        if (allCreatures.Count == 0)
        {
            return null;
        }

        if (settings.NpcFilters is { Length: > 0 })
        {
            var formIdSet = new HashSet<uint>();
            var editorIdSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var filter in settings.NpcFilters)
            {
                var formId = NpcTextureHelpers.ParseFormId(filter);
                if (formId.HasValue)
                {
                    formIdSet.Add(formId.Value);
                    continue;
                }

                editorIdSet.Add(filter.Trim());
            }

            var filtered = allCreatures
                .Where(kvp =>
                    formIdSet.Contains(kvp.Key) ||
                    (kvp.Value.EditorId != null && editorIdSet.Contains(kvp.Value.EditorId)))
                .Select(kvp => (kvp.Key, kvp.Value))
                .ToList();

            if (filtered.Count > 0)
            {
                AnsiConsole.MarkupLine(
                    "Matched [green]{0}[/] creatures from filter(s)",
                    filtered.Count);
            }

            return filtered.Count > 0 ? filtered : null;
        }

        // No filters: include all named creatures
        var allNamed = allCreatures
            .Where(kvp => !string.IsNullOrEmpty(kvp.Value.FullName))
            .Select(kvp => (kvp.Key, kvp.Value))
            .ToList();
        if (allNamed.Count > 0)
        {
            AnsiConsole.MarkupLine(
                "Resolved [green]{0}[/] named creatures",
                allNamed.Count);
        }

        return allNamed.Count > 0 ? allNamed : null;
    }

    private static void RenderCreaturesCpu(
        List<(uint FormId, CreatureScanEntry Creature)> creatures,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        NpcAppearanceResolver resolver,
        NpcRenderSettings settings,
        ref int rendered,
        ref int skipped,
        ref int failed)
    {
        var views = settings.Camera.ResolveViews(90f);

        foreach (var (formId, creature) in creatures)
        {
            try
            {
                var model = BuildCreatureModel(creature, meshArchives, textureResolver, resolver, settings);
                foreach (var (suffix, azimuth, elevation) in views)
                {
                    SpriteResult? result = null;
                    if (model is { HasGeometry: true })
                    {
                        var renderModel = views.Length > 1
                            ? NpcMeshHelpers.DeepCloneModel(model)
                            : model;
                        result = NifSpriteRenderer.Render(
                            renderModel,
                            textureResolver,
                            1.0f,
                            32,
                            settings.SpriteSize,
                            azimuth,
                            elevation,
                            settings.SpriteSize);
                    }

                    SaveCreatureResult(
                        formId,
                        creature,
                        result,
                        settings,
                        creatures.Count,
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
                    formId,
                    creature.EditorId ?? "?",
                    Markup.Escape(ex.Message));
            }
        }
    }

    private static NifRenderableModel? BuildCreatureModel(
        CreatureScanEntry creature,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        NpcAppearanceResolver resolver,
        NpcRenderSettings settings)
    {
        if (creature.SkeletonPath == null || creature.BodyModelPaths is not { Length: > 0 })
        {
            return null;
        }

        var skeletonNifPath = creature.SkeletonPath.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase)
            ? creature.SkeletonPath
            : "meshes\\" + creature.SkeletonPath;

        // Load skeleton with idle animation. Fallback chain:
        // 1. KFFZ from ESM → 2. locomotion/mtidle.kf → 3. skeleton embedded sequences
        // 4. per-node NiTransformControllers → 5. idleanims/ directory
        var idleAnimPath = settings.AnimOverride ?? creature.ResolveIdleAnimationPath();
        NpcSkeletonLoader.LoadSkeletonBones(
            skeletonNifPath,
            meshArchives,
            settings.BindPose,
            out var boneCache,
            out _,
            idleAnimPath);

        // If still in bind pose, try additional fallbacks
        if (boneCache != null && !settings.BindPose)
        {
            var skelRaw = NpcMeshHelpers.LoadNifRawFromBsa(skeletonNifPath, meshArchives);
            if (skelRaw != null)
            {
                var bindPoseBones = NifGeometryExtractor.ExtractNamedBoneTransforms(
                    skelRaw.Value.Data, skelRaw.Value.Info);
                // Check if skeleton loader actually applied idle overrides.
                // Use threshold comparison since re-extraction can have float imprecision.
                var hasIdlePose = false;
                foreach (var (name, idleTransform) in boneCache)
                {
                    if (bindPoseBones.TryGetValue(name, out var bindTransform))
                    {
                        var diff = (idleTransform.Translation - bindTransform.Translation).Length();
                        if (diff > 0.1f)
                        {
                            hasIdlePose = true;
                            break;
                        }
                    }
                }

                if (!hasIdlePose)
                {
                    // No idle from skeleton loader — try additional fallbacks
                    // Fallback 4: per-node NiTransformControllers
                    var nodeOverrides = NifNodeControllerPoseReader.Parse(
                        skelRaw.Value.Data, skelRaw.Value.Info);
                    if (nodeOverrides is { Count: > 0 })
                    {
                        boneCache = NifGeometryExtractor.ExtractNamedBoneTransforms(
                            skelRaw.Value.Data, skelRaw.Value.Info, nodeOverrides);
                    }

                    // Fallback 5: try candidate KFs in skeleton directory.
                    // Some creatures have no KFFZ and no mtidle.kf, but have
                    // special idle or locomotion animations that give an upright pose.
                    var skelDir = skeletonNifPath.Replace(
                        "skeleton.nif", "", StringComparison.OrdinalIgnoreCase);
                    string[] candidateIdleKfs =
                    [
                        "idleanims\\specialidle_toungehang.kf",
                        "idleanims\\specialidle_sniff.kf",
                        "idleanims\\mtidle.kf",
                        "locomotion\\mtforward.kf" // walking pose — upright at least
                    ];
                    foreach (var candidateKf in candidateIdleKfs)
                    {
                        var kfPath = skelDir + candidateKf;
                        var idleAnimsKf = NpcMeshHelpers.LoadNifRawFromBsa(
                            kfPath, meshArchives, skipConversion: true);
                        if (idleAnimsKf != null)
                        {
                            var animOverrides = NifAnimationParser.ParseIdlePoseOverrides(
                                idleAnimsKf.Value.Data, idleAnimsKf.Value.Info);
                            if (animOverrides is { Count: > 0 })
                            {
                                boneCache = NifGeometryExtractor.ExtractNamedBoneTransforms(
                                    skelRaw.Value.Data, skelRaw.Value.Info, animOverrides);
                                    break;
                            }
                        }
                    }
                }
            }
        }

        var skeletonDirectory = Path.GetDirectoryName(skeletonNifPath);
        var bodyModel = new NifRenderableModel();

        // Get head bone world transform for rigid mesh attachment (eyes, brain, etc.)
        // Use full transform (rotation + translation) since rigid meshes are authored
        // in head-local space and need the head's orientation applied.
        Matrix4x4? headBoneTransform = null;
        if (boneCache != null && boneCache.TryGetValue("Bip01 Head", out var headBoneWorld))
        {
            headBoneTransform = headBoneWorld;
        }

        foreach (var bodyPath in creature.BodyModelPaths)
        {
            string fullBodyPath;
            if (bodyPath.Contains('\\') || bodyPath.Contains('/'))
            {
                fullBodyPath = bodyPath.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase)
                    ? bodyPath
                    : "meshes\\" + bodyPath;
            }
            else if (!string.IsNullOrEmpty(skeletonDirectory))
            {
                fullBodyPath = Path.Combine(skeletonDirectory, bodyPath);
            }
            else
            {
                fullBodyPath = "meshes\\" + bodyPath;
            }

            var raw = NpcMeshHelpers.LoadNifRawFromBsa(fullBodyPath, meshArchives);
            if (raw == null)
            {
                continue;
            }

            var partModel = NifGeometryExtractor.Extract(
                raw.Value.Data,
                raw.Value.Info,
                textureResolver,
                externalBoneTransforms: boneCache,
                useDualQuaternionSkinning: true);
            if (partModel == null || !partModel.HasGeometry)
            {
                continue;
            }

            // Rigid (unskinned) meshes are likely authored in head-local space
            // (eyes, brain dome, etc.). Attach to Bip01 Head using translation only
            // (matches NifExportSceneBuilder.BuildCreature logic).
            var attachRigidToHead = !partModel.WasSkinned && headBoneTransform.HasValue;

            foreach (var sub in partModel.Submeshes)
            {
                if (attachRigidToHead)
                {
                    NpcRenderHelpers.TransformSubmesh(sub, headBoneTransform!.Value);
                }

                bodyModel.Submeshes.Add(sub);
                bodyModel.ExpandBounds(sub.Positions);
            }

            if (partModel.WasSkinned)
            {
                bodyModel.WasSkinned = true;
            }
        }

        // Attach weapon if creature has one in inventory
        if (!settings.NoWeapon && creature.InventoryItems != null && boneCache != null)
        {
            string? weaponMeshPath = null;
            foreach (var item in creature.InventoryItems)
            {
                weaponMeshPath = resolver.ResolveWeaponMeshPath(item.ItemFormId);
                if (weaponMeshPath != null) break;
            }

            if (weaponMeshPath != null && boneCache.TryGetValue("Weapon", out var weaponBoneTransform))
            {
                var weaponNifPath = weaponMeshPath.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase)
                    ? weaponMeshPath
                    : "meshes\\" + weaponMeshPath;
                var weaponRaw = NpcMeshHelpers.LoadNifRawFromBsa(weaponNifPath, meshArchives);
                if (weaponRaw != null)
                {
                    var weaponModel = NifGeometryExtractor.Extract(
                        weaponRaw.Value.Data, weaponRaw.Value.Info, textureResolver);
                    if (weaponModel is { HasGeometry: true })
                    {
                        foreach (var sub in weaponModel.Submeshes)
                        {
                            NpcRenderHelpers.TransformSubmesh(sub, weaponBoneTransform);
                            bodyModel.Submeshes.Add(sub);
                            bodyModel.ExpandBounds(sub.Positions);
                        }
                    }
                }
            }
        }

        return bodyModel.HasGeometry ? bodyModel : null;
    }

    private static void SaveCreatureResult(
        uint formId,
        CreatureScanEntry creature,
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
                    formId,
                    creature.FullName ?? creature.EditorId ?? "unknown");
            }

            return;
        }

        var name = creature.EditorId ?? $"{formId:X8}";
        var fileName = $"{name}{viewSuffix}.png";
        var outputPath = Path.Combine(settings.OutputDir, fileName);
        var expectedLength = result.Width * result.Height * 4;

        if (result.Pixels.Length != expectedLength)
        {
            failed++;
            AnsiConsole.MarkupLine(
                "[red]FAIL:[/] 0x{0:X8} {1}: pixel buffer mismatch ({2} bytes, expected {3} for {4}x{5})",
                formId,
                creature.EditorId ?? "?",
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
                "[green]OK:[/] 0x{0:X8} {1} ({2}) -> {3} ({4}x{5})",
                formId,
                Markup.Escape(creature.FullName ?? "?"),
                Markup.Escape(creature.CreatureTypeName),
                fileName,
                result.Width,
                result.Height);
        }
    }

    private static void RenderNpcsPipelinedGpu(
        List<NpcAppearance> appearances,
        GpuSpriteRenderer gpuRenderer,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        NpcRenderCaches caches,
        NpcRenderSettings settings,
        ref int rendered,
        ref int skipped,
        ref int failed)
    {
        var views = settings.Camera.ResolveViews(
            90f);

        NifRenderableModel? currentModel = null;
        if (appearances.Count > 0)
        {
            currentModel = BuildNpcModel(
                appearances[0],
                meshArchives,
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
                            viewIndex < views.Length - 1);
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
                            meshArchives,
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
                EvictNpcTextures(textureResolver, npc);
                gpuRenderer.EvictTexture(NpcTextureHelpers.BuildNpcFaceEgtTextureKey(npc));
                gpuRenderer.EvictTexture(NpcTextureHelpers.BuildNpcBodyEgtTextureKey(
                    npc.NpcFormId,
                    "upperbody",
                    npc.RenderVariantLabel));
                gpuRenderer.EvictTexture(NpcTextureHelpers.BuildNpcBodyEgtTextureKey(
                    npc.NpcFormId,
                    "lefthand",
                    npc.RenderVariantLabel));
                gpuRenderer.EvictTexture(NpcTextureHelpers.BuildNpcBodyEgtTextureKey(
                    npc.NpcFormId,
                    "righthand",
                    npc.RenderVariantLabel));
                currentModel = nextModel;
            }
        }
    }

    private static NifRenderableModel? BuildNpcModel(
        NpcAppearance npc,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        NpcRenderCaches caches,
        NpcRenderSettings settings)
    {
        if (settings.HeadOnly)
        {
            return NpcHeadBuilder.Build(
                npc,
                meshArchives,
                textureResolver,
                caches.HeadMeshes,
                caches.EgmFiles,
                caches.EgtFiles,
                settings);
        }

        return NpcBodyBuilder.Build(
            npc,
            meshArchives,
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
        var renderModel = cloneForRender ? NpcMeshHelpers.DeepCloneModel(model) : model;
        return renderModel;
    }

    private static SpriteResult? RenderNpcHead(
        NpcAppearance npc,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        NpcRenderCaches caches,
        NpcRenderSettings settings,
        float azimuth,
        float elevation)
    {
        var model = NpcHeadBuilder.Build(
            npc,
            meshArchives,
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
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        NpcRenderCaches caches,
        NpcRenderSettings settings,
        float azimuth,
        float elevation)
    {
        var model = NpcBodyBuilder.Build(
            npc,
            meshArchives,
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

        var name = NpcTextureHelpers.BuildNpcRenderName(npc);
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

    internal static List<NpcAppearance> BuildTextureComparisonVariants(
        IReadOnlyList<NpcAppearance> appearances)
    {
        var variants = new List<NpcAppearance>(appearances.Count * 2);
        foreach (var appearance in appearances)
        {
            variants.Add(appearance.CloneWithTextureVariant(
                appearance.NpcFaceGenTextureCoeffs,
                "npc_only"));
            variants.Add(appearance.CloneWithTextureVariant(
                NpcFaceGenCoefficientMerger.Merge(
                    appearance.NpcFaceGenTextureCoeffs,
                    appearance.RaceFaceGenTextureCoeffs),
                "npc_plus_race"));
        }

        return variants;
    }

    private static void EvictNpcTextures(
        NifTextureResolver textureResolver,
        NpcAppearance npc)
    {
        textureResolver.EvictTexture(NpcTextureHelpers.BuildNpcFaceEgtTextureKey(npc));
        textureResolver.EvictTexture(NpcTextureHelpers.BuildNpcBodyEgtTextureKey(
            npc.NpcFormId,
            "upperbody",
            npc.RenderVariantLabel));
        textureResolver.EvictTexture(NpcTextureHelpers.BuildNpcBodyEgtTextureKey(
            npc.NpcFormId,
            "lefthand",
            npc.RenderVariantLabel));
        textureResolver.EvictTexture(NpcTextureHelpers.BuildNpcBodyEgtTextureKey(
            npc.NpcFormId,
            "righthand",
            npc.RenderVariantLabel));
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
            if (settings.CompareRaceTextureFgts)
            {
                if (settings.ForceGpu)
                {
                    AnsiConsole.MarkupLine(
                        "[yellow]--compare-race-fgts currently uses the CPU renderer; ignoring --gpu[/]");
                }

                AnsiConsole.MarkupLine(
                    "Using [yellow]CPU software renderer[/] (--compare-race-fgts)");
                return new NpcGpuRenderResources(null, null, false);
            }

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
