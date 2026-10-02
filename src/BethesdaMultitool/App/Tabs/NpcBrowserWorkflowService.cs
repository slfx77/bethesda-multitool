using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using BethesdaMultitool.CLI.Rendering.Nif;
using BethesdaMultitool.CLI;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc;
using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using BethesdaMultitool.Core.Minidump;
using BethesdaMultitool.Core.Semantic.LoadOrder;

namespace BethesdaMultitool;

internal static class NpcBrowserWorkflowService
{
    private static readonly Logger Log = Logger.Instance;

    internal static Task<BsaDiscoveryResult> DiscoverBsaPathsAsync(
        string esmPath,
        string? configuredDataDirectory,
        IProgress<NpcBrowserLoadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            progress?.Report(new NpcBrowserLoadProgress(
                NpcBrowserLoadStage.DiscoveringArchives,
                "Detecting archives..."));
            var timer = Stopwatch.StartNew();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = configuredDataDirectory != null
                    ? BsaDiscovery.Discover(Path.Combine(configuredDataDirectory, Path.GetFileName(esmPath)))
                    : BsaDiscovery.Discover(esmPath);
                timer.Stop();
                var detail =
                    $"meshArchives={result.MeshesBsaPaths.Length} textureArchives={result.TexturesBsaPaths.Length}";
                Log.Info(
                    "NPC Browser load stage={0} elapsed={1:N2} ms {2}.",
                    NpcBrowserLoadStage.DiscoveringArchives,
                    timer.Elapsed.TotalMilliseconds,
                    detail);
                progress?.Report(new NpcBrowserLoadProgress(
                    NpcBrowserLoadStage.DiscoveringArchives,
                    "Detecting archives...",
                    timer.Elapsed,
                    detail));
                return result;
            }
            catch
            {
                timer.Stop();
                Log.Info(
                    "NPC Browser load stage={0} outcome=failed elapsed={1:N2} ms.",
                    NpcBrowserLoadStage.DiscoveringArchives,
                    timer.Elapsed.TotalMilliseconds);
                throw;
            }
        }, cancellationToken);
    }

    internal static Task<NpcBrowserService?> CreateFromEsmAsync(
        string esmPath,
        bool bigEndian,
        BsaDiscoveryResult bsaPaths,
        IProgress<NpcBrowserLoadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            progress?.Report(new NpcBrowserLoadProgress(
                NpcBrowserLoadStage.ReadingEsm,
                "Reading ESM for NPC records..."));
            var readTimer = Stopwatch.StartNew();
            var esmData = File.ReadAllBytes(esmPath);
            readTimer.Stop();
            Log.Info(
                "NPC Browser load stage={0} elapsed={1:N2} ms bytesRead={2:N0} source=whole-file-fallback.",
                NpcBrowserLoadStage.ReadingEsm,
                readTimer.Elapsed.TotalMilliseconds,
                esmData.LongLength);
            progress?.Report(new NpcBrowserLoadProgress(
                NpcBrowserLoadStage.ReadingEsm,
                "Reading ESM for NPC records...",
                readTimer.Elapsed,
                $"bytesRead={esmData.LongLength}"));
            cancellationToken.ThrowIfCancellationRequested();
            return NpcBrowserService.TryCreate(
                esmData,
                bigEndian,
                esmPath,
                bsaPaths,
                progress,
                cancellationToken);
        }, cancellationToken);
    }

    /// <summary>
    ///     Creates the ESM-mode browser from the analysis session's existing memory map and record
    ///     descriptors. This is the SingleFile tab path; the byte-array overload remains the safe
    ///     fallback for callers that do not own an analyzed session.
    /// </summary>
    internal static Task<NpcBrowserService?> CreateFromAnalyzedEsmAsync(
        string esmPath,
        bool bigEndian,
        BsaDiscoveryResult bsaPaths,
        MemoryMappedViewAccessor accessor,
        long fileSize,
        EsmRecordScanResult analyzedRecords,
        IProgress<NpcBrowserLoadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        ArgumentNullException.ThrowIfNull(analyzedRecords);
        return Task.Run(() => NpcBrowserService.TryCreateFromAnalyzedEsm(
            new MmfMemoryAccessor(accessor),
            fileSize,
            analyzedRecords,
            bigEndian,
            esmPath,
            bsaPaths,
            progress,
            cancellationToken), cancellationToken);
    }

    /// <summary>Builds appearances from the same physical winners as selected record details.</summary>
    internal static Task<NpcBrowserService?> CreateFromSelectedViewAsync(
        LoadOrderSelectionView selection,
        string primaryPath,
        BsaDiscoveryResult bsaPaths,
        IProgress<NpcBrowserLoadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            progress?.Report(new NpcBrowserLoadProgress(NpcBrowserLoadStage.DecodingAppearanceRecords,
                "Reading selected NPC appearance records..."));
            var timer = Stopwatch.StartNew();
            var resolver = NpcAppearanceResolver.Build(selection.Order, selection.Index, cancellationToken);
            timer.Stop();
            Log.Info("NPC Browser selected appearance preparation completed elapsed={0:N2} ms.", timer.Elapsed.TotalMilliseconds);
            cancellationToken.ThrowIfCancellationRequested();
            return NpcBrowserService.TryCreateFromResolver(resolver, primaryPath, bsaPaths, progress, cancellationToken);
        }, cancellationToken);
    }

    internal static Task<NpcBrowserService?> CreateFromDmpAsync(
        string dataDirectory,
        MemoryMappedViewAccessor accessor,
        long fileSize,
        MinidumpInfo minidumpInfo,
        EsmRecordScanResult scanResult,
        BsaDiscoveryResult bsaPaths)
    {
        return Task.Run(() =>
        {
            var esmFile = DiscoverEsmFile(dataDirectory);
            if (esmFile == null)
            {
                return null;
            }

            var esmData = File.ReadAllBytes(esmFile);
            var esmBigEndian = NpcBrowserService.DetectEsmBigEndian(esmData);

            return NpcBrowserService.TryCreateFromDmp(
                accessor,
                fileSize,
                minidumpInfo,
                scanResult,
                esmData,
                esmBigEndian,
                esmFile,
                bsaPaths);
        });
    }

    internal static string? DiscoverEsmFile(string dataDir)
    {
        var preferred = Path.Combine(dataDir, "FalloutNV.esm");
        if (File.Exists(preferred))
        {
            return preferred;
        }

        var esmFiles = Directory.GetFiles(dataDir, "*.esm");
        return esmFiles.Length > 0 ? esmFiles[0] : null;
    }

    internal static string BuildDetailText(NpcListItem npc)
    {
        if (npc.IsCreature)
        {
            return $"FormID: 0x{npc.FormId:X8}\n" +
                   $"Editor ID: {npc.EditorId ?? "(none)"}\n" +
                   $"Type: {npc.CreatureTypeName}\n" +
                   $"Model: {npc.ModelPath ?? "(none)"}";
        }

        return $"FormID: 0x{npc.FormId:X8}\n" +
               $"Editor ID: {npc.EditorId ?? "(none)"}\n" +
               $"Gender: {(npc.IsFemale ? "Female" : "Male")}";
    }

    /// <summary>
    ///     Builds the shared renderer-neutral scene used by both the NPC and creature preview
    ///     workflows. Existing GLB callers serialize this scene only after composition completes.
    /// </summary>
    internal static Task<BethesdaViewerScene?> BuildViewerSceneAsync(
        NpcBrowserService service,
        NpcListItem npc,
        NpcRenderOptions options,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var timer = Stopwatch.StartNew();
            Log.Info(
                "NPC Browser actor-scene assembly started formId=0x{0:X8} kind={1}.",
                npc.FormId,
                npc.IsCreature ? "creature" : "npc");
            try
            {
                var scene = npc.IsCreature
                    ? service.BuildCreatureViewerScene(npc.FormId, options.BindPose)
                    : service.BuildViewerScene(
                        npc.FormId,
                        options.HeadOnly,
                        options.NoEquip,
                        options.NoWeapon,
                        options.BindPose,
                        options.PreviewPlayerLevel,
                        options.Generation);
                timer.Stop();
                Log.Info(
                    "NPC Browser actor-scene assembly completed formId=0x{0:X8} outcome={1} " +
                    "elapsed={2:N2} ms parts={3:N0}.",
                    npc.FormId,
                    scene is null ? "no-scene" : "ready",
                    timer.Elapsed.TotalMilliseconds,
                    scene?.MeshParts.Count ?? 0);
                return scene;
            }
            catch
            {
                timer.Stop();
                Log.Info(
                    "NPC Browser actor-scene assembly failed formId=0x{0:X8} elapsed={1:N2} ms.",
                    npc.FormId,
                    timer.Elapsed.TotalMilliseconds);
                throw;
            }
        }, cancellationToken);
    }

    internal static async Task<byte[]?> BuildGlbAsync(
        NpcBrowserService service,
        NpcListItem npc,
        NpcRenderOptions options)
    {
        var scene = await BuildViewerSceneAsync(service, npc, options);
        return scene == null
            ? null
            : await Task.Run(() => service.ExportViewerSceneToGlb(scene));
    }

    internal static async Task ExportGlbAsync(
        NpcBrowserService service,
        NpcListItem npc,
        string outputPath,
        NpcRenderOptions options)
    {
        var glbBytes = await BuildGlbAsync(service, npc, options);
        if (glbBytes != null)
        {
            await File.WriteAllBytesAsync(outputPath, glbBytes);
        }
    }

    internal static async Task<int> RenderPngViewsAsync(
        NpcBrowserService service,
        uint npcFormId,
        string outputPath,
        NpcRenderOptions options,
        int spriteSize,
        CameraConfig camera)
    {
        var views = camera.ResolveViews(defaultAzimuth: 90f);
        foreach (var (suffix, azimuth, elevation) in views)
        {
            var pngBytes = await Task.Run(() =>
                service.RenderPng(
                    npcFormId,
                    options.HeadOnly,
                    options.NoEquip,
                    options.NoWeapon,
                    spriteSize,
                    azimuth,
                    elevation,
                    options.PreviewPlayerLevel,
                    options.Generation));

            if (pngBytes != null)
            {
                var viewOutputPath = views.Length > 1
                    ? Path.Combine(
                        Path.GetDirectoryName(outputPath) ?? ".",
                        Path.GetFileNameWithoutExtension(outputPath) + suffix + ".png")
                    : outputPath;
                await File.WriteAllBytesAsync(viewOutputPath, pngBytes);
            }
        }

        NifConverterWorkflowService.DeleteMultiViewPlaceholder(outputPath, views.Length);
        return views.Length;
    }

    internal static void SetAllSelected(IEnumerable<NpcListItem> items, bool selected)
    {
        foreach (var item in items)
        {
            item.IsSelected = selected;
        }
    }

    internal static List<uint>? GetSelectedFormIds(IEnumerable<NpcListItem> items)
    {
        var selected = items.Where(n => n.IsSelected).Select(n => n.FormId).ToList();
        return selected.Count > 0 ? selected : null;
    }
}
