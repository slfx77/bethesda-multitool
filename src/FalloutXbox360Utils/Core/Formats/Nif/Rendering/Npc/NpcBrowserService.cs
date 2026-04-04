using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.CLI;
using FalloutXbox360Utils.CLI.Rendering.Npc;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis;
using FalloutXbox360Utils.Core.Formats.Esm.Records;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Export;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Composition;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assembly;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assets;
using FalloutXbox360Utils.Core.Minidump;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc;

/// <summary>
///     GUI-facing service wrapping the NPC render/export pipelines.
///     No Spectre.Console dependency — suitable for WinUI 3 consumption.
/// </summary>
internal sealed class NpcBrowserService : IDisposable
{
    private static readonly Logger Log = Logger.Instance;

    // Pre-resolved appearances from DMP (null for ESM-only mode)
    private readonly Dictionary<uint, NpcAppearance>? _dmpAppearances;

    // Shared caches (same as CLI pipelines)
    private readonly NpcCompositionCaches _compositionCaches = new();
    private readonly NpcMeshArchiveSet _meshArchives;
    private readonly string _pluginName;
    private readonly NpcRenderCaches _renderCaches = new();

    private readonly NpcAppearanceResolver _resolver;
    private readonly NifTextureResolver _textureResolver;

    private NpcBrowserService(
        NpcAppearanceResolver resolver,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        string pluginName,
        Dictionary<uint, NpcAppearance>? dmpAppearances = null)
    {
        _resolver = resolver;
        _meshArchives = meshArchives;
        _textureResolver = textureResolver;
        _pluginName = pluginName;
        _dmpAppearances = dmpAppearances;
    }

    public int NpcCount => _dmpAppearances?.Count ?? _resolver.NpcCount;
    public int CreatureCount => _resolver.CreatureCount;
    public int RaceCount => _resolver.RaceCount;
    public bool IsDmpMode => _dmpAppearances != null;

    public void Dispose()
    {
        _meshArchives.Dispose();
        _textureResolver.Dispose();
    }

    public static NpcBrowserService? TryCreate(
        byte[] esmData,
        bool bigEndian,
        string esmPath,
        BsaDiscoveryResult bsaPaths)
    {
        if (!bsaPaths.HasMeshes)
        {
            return null;
        }

        var resolver = NpcAppearanceResolver.Build(esmData, bigEndian);
        var meshArchives = NpcMeshArchiveSet.Open(bsaPaths.MeshesBsaPath!, bsaPaths.ExtraMeshesBsaPaths);
        var textureResolver = new NifTextureResolver(bsaPaths.TexturesBsaPaths);
        var pluginName = Path.GetFileName(esmPath);

        return new NpcBrowserService(resolver, meshArchives, textureResolver, pluginName);
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
        var meshArchives = NpcMeshArchiveSet.Open(bsaPaths.MeshesBsaPath!, bsaPaths.ExtraMeshesBsaPaths);
        var textureResolver = new NifTextureResolver(bsaPaths.TexturesBsaPaths);
        var pluginName = Path.GetFileName(esmPath);

        // Get NPC_ entries from DMP runtime hash table (FormType 0x2A = NPC_)
        var npcEntries = scanResult.RuntimeEditorIds
            .Where(e => e.FormType == 0x2A)
            .ToList();

        if (npcEntries.Count == 0)
        {
            Log.Warn("No NPC_ entries found in DMP runtime hash table");
            meshArchives.Dispose();
            textureResolver.Dispose();
            return null;
        }

        Log.Info("Found {0} NPC_ entries in DMP, resolving appearances...", npcEntries.Count);

        // Create struct reader with auto-detected layout
        var structReader = RuntimeStructReader.CreateWithAutoDetect(
            accessor,
            fileSize,
            minidumpInfo,
            scanResult.RuntimeRefrFormEntries,
            npcEntries);

        // Resolve all NPC appearances from DMP
        var dmpAppearances = new Dictionary<uint, NpcAppearance>();
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

        if (dmpAppearances.Count == 0)
        {
            Log.Warn("No NPC appearances could be resolved from DMP");
            meshArchives.Dispose();
            textureResolver.Dispose();
            return null;
        }

        Log.Info("Resolved {0} NPC appearances from DMP", dmpAppearances.Count);

        return new NpcBrowserService(resolver, meshArchives, textureResolver, pluginName, dmpAppearances);
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

    public List<NpcListItem> GetNpcList(bool namedOnly = false)
    {
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
                creature.CreatureTypeName));
        }

        list.Sort((a, b) =>
            string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));

        return list;
    }

    public byte[]? BuildGlb(uint npcFormId, bool headOnly, bool noEquip, bool noWeapon,
        bool bindPose = false)
    {
        var appearance = ResolveAppearance(npcFormId);
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

        var plan = NpcCompositionPlanner.CreatePlan(
            appearance,
            _meshArchives,
            _textureResolver,
            _compositionCaches,
            NpcCompositionOptions.From(settings));
        var scene = NpcCompositionExportAdapter.BuildNpc(
            plan,
            _meshArchives,
            _textureResolver,
            _compositionCaches);

        if (scene == null || scene.MeshParts.Count == 0)
        {
            return null;
        }

        return NpcGlbWriter.WriteToBytes(scene, _textureResolver);
    }

    public byte[]? BuildCreatureGlb(uint creatureFormId, bool bindPose = false)
    {
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
        var scene = plan == null ? null : NpcCompositionExportAdapter.BuildCreature(plan, _meshArchives);

        if (scene == null || scene.MeshParts.Count == 0)
        {
            return null;
        }

        return NpcGlbWriter.WriteToBytes(scene, _textureResolver);
    }

    public byte[]? RenderPng(
        uint npcFormId,
        bool headOnly,
        bool noEquip,
        bool noWeapon,
        int spriteSize,
        float azimuth,
        float elevation)
    {
        var appearance = ResolveAppearance(npcFormId);
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

    public async Task BatchExportGlbAsync(
        string outputDir,
        bool headOnly,
        bool noEquip,
        bool noWeapon,
        IProgress<(int Done, int Total, string Name)> progress,
        CancellationToken ct,
        IReadOnlyList<uint>? selectedFormIds = null)
    {
        var appearances = FilterBySelection(GetAllAppearances(), selectedFormIds);
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
                        NpcGlbWriter.Write(scene, _textureResolver, outputPath);
                    }
                }
                catch
                {
                    // Skip failures in batch mode
                }
                finally
                {
                    _textureResolver.EvictTexture(NpcTextureHelpers.BuildNpcFaceEgtTextureKey(npc));
                }

                done++;
                progress.Report((done, total, npc.FullName ?? npc.EditorId ?? $"0x{npc.NpcFormId:X8}"));
            }
        }, ct);
    }

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
        var appearances = FilterBySelection(GetAllAppearances(), selectedFormIds);
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
                    _textureResolver.EvictTexture(NpcTextureHelpers.BuildNpcFaceEgtTextureKey(npc));
                }

                done++;
                progress.Report((done, total, npc.FullName ?? npc.EditorId ?? $"0x{npc.NpcFormId:X8}"));
            }
        }, ct);
    }

    private NpcAppearance? ResolveAppearance(uint npcFormId)
    {
        if (_dmpAppearances != null)
        {
            return _dmpAppearances.GetValueOrDefault(npcFormId);
        }

        return _resolver.ResolveHeadOnly(npcFormId, _pluginName);
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
        var list = new List<NpcListItem>(_dmpAppearances!.Count);

        foreach (var (formId, npc) in _dmpAppearances)
        {
            if (namedOnly && string.IsNullOrEmpty(npc.FullName))
            {
                continue;
            }

            list.Add(new NpcListItem(formId, npc.EditorId, npc.FullName, npc.IsFemale, null));
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
