using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using BethesdaMultitool.CLI.Rendering.Nif;
using BethesdaMultitool.CLI.Rendering.Npc;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Analysis.Geometry;
using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Appearance;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Appearance.Scanning;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Composition;
using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Rasterization;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Minidump;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc;

/// <summary>
///     GUI-facing service wrapping the NPC render/export pipelines.
///     No Spectre.Console dependency — suitable for WinUI 3 consumption.
/// </summary>
internal sealed class NpcBrowserService : IDisposable
{
    private static readonly Logger Log = Logger.Instance;

    // Shared caches (same as CLI pipelines)
    private readonly NpcCompositionCaches _compositionCaches = new();

    // Pre-resolved appearances from DMP (null for ESM-only mode)
    private readonly Dictionary<uint, NpcAppearance>? _dmpAppearances;
    private readonly BethesdaGame _game;
    private readonly MeshArchiveSet _meshArchives;

    // Composition caches contain ordinary dictionaries, and generated FaceGen/EGT keys are stable
    // per actor. Keep each compose/capture/evict transaction exclusive so a concurrent preview,
    // export, render, or batch item cannot evict another operation's in-flight payload.
    private readonly NpcBrowserOperationGate _operationGate = new();
    private readonly string _pluginName;
    private readonly NpcRenderCaches _renderCaches = new();

    private readonly NpcAppearanceResolver _resolver;
    private readonly NifTextureResolver _textureResolver;
    private readonly string[] _textureSourcePaths;

    private NpcBrowserService(
        NpcAppearanceResolver resolver,
        MeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        string[] textureSourcePaths,
        string pluginName,
        Dictionary<uint, NpcAppearance>? dmpAppearances = null)
    {
        _resolver = resolver;
        _meshArchives = meshArchives;
        _textureResolver = textureResolver;
        _textureSourcePaths = (string[])textureSourcePaths.Clone();
        _pluginName = pluginName;
        _game = ResolveGame(resolver.Game, pluginName);
        _dmpAppearances = dmpAppearances;
    }

    public int NpcCount => _dmpAppearances?.Count ?? _resolver.NpcCount;
    public int CreatureCount => _resolver.CreatureCount;
    public int RaceCount => _resolver.RaceCount;
    public bool IsDmpMode => _dmpAppearances != null;

    public void Dispose()
    {
        _operationGate.DisposeResources(() =>
        {
            _meshArchives.Dispose();
            _textureResolver.Dispose();
        });
    }

    /// <summary>
    ///     Prefers the game established from record structure over a master-file name. Filename
    ///     inference remains a fallback for incomplete legacy scans and renamed plugins therefore
    ///     retain the authoritative family decoded by the appearance index.
    /// </summary>
    internal static BethesdaGame ResolveGame(BethesdaGame indexedGame, string pluginName)
    {
        return indexedGame != BethesdaGame.Unknown
            ? indexedGame
            : GameProfiles.ResolveByNames([pluginName]) ?? BethesdaGame.Unknown;
    }

    /// <summary>
    ///     Creates a browser service for an ESM and its discovered mesh/texture archives, or <c>null</c> if assets can't
    ///     be opened.
    /// </summary>
    public static NpcBrowserService? TryCreate(
        byte[] esmData,
        bool bigEndian,
        string esmPath,
        BsaDiscoveryResult bsaPaths)
    {
        return TryCreate(
            esmData,
            bigEndian,
            esmPath,
            bsaPaths,
            null,
            default);
    }

    internal static NpcBrowserService? TryCreate(
        byte[] esmData,
        bool bigEndian,
        string esmPath,
        BsaDiscoveryResult bsaPaths,
        IProgress<NpcBrowserLoadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!bsaPaths.HasMeshes)
        {
            return null;
        }

        return TryCreateCore(
            () => NpcAppearanceResolver.Build(
                esmData,
                bigEndian,
                timing => LogAppearanceIndexTiming(timing, "whole-file-rescan"),
                cancellationToken),
            esmPath,
            bsaPaths,
            progress,
            "whole-file-rescan",
            cancellationToken);
    }

    /// <summary>
    ///     Creates an ESM browser from the session's retained record descriptors and memory mapping.
    ///     This is the GUI path: it reads only appearance-relevant record payloads and never rereads
    ///     or rescans the complete plugin.
    /// </summary>
    internal static NpcBrowserService? TryCreateFromAnalyzedEsm(
        IMemoryAccessor esmAccessor,
        long esmLength,
        EsmRecordScanResult analyzedRecords,
        bool bigEndian,
        string esmPath,
        BsaDiscoveryResult bsaPaths,
        IProgress<NpcBrowserLoadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(esmAccessor);
        ArgumentNullException.ThrowIfNull(analyzedRecords);
        if (!bsaPaths.HasMeshes || analyzedRecords.MainRecords.Count == 0)
        {
            return null;
        }

        return TryCreateCore(
            () => NpcAppearanceResolver.Build(
                esmAccessor,
                esmLength,
                analyzedRecords.MainRecords,
                bigEndian,
                analyzedRecords.Game,
                timing => LogAppearanceIndexTiming(timing, "analyzed-session-index"),
                cancellationToken),
            esmPath,
            bsaPaths,
            progress,
            "analyzed-session-index",
            cancellationToken);
    }

    private static NpcBrowserService? TryCreateCore(
        Func<NpcAppearanceResolver> resolverFactory,
        string esmPath,
        BsaDiscoveryResult bsaPaths,
        IProgress<NpcBrowserLoadProgress>? progress,
        string recordSource,
        CancellationToken cancellationToken)
    {
        var totalTimer = Stopwatch.StartNew();
        Log.Info(
            "NPC Browser load started for '{0}' source={1}; CPU record/archive preparation only " +
            "(renderer and shaders remain cold until actor selection).",
            Path.GetFileName(esmPath),
            recordSource);

        progress?.Report(new NpcBrowserLoadProgress(
            recordSource == "analyzed-session-index"
                ? NpcBrowserLoadStage.ReusingRecordIndex
                : NpcBrowserLoadStage.ReadingEsm,
            recordSource == "analyzed-session-index"
                ? "Reusing analyzed ESM record index..."
                : "Scanning NPC records..."));

        var resolver = TimeLoadStage(
            NpcBrowserLoadStage.DecodingAppearanceRecords,
            "Reading NPC appearance records...",
            resolverFactory,
            progress,
            $"source={recordSource}");
        cancellationToken.ThrowIfCancellationRequested();

        var meshArchives = TimeLoadStage(
            NpcBrowserLoadStage.IndexingMeshArchives,
            "Indexing mesh archives...",
            () => MeshArchiveSet.Open(
                bsaPaths.MeshesBsaPaths[0],
                bsaPaths.MeshesBsaPaths.Length > 1 ? bsaPaths.MeshesBsaPaths[1..] : null),
            progress,
            $"archives={bsaPaths.MeshesBsaPaths.Length}");

        NifTextureResolver? textureResolver = null;
        NpcBrowserService? service = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            textureResolver = TimeLoadStage(
                NpcBrowserLoadStage.IndexingTextureArchives,
                "Indexing texture archives...",
                () => new NifTextureResolver(bsaPaths.TexturesBsaPaths),
                progress,
                $"archives={bsaPaths.TexturesBsaPaths.Length}");
            cancellationToken.ThrowIfCancellationRequested();

            service = new NpcBrowserService(
                resolver,
                meshArchives,
                textureResolver,
                bsaPaths.TexturesBsaPaths,
                Path.GetFileName(esmPath));
            textureResolver = null;

            totalTimer.Stop();
            var detail =
                $"source={recordSource} npcs={service.NpcCount} creatures={service.CreatureCount} " +
                $"races={service.RaceCount}";
            Log.Info(
                "NPC Browser load completed in {0:N2} ms ({1}).",
                totalTimer.Elapsed.TotalMilliseconds,
                detail);
            progress?.Report(new NpcBrowserLoadProgress(
                NpcBrowserLoadStage.Complete,
                $"Loaded {service.NpcCount + service.CreatureCount:N0} actors",
                totalTimer.Elapsed,
                detail));
            return service;
        }
        catch
        {
            if (service != null)
            {
                service.Dispose();
            }
            else
            {
                textureResolver?.Dispose();
                meshArchives.Dispose();
            }

            throw;
        }
    }

    private static T TimeLoadStage<T>(
        NpcBrowserLoadStage stage,
        string message,
        Func<T> action,
        IProgress<NpcBrowserLoadProgress>? progress,
        string detail)
    {
        progress?.Report(new NpcBrowserLoadProgress(stage, message, Detail: detail));
        var timer = Stopwatch.StartNew();
        try
        {
            var result = action();
            timer.Stop();
            Log.Info(
                "NPC Browser load stage={0} outcome=completed elapsed={1:N2} ms {2}.",
                stage,
                timer.Elapsed.TotalMilliseconds,
                detail);
            progress?.Report(new NpcBrowserLoadProgress(stage, message, timer.Elapsed, detail));
            return result;
        }
        catch
        {
            timer.Stop();
            Log.Info(
                "NPC Browser load stage={0} outcome=failed elapsed={1:N2} ms {2}.",
                stage,
                timer.Elapsed.TotalMilliseconds,
                detail);
            throw;
        }
    }

    private static void LogAppearanceIndexTiming(NpcAppearanceIndexBuildTiming timing, string source)
    {
        Log.Info(
            "NPC Browser appearance-index stage={0} elapsed={1:N2} ms source={2} " +
            "recordsVisited={3:N0} recordsDecoded={4:N0} bytesRead={5:N0}.",
            timing.Stage,
            timing.Elapsed.TotalMilliseconds,
            source,
            timing.RecordsVisited,
            timing.RecordsDecoded,
            timing.BytesRead);
    }

    /// <summary>
    ///     Creates a service from a DMP memory dump, using a game Data directory for ESM and BSA assets.
    ///     Resolves all NPC appearances from DMP runtime memory at initialization time.
    /// </summary>
    public static NpcBrowserService? TryCreateFromDmp(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        MinidumpInfo minidumpInfo,
        EsmRecordScanResult scanResult,
        byte[] esmData,
        bool esmBigEndian,
        string esmPath,
        BsaDiscoveryResult bsaPaths)
    {
        if (!bsaPaths.HasMeshes)
        {
            return null;
        }

        var resolver = NpcAppearanceResolver.Build(esmData, esmBigEndian);
        var meshArchives = MeshArchiveSet.Open(
            bsaPaths.MeshesBsaPaths[0],
            bsaPaths.MeshesBsaPaths.Length > 1 ? bsaPaths.MeshesBsaPaths[1..] : null);
        var textureResolver = new NifTextureResolver(bsaPaths.TexturesBsaPaths);
        var pluginName = Path.GetFileName(esmPath);

        // Get NPC_ entries from DMP runtime hash table (FormType 0x2A = NPC_)
        var npcEntries = scanResult.RuntimeEditorIds
            .Where(e => e.FormType == 0x2A)
            .ToList();

        // Resolve all NPC appearances from DMP
        var dmpAppearances = new Dictionary<uint, NpcAppearance>();
        if (npcEntries.Count == 0)
        {
            Log.Warn(
                "No NPC_ entries found in DMP runtime hash table; companion-ESM creatures remain available");
        }
        else
        {
            Log.Info("Found {0} NPC_ entries in DMP, resolving appearances...", npcEntries.Count);

            // Runtime data remains authoritative for the DMP NPC tab. The companion ESM is used
            // only for actor families that the runtime appearance path does not currently decode.
            var structReader = RuntimeStructReader.CreateWithAutoDetect(
                accessor,
                fileSize,
                minidumpInfo,
                scanResult.RuntimeRefrFormEntries,
                npcEntries);

            foreach (var entry in npcEntries)
            {
                var npcRecord = structReader.ReadRuntimeNpc(entry);
                if (npcRecord == null)
                {
                    continue;
                }

                var appearance = resolver.ResolveFromDmpRecord(npcRecord, pluginName);
                if (appearance != null)
                {
                    dmpAppearances.TryAdd(appearance.NpcFormId, appearance);
                }
            }
        }

        if (dmpAppearances.Count == 0 && resolver.CreatureCount == 0)
        {
            Log.Warn("No runtime NPC appearances or companion-ESM creatures could be resolved for DMP browsing");
            meshArchives.Dispose();
            textureResolver.Dispose();
            return null;
        }

        Log.Info(
            "Resolved {0} runtime NPC appearances and {1} companion-ESM creatures for DMP browsing",
            dmpAppearances.Count,
            resolver.CreatureCount);

        return new NpcBrowserService(
            resolver,
            meshArchives,
            textureResolver,
            bsaPaths.TexturesBsaPaths,
            pluginName,
            dmpAppearances);
    }

    /// <summary>
    ///     Auto-detects ESM endianness by comparing TES4 header data size as LE vs BE.
    /// </summary>
    internal static bool DetectEsmBigEndian(byte[] esmData)
    {
        if (esmData.Length < 8)
        {
            return false;
        }

        // TES4 header: bytes 0-3 = "TES4", bytes 4-7 = data size
        var sizeLE = BitConverter.ToUInt32(esmData, 4);
        var sizeBE = (uint)((esmData[4] << 24) | (esmData[5] << 16) | (esmData[6] << 8) | esmData[7]);

        // TES4 data size is typically < 1 KB; never > 1 MB
        if (sizeBE < sizeLE && sizeBE < 0x100000)
        {
            return true;
        }

        return false;
    }

    /// <summary>Returns the browsable actor list (from ESM or DMP), optionally limited to named actors.</summary>
    public List<NpcListItem> GetNpcList(bool namedOnly = false)
    {
        using var operation = _operationGate.Enter();

        if (_dmpAppearances != null)
        {
            return GetNpcListFromDmp(namedOnly);
        }

        var npcs = _resolver.GetAllNpcs();
        var creatures = _resolver.GetAllCreatures();
        var list = new List<NpcListItem>(npcs.Count + creatures.Count);

        foreach (var (formId, npc) in npcs)
        {
            if (namedOnly && string.IsNullOrEmpty(npc.FullName))
            {
                continue;
            }

            list.Add(new NpcListItem(formId, npc.EditorId, npc.FullName, npc.IsFemale, npc.RaceFormId));
        }

        foreach (var (formId, creature) in creatures)
        {
            if (namedOnly && string.IsNullOrEmpty(creature.FullName))
            {
                continue;
            }

            list.Add(new NpcListItem(formId, creature.EditorId, creature.FullName, creature.ResolveBodyModelPath(),
                creature.GetCreatureTypeName(_game)));
        }

        list.Sort((a, b) =>
            string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));

        return list;
    }

    /// <summary>
    ///     Composes an NPC into the renderer-neutral native-viewer scene, or <c>null</c> if the NPC
    ///     can't be resolved. Appearance resolution, FaceGen morphs, equipment, weapon, skeleton,
    ///     and skin assembly all remain owned by the existing NPC composition pipeline.
    /// </summary>
    public BethesdaViewerScene? BuildViewerScene(
        uint npcFormId,
        bool headOnly,
        bool noEquip,
        bool noWeapon,
        bool bindPose = false,
        ushort? previewPlayerLevel = null)
    {
        using var operation = _operationGate.Enter();

        var appearance = ResolveAppearance(npcFormId, previewPlayerLevel);
        if (appearance == null)
        {
            return null;
        }

        var settings = new NpcExportSettings
        {
            MeshesBsaPath = string.Empty, // not used — we pass meshArchives directly
            EsmPath = string.Empty,
            OutputDir = string.Empty,
            HeadOnly = headOnly,
            NoEquip = noEquip,
            IncludeWeapon = !noWeapon,
            BindPose = bindPose
        };

        try
        {
            var plan = NpcCompositionPlanner.CreatePlan(
                appearance,
                _meshArchives,
                _textureResolver,
                _compositionCaches,
                NpcCompositionOptions.From(settings));
            var exportScene = NpcCompositionExportAdapter.BuildNpc(
                plan,
                _meshArchives,
                _textureResolver,
                _compositionCaches);

            if (exportScene == null || exportScene.MeshParts.Count == 0)
            {
                return null;
            }

            var viewerScene = BethesdaViewerSceneGlbAdapter.FromGlbScene(
                exportScene,
                BuildNpcSourceLabel(appearance),
                BethesdaViewerScenePurpose.NpcAppearance,
                game: _game,
                textureSourcePaths: _textureSourcePaths);
            CaptureReferencedGeneratedTextures(viewerScene, appearance);
            NpcBoundaryVertexStitcher.PopulateViewerSceneBoundaryGroups(viewerScene);
            return viewerScene;
        }
        finally
        {
            EvictNpcGeneratedTextures(appearance);
        }
    }

    /// <summary>
    ///     Composes a creature into the renderer-neutral native-viewer scene, or <c>null</c> if the
    ///     creature can't be resolved. The existing record-driven skeleton, body, weapon, and skin
    ///     assembly remains unchanged.
    /// </summary>
    public BethesdaViewerScene? BuildCreatureViewerScene(uint creatureFormId, bool bindPose = false)
    {
        using var operation = _operationGate.Enter();

        var creatures = _resolver.GetAllCreatures();
        if (!creatures.TryGetValue(creatureFormId, out var creature))
        {
            return null;
        }

        if (creature.SkeletonPath == null || creature.BodyModelPaths is not { Length: > 0 })
        {
            return null;
        }

        var plan = CreatureCompositionPlanner.CreatePlan(
            creature,
            _meshArchives,
            _resolver,
            new CreatureCompositionOptions
            {
                IncludeWeapon = true,
                BindPose = bindPose
            });
        if (plan != null)
        {
            Log.Info(
                "NPC Browser creature animation formId=0x{0:X8} source={1} composedOverrides={2:N0}.",
                creatureFormId,
                plan.AnimationSourcePath ?? "(bind/rest pose)",
                plan.AnimationOverrides?.Count ?? 0);
        }

        var exportScene = plan == null ? null : NpcCompositionExportAdapter.BuildCreature(plan, _meshArchives);

        if (exportScene == null || exportScene.MeshParts.Count == 0)
        {
            return null;
        }

        var viewerScene = BethesdaViewerSceneGlbAdapter.FromGlbScene(
            exportScene,
            BuildCreatureSourceLabel(creatureFormId, creature),
            BethesdaViewerScenePurpose.CreatureAppearance,
            game: _game,
            textureSourcePaths: _textureSourcePaths);
        NpcBoundaryVertexStitcher.PopulateViewerSceneBoundaryGroups(viewerScene);
        return viewerScene;
    }

    /// <summary>
    ///     Serializes a native viewer scene to GLB for compatibility/export callers. Generated
    ///     actor textures are restored to the resolver immediately before serialization so the
    ///     export does not depend on the resolver cache lifetime used during composition.
    /// </summary>
    public byte[] ExportViewerSceneToGlb(BethesdaViewerScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        using var operation = _operationGate.Enter();

        try
        {
            foreach (var (texturePath, texture) in scene.GeneratedTextures)
            {
                _textureResolver.InjectTexture(texturePath, texture);
            }

            var exportScene = BethesdaViewerSceneGlbAdapter.ToGlbScene(scene);
            return GlbWriter.WriteToBytes(exportScene, _textureResolver);
        }
        finally
        {
            foreach (var texturePath in scene.GeneratedTextures.Keys)
            {
                _textureResolver.EvictTexture(texturePath);
            }
        }
    }

    /// <summary>Composes and exports an NPC to GLB bytes, or <c>null</c> if the NPC can't be resolved.</summary>
    public byte[]? BuildGlb(uint npcFormId, bool headOnly, bool noEquip, bool noWeapon,
        bool bindPose = false,
        ushort? previewPlayerLevel = null)
    {
        using var operation = _operationGate.Enter();

        var scene = BuildViewerScene(
            npcFormId,
            headOnly,
            noEquip,
            noWeapon,
            bindPose,
            previewPlayerLevel);
        return scene == null ? null : ExportViewerSceneToGlb(scene);
    }

    /// <summary>Composes and exports a creature to GLB bytes, or <c>null</c> if the creature can't be resolved.</summary>
    public byte[]? BuildCreatureGlb(uint creatureFormId, bool bindPose = false)
    {
        using var operation = _operationGate.Enter();

        var scene = BuildCreatureViewerScene(creatureFormId, bindPose);
        return scene == null ? null : ExportViewerSceneToGlb(scene);
    }

    /// <summary>Composes and renders an NPC to PNG bytes, or <c>null</c> if the NPC can't be resolved or rendered.</summary>
    public byte[]? RenderPng(
        uint npcFormId,
        bool headOnly,
        bool noEquip,
        bool noWeapon,
        int spriteSize,
        float azimuth,
        float elevation,
        ushort? previewPlayerLevel = null)
    {
        using var operation = _operationGate.Enter();

        var appearance = ResolveAppearance(npcFormId, previewPlayerLevel);
        if (appearance == null)
        {
            return null;
        }

        var settings = new NpcRenderSettings
        {
            MeshesBsaPath = string.Empty,
            EsmPath = string.Empty,
            OutputDir = string.Empty,
            HeadOnly = headOnly,
            NoEquip = noEquip,
            NoWeapon = noWeapon,
            SpriteSize = spriteSize
        };

        try
        {
            var plan = NpcCompositionPlanner.CreatePlan(
                appearance,
                _meshArchives,
                _textureResolver,
                _renderCaches.Composition,
                NpcCompositionOptions.From(settings));
            var model = NpcCompositionRenderAdapter.BuildNpc(
                plan,
                _meshArchives,
                _textureResolver,
                _renderCaches.Composition,
                _renderCaches.RenderModels);

            if (model == null)
            {
                return null;
            }

            var result = NifSpriteRenderer.Render(
                model, _textureResolver, 1.0f, 32, spriteSize, azimuth, elevation, spriteSize);

            if (result == null)
            {
                return null;
            }

            return PngWriter.EncodeRgba(result.Pixels, result.Width, result.Height);
        }
        finally
        {
            EvictNpcGeneratedTextures(appearance);
        }
    }

    /// <summary>Exports a batch of NPCs to GLB files, reporting progress and honoring cancellation.</summary>
    public async Task BatchExportGlbAsync(
        string outputDir,
        bool headOnly,
        bool noEquip,
        bool noWeapon,
        IProgress<(int Done, int Total, string Name)> progress,
        CancellationToken ct,
        IReadOnlyList<uint>? selectedFormIds = null)
    {
        List<NpcAppearance> appearances;
        using (var operation = _operationGate.Enter())
        {
            appearances = FilterBySelection(GetAllAppearances(), selectedFormIds);
        }

        var total = appearances.Count;

        var settings = new NpcExportSettings
        {
            MeshesBsaPath = string.Empty,
            EsmPath = string.Empty,
            OutputDir = outputDir,
            HeadOnly = headOnly,
            NoEquip = noEquip,
            IncludeWeapon = !noWeapon
        };

        Directory.CreateDirectory(outputDir);
        await Task.Run(() =>
        {
            var done = 0;
            foreach (var npc in appearances)
            {
                ct.ThrowIfCancellationRequested();
                using (_operationGate.Enter())
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var plan = NpcCompositionPlanner.CreatePlan(
                            npc,
                            _meshArchives,
                            _textureResolver,
                            _compositionCaches,
                            NpcCompositionOptions.From(settings));
                        var scene = NpcCompositionExportAdapter.BuildNpc(
                            plan,
                            _meshArchives,
                            _textureResolver,
                            _compositionCaches);
                        if (scene != null && scene.MeshParts.Count > 0)
                        {
                            var outputPath = Path.Combine(outputDir, NpcExportFileNaming.BuildFileName(npc));
                            GlbWriter.Write(scene, _textureResolver, outputPath);
                        }
                    }
                    catch
                    {
                        // Skip failures in batch mode
                    }
                    finally
                    {
                        EvictNpcGeneratedTextures(npc);
                    }
                }

                done++;
                progress.Report((done, total, npc.FullName ?? npc.EditorId ?? $"0x{npc.NpcFormId:X8}"));
            }
        }, ct);
    }

    /// <summary>Renders a batch of NPCs to PNG files, reporting progress and honoring cancellation.</summary>
    public async Task BatchRenderPngAsync(
        string outputDir,
        bool headOnly,
        bool noEquip,
        bool noWeapon,
        int spriteSize,
        CameraConfig camera,
        IProgress<(int Done, int Total, string Name)> progress,
        CancellationToken ct,
        IReadOnlyList<uint>? selectedFormIds = null)
    {
        List<NpcAppearance> appearances;
        using (var operation = _operationGate.Enter())
        {
            appearances = FilterBySelection(GetAllAppearances(), selectedFormIds);
        }

        var total = appearances.Count;
        var views = camera.ResolveViews(90f);

        var settings = new NpcRenderSettings
        {
            MeshesBsaPath = string.Empty,
            EsmPath = string.Empty,
            OutputDir = outputDir,
            HeadOnly = headOnly,
            NoEquip = noEquip,
            NoWeapon = noWeapon,
            SpriteSize = spriteSize,
            Camera = camera
        };

        Directory.CreateDirectory(outputDir);
        var caches = new NpcRenderCaches();

        await Task.Run(() =>
        {
            var done = 0;
            foreach (var npc in appearances)
            {
                ct.ThrowIfCancellationRequested();
                using (_operationGate.Enter())
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        foreach (var (suffix, azimuth, elevation) in views)
                        {
                            var plan = NpcCompositionPlanner.CreatePlan(
                                npc,
                                _meshArchives,
                                _textureResolver,
                                caches.Composition,
                                NpcCompositionOptions.From(settings));
                            var model = NpcCompositionRenderAdapter.BuildNpc(
                                plan,
                                _meshArchives,
                                _textureResolver,
                                caches.Composition,
                                caches.RenderModels);

                            if (model == null)
                            {
                                continue;
                            }

                            var result = NifSpriteRenderer.Render(
                                model, _textureResolver, 1.0f, 32, spriteSize, azimuth, elevation, spriteSize);
                            if (result == null)
                            {
                                continue;
                            }

                            var name = NpcTextureHelpers.BuildNpcRenderName(npc);
                            var fileName = $"{name}{suffix}.png";
                            PngWriter.SaveRgba(result.Pixels, result.Width, result.Height,
                                Path.Combine(outputDir, fileName));
                        }
                    }
                    catch
                    {
                        // Skip failures in batch mode
                    }
                    finally
                    {
                        EvictNpcGeneratedTextures(npc);
                    }
                }

                done++;
                progress.Report((done, total, npc.FullName ?? npc.EditorId ?? $"0x{npc.NpcFormId:X8}"));
            }
        }, ct);
    }

    private NpcAppearance? ResolveAppearance(
        uint npcFormId,
        ushort? previewPlayerLevel = null)
    {
        if (_dmpAppearances != null)
        {
            return _dmpAppearances.GetValueOrDefault(npcFormId);
        }

        return _resolver.ResolveHeadOnly(npcFormId, _pluginName, previewPlayerLevel);
    }

    private void CaptureReferencedGeneratedTextures(
        BethesdaViewerScene scene,
        NpcAppearance appearance)
    {
        var referencedDiffusePaths = scene.MeshParts
            .Select(static meshPart => meshPart.Submesh.DiffuseTexturePath)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => NifTexturePathUtility.Normalize(path!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var textureKey in NpcTextureHelpers.BuildNpcGeneratedTextureKeys(appearance))
        {
            if (!referencedDiffusePaths.Contains(NifTexturePathUtility.Normalize(textureKey)))
            {
                continue;
            }

            var texture = _textureResolver.GetTexture(textureKey);
            if (texture != null)
            {
                scene.AddGeneratedTexture(textureKey, texture);
            }
        }
    }

    private void EvictNpcGeneratedTextures(NpcAppearance appearance)
    {
        foreach (var textureKey in NpcTextureHelpers.BuildNpcGeneratedTextureKeys(appearance))
        {
            _textureResolver.EvictTexture(textureKey);
        }
    }

    private static string BuildNpcSourceLabel(NpcAppearance appearance)
    {
        var actorName = appearance.FullName ?? appearance.EditorId ?? $"0x{appearance.NpcFormId:X8}";
        var variant = string.IsNullOrWhiteSpace(appearance.RenderVariantLabel)
            ? string.Empty
            : $" [{appearance.RenderVariantLabel}]";
        var leveledWeapon = appearance.WeaponVisual?.LeveledListTrace;
        var weaponContext = string.Empty;
        if (leveledWeapon is not null)
        {
            weaponContext = leveledWeapon.PreviewPlayerLevel.HasValue
                ? $" [weapon LVLI 0x{leveledWeapon.ListFormId:X8}, preview Lv{leveledWeapon.PreviewPlayerLevel}, tier {leveledWeapon.SelectedEntryLevel}]"
                : $" [weapon LVLI 0x{leveledWeapon.ListFormId:X8} omitted: preview level required]";
        }

        return $"{actorName}{variant} (NPC_ 0x{appearance.NpcFormId:X8}){weaponContext}";
    }

    private static string BuildCreatureSourceLabel(uint creatureFormId, CreatureScanEntry creature)
    {
        var actorName = creature.FullName ?? creature.EditorId ?? $"0x{creatureFormId:X8}";
        return $"{actorName} (CREA 0x{creatureFormId:X8})";
    }

    private List<NpcAppearance> GetAllAppearances()
    {
        if (_dmpAppearances != null)
        {
            return _dmpAppearances.Values.ToList();
        }

        return _resolver.ResolveAllHeadOnly(_pluginName);
    }

    private List<NpcListItem> GetNpcListFromDmp(bool namedOnly)
    {
        return BuildDmpActorList(
            _dmpAppearances!,
            _resolver.GetAllCreatures(),
            _game,
            namedOnly);
    }

    /// <summary>
    ///     Builds the DMP browser's hybrid actor list. Runtime appearances remain authoritative
    ///     for NPCs, while creatures come from the companion ESM used by the existing creature
    ///     composition path.
    /// </summary>
    internal static List<NpcListItem> BuildDmpActorList(
        IReadOnlyDictionary<uint, NpcAppearance> dmpAppearances,
        IReadOnlyDictionary<uint, CreatureScanEntry> creatures,
        BethesdaGame game,
        bool namedOnly)
    {
        ArgumentNullException.ThrowIfNull(dmpAppearances);
        ArgumentNullException.ThrowIfNull(creatures);

        var list = new List<NpcListItem>(dmpAppearances.Count + creatures.Count);

        foreach (var (formId, npc) in dmpAppearances)
        {
            if (namedOnly && string.IsNullOrEmpty(npc.FullName))
            {
                continue;
            }

            list.Add(new NpcListItem(formId, npc.EditorId, npc.FullName, npc.IsFemale, null));
        }

        foreach (var (formId, creature) in creatures)
        {
            if (namedOnly && string.IsNullOrEmpty(creature.FullName))
            {
                continue;
            }

            list.Add(new NpcListItem(
                formId,
                creature.EditorId,
                creature.FullName,
                creature.ResolveBodyModelPath(),
                creature.GetCreatureTypeName(game)));
        }

        list.Sort((a, b) =>
            string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));

        return list;
    }

    private static List<NpcAppearance> FilterBySelection(
        List<NpcAppearance> appearances,
        IReadOnlyList<uint>? selectedFormIds)
    {
        if (selectedFormIds == null || selectedFormIds.Count == 0)
        {
            return appearances;
        }

        var idSet = new HashSet<uint>(selectedFormIds);
        return appearances.Where(npc => idSet.Contains(npc.NpcFormId)).ToList();
    }
}
