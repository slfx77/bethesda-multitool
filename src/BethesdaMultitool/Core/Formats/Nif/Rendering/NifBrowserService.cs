using System.Diagnostics;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Bsa.Ba2;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Bsa.Models;
using BethesdaMultitool.Core.Formats.Esm.Analysis.Geometry;
using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using BethesdaMultitool.Core.Formats.Nif.Conversion;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Rasterization;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Skinning;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Vfs;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering;

/// <summary>
///     GUI-facing service for browsing, viewing, and exporting individual NIF files.
///     No Spectre.Console dependency — suitable for WinUI 3 consumption.
/// </summary>
internal sealed class NifBrowserService : IDisposable
{
    private static readonly Logger Log = Logger.Instance;
    private readonly ArchiveReader? _archive;
    private readonly ArchiveLease? _archiveLease;
    private readonly string? _archivePath;
    private readonly DeferredArchiveSourceSet? _archiveSourceSet;
    private readonly string[] _eagerTexturePaths;
    private readonly IGameFileSystem _modelFamilyFiles;
    private readonly string? _modelFamilyLooseRoot;
    private readonly string? _rootDirectory;
    private readonly SynchronizedLazyDisposable<MeshArchiveSet>? _siblingMeshArchives;
    private readonly SynchronizedLazyDisposable<NifTextureResolver> _textureResolver;

    private NifBrowserService(
        string? rootDirectory,
        ArchiveLease? archiveLease,
        string[] texturePaths,
        IGameFileSystem modelFamilyFiles,
        string? modelFamilyLooseRoot,
        string? archivePath = null,
        DeferredArchiveSourceSet? archiveSourceSet = null)
    {
        _rootDirectory = rootDirectory;
        _archiveLease = archiveLease;
        _archive = archiveLease?.Reader;
        _archivePath = archivePath;
        _archiveSourceSet = archiveSourceSet;
        _eagerTexturePaths = [.. texturePaths];
        _textureResolver = new SynchronizedLazyDisposable<NifTextureResolver>(() =>
        {
            var resolverPaths = TexturePaths;
            return resolverPaths.Length > 0
                ? new NifTextureResolver(resolverPaths)
                : new NifTextureResolver();
        });
        _modelFamilyFiles = modelFamilyFiles;
        _modelFamilyLooseRoot = modelFamilyLooseRoot;
        if (_archiveSourceSet is not null)
        {
            _siblingMeshArchives = new SynchronizedLazyDisposable<MeshArchiveSet>(() =>
            {
                var siblingPaths = _archiveSourceSet.Get().SiblingMeshArchivePaths;
                if (siblingPaths.Length == 0)
                {
                    throw new InvalidOperationException(
                        "A sibling mesh archive set was requested when discovery found none.");
                }

                return MeshArchiveSet.Open(
                    siblingPaths[0],
                    siblingPaths.Length == 1 ? null : siblingPaths[1..]);
            });
        }
    }

    public bool IsBsaMode => _archive != null;

    /// <summary>
    ///     Ordered texture sources configured for this browser (caller-supplied first, then
    ///     auto-detected). Paths remain metadata until a model/render/export path first borrows the
    ///     lazily owned resolver.
    /// </summary>
    internal string[] TexturePaths =>
        _archiveSourceSet?.Get().TexturePaths ?? _eagerTexturePaths;

    /// <summary>
    ///     Deterministic external-geometry search order. The archive explicitly opened by the user is
    ///     always first; content-discovered siblings follow in ordinal-ignore-case path order with
    ///     an ordinal tie-breaker.
    /// </summary>
    internal IReadOnlyList<string> ExternalMeshArchivePaths
    {
        get
        {
            var siblingPaths = _archiveSourceSet?.Get().SiblingMeshArchivePaths ?? [];
            return _archivePath is null
                ? siblingPaths
                : [_archivePath, .. siblingPaths];
        }
    }

    public void Dispose()
    {
        // A modern extraction can borrow the resolver while its external-mesh callback reads the
        // selected/sibling archives. Close the synchronized use gate before releasing those owners.
        _textureResolver.Dispose();
        _modelFamilyFiles.Dispose();
        _siblingMeshArchives?.Dispose();
        _archiveLease?.Dispose();
    }

    /// <summary>
    ///     Starts archive sibling classification without waiting for it. The source-loading workflow
    ///     uses this to overlap cold GNRL name-table I/O with selected-archive NIF enumeration; all
    ///     consumers still join the same task before observing paths or resolving an asset.
    /// </summary>
    internal Task BeginRelatedArchiveDiscovery()
    {
        return _archiveSourceSet?.Begin() ?? Task.CompletedTask;
    }

    /// <summary>
    ///     Create from a filesystem directory containing NIF files. Caller-supplied texture sources
    ///     have first-hit precedence, while auto-discovered sibling archives / the loose Data root
    ///     remain as fallbacks. Starfield needs that union: one selected override cannot provide both
    ///     <c>materials\materialsbeta.cdb</c> and every DX10 texture shard it references.
    /// </summary>
    internal static NifBrowserService CreateFromDirectory(string rootDir, string[]? texturePaths = null)
    {
        rootDir = Path.GetFullPath(rootDir);
        texturePaths = MergeTextureSources(texturePaths, DiscoverTextureSources(rootDir));
        var modelFamilyLooseRoot = FindModelFamilyLooseRoot(rootDir);
        IGameFileSystem modelFamilyFiles = new DeferredGameFileSystem(
            $"model-family:{modelFamilyLooseRoot}",
            () => CreateDirectoryModelFamilyFileSystem(modelFamilyLooseRoot));
        try
        {
            return new NifBrowserService(
                rootDir,
                null,
                texturePaths,
                modelFamilyFiles,
                modelFamilyLooseRoot);
        }
        catch
        {
            modelFamilyFiles.Dispose();
            throw;
        }
    }

    /// <summary>
    ///     Create from a mesh archive containing NIF files — a classic BSA or a Bethesda Archive 2
    ///     (<c>.ba2</c>), dispatched by magic. Explicit texture paths are priority overrides, not an
    ///     exclusive replacement for discovered siblings; otherwise selecting one Starfield archive
    ///     makes geometry complete while silently dropping either its CDB or most texture shards.
    /// </summary>
    internal static NifBrowserService CreateFromBsa(string archivePath, string[]? texturePaths = null)
    {
        archivePath = Path.GetFullPath(archivePath);

        // Shared handle: the browser is long-lived and read-only, and its meshes archive often
        // overlaps what the 3D viewer / extractor tab already hold open.
        var archiveLease = ArchiveHandleRegistry.Shared.Acquire(archivePath);
        IGameFileSystem? modelFamilyFiles = null;
        try
        {
            // The selected archive is already fully parsed by the shared lease. Classify its parsed
            // paths once, then let deferred directory discovery trust those flags instead of
            // streaming a large GNRL BA2 name table from disk a second time.
            var selectedContent = ClassifyOpenArchiveContent(archiveLease.Reader);
            var preferredTexturePaths = texturePaths?.ToArray() ?? [];
            var archiveSourceSet = new DeferredArchiveSourceSet(() =>
                DiscoverArchiveSourceSet(
                    archivePath,
                    preferredTexturePaths,
                    selectedContent.Meshes,
                    selectedContent.Textures));
            modelFamilyFiles = new DeferredGameFileSystem(
                $"model-family:{archivePath}",
                () => CreateArchiveModelFamilyFileSystem(
                    archivePath,
                    archiveSourceSet.Get().SiblingMeshArchivePaths));
            return new NifBrowserService(
                null,
                archiveLease,
                preferredTexturePaths,
                modelFamilyFiles,
                null,
                archivePath,
                archiveSourceSet);
        }
        catch
        {
            // A failed source-set open must not strand the selected archive's shared registry lease.
            modelFamilyFiles?.Dispose();
            archiveLease.Dispose();
            throw;
        }
    }

    /// <summary>
    ///     List all NIF files available for browsing.
    /// </summary>
    internal List<NifTreeEntry> ListNifFiles(
        Action<NifBrowserScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_archive != null)
        {
            return ListNifFilesFromArchive(
                _archive.EnumerateFilePaths(),
                _archive.TotalFiles,
                progress,
                cancellationToken);
        }

        if (_rootDirectory != null)
        {
            return ListNifFilesFromDirectory(_rootDirectory, progress, cancellationToken);
        }

        return [];
    }

    /// <summary>
    ///     Read NIF file data from the source (filesystem or archive).
    /// </summary>
    internal byte[]? ReadNifData(string path)
    {
        if (_archive != null)
        {
            return _archive.ReadFile(path);
        }

        if (_rootDirectory != null)
        {
            var fullPath = Path.IsPathRooted(path) ? path : Path.Combine(_rootDirectory, path);
            return File.Exists(fullPath) ? File.ReadAllBytes(fullPath) : null;
        }

        return null;
    }

    /// <summary>
    ///     Parse NIF header and return viewer info.
    /// </summary>
    internal static NifViewerInfo? GetNifInfo(byte[] nifData, string fileName)
    {
        var nif = NifParser.Parse(nifData);
        if (nif == null) return null;

        return new NifViewerInfo
        {
            FileName = fileName,
            BlockCount = nif.BlockCount,
            Format = nif.IsBigEndian ? "Xbox 360 (BE)" : "PC (LE)",
            BsVersion = nif.BsVersion,
            UserVersion = nif.UserVersion,
            BlockTypeNames = nif.BlockTypeNames.Distinct().OrderBy(n => n).ToList(),
            FileSize = nifData.Length
        };
    }

    /// <summary>
    ///     Builds a best-effort GLB from NIF data. This compatibility entry point intentionally
    ///     preserves partial-output behavior when an external geometry blob is unavailable; callers
    ///     that need to prove completeness must use <see cref="BuildGlbWithDiagnostics" />.
    /// </summary>
    internal byte[]? BuildGlb(byte[] nifData, string sourceLabel)
    {
        return BuildGlbWithDiagnostics(nifData, sourceLabel).GlbBytes;
    }

    /// <summary>
    ///     Builds the renderer-neutral scene first, then projects it to GLB for the explicit export
    ///     and temporary WebView compatibility paths. GLB is deliberately downstream of the viewer
    ///     scene so the native viewer never has to recover Bethesda semantics from glTF extras.
    /// </summary>
    internal NifBrowserGlbBuildResult BuildGlbWithDiagnostics(byte[] nifData, string sourceLabel)
    {
        var build = BuildViewerSceneWithDiagnostics(nifData, sourceLabel);
        return new NifBrowserGlbBuildResult(
            build.Scene is null ? null : ExportViewerSceneToGlb(build.Scene),
            build.ExternalGeometry);
    }

    /// <summary>
    ///     Builds the native raw-NIF viewer scene and reports every Starfield external-geometry
    ///     reference. One surviving primitive is not proof that the model is complete: one NIF can
    ///     reference blobs split across multiple <c>Starfield - Meshes*.ba2</c> archives.
    /// </summary>
    internal NifBrowserViewerSceneBuildResult BuildViewerSceneWithDiagnostics(
        byte[] nifData,
        string sourceLabel,
        string? sourcePath = null,
        INifModelFamilyRigInspector? modelFamilyRigInspector = null)
    {
        var externalGeometryResolutions = new List<NifExternalGeometryResolution>();
        var externalGeometryDecodeFailures = new List<string>();
        var detectedGame = BethesdaGame.Unknown;

        NifBrowserViewerSceneBuildResult Finish(
            GlbScene? exportScene,
            byte[]? parsedData = null,
            NifInfo? parsedNif = null)
        {
            NifModelFamilyAnimationCatalog? modelFamilyAnimations = null;
            if (exportScene is not null && parsedData is not null && parsedNif is not null)
            {
                try
                {
                    modelFamilyAnimations = modelFamilyRigInspector is null
                        ? ResolveModelFamilyAnimations(sourcePath ?? sourceLabel, parsedData)
                        : ResolveModelFamilyAnimations(
                            sourcePath ?? sourceLabel,
                            parsedData,
                            modelFamilyRigInspector);

                    // A custom rig inspector is a path/VFS test seam whose synthetic skeleton bytes
                    // are not necessarily parseable NIFs. Production discovery always uses the real
                    // inspector and must install the selected canonical rest graph before any KF can
                    // bind to this scene.
                    if (modelFamilyRigInspector is null &&
                        modelFamilyAnimations.Skeleton is { } skeleton)
                    {
                        if (!TryReadModelFamilySkeletonData(
                                skeleton,
                                out var skeletonData,
                                out var skeletonReadDiagnostic))
                        {
                            modelFamilyAnimations = WithholdModelFamilyAnimations(
                                modelFamilyAnimations,
                                skeletonReadDiagnostic);
                            Log.Warn(
                                "NifBrowserService: canonical rig for '{0}' was not applied: {1}",
                                sourcePath ?? sourceLabel,
                                skeletonReadDiagnostic);
                        }
                        else if (!BethesdaViewerExternalSkeletonRigAdapter.TryApply(
                                     exportScene,
                                     skeletonData!,
                                     out var reboundScene,
                                     out var rigDiagnostic))
                        {
                            // Keep the raw model viewable in its authored static state, but expose no
                            // standalone KF that could animate its flattened/ambiguous bone stubs.
                            modelFamilyAnimations = WithholdModelFamilyAnimations(
                                modelFamilyAnimations,
                                rigDiagnostic);
                            Log.Warn(
                                "NifBrowserService: canonical rig for '{0}' was rejected: {1}",
                                sourcePath ?? sourceLabel,
                                rigDiagnostic);
                        }
                        else
                        {
                            exportScene = reboundScene;
                            Log.Info(
                                "NifBrowserService: canonical rig for '{0}' applied: {1}",
                                sourcePath ?? sourceLabel,
                                rigDiagnostic);

                            if (TryAttachModelFamilyRigidHead(
                                    reboundScene,
                                    modelFamilyAnimations.ModelPath,
                                    out var assembledScene,
                                    out var siblingDiagnostic))
                            {
                                exportScene = assembledScene;
                                Log.Info(
                                    "NifBrowserService: rigid model-family sibling for '{0}' applied: {1}",
                                    sourcePath ?? sourceLabel,
                                    siblingDiagnostic);
                            }
                            else if (!string.IsNullOrWhiteSpace(siblingDiagnostic))
                            {
                                // The canonical body rig remains valid and its KFs remain safe to
                                // expose. A malformed optional rigid sibling must not undo that work.
                                Log.Warn(
                                    "NifBrowserService: rigid model-family sibling for '{0}' was ignored: {1}",
                                    sourcePath ?? sourceLabel,
                                    siblingDiagnostic);
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and
                                               not StackOverflowException and
                                               not OperationCanceledException)
                {
                    // External animation discovery/application is optional presentation data. It
                    // must not turn a renderable raw model into a failed Mesh Viewer load.
                    Log.Warn(
                        "NifBrowserService: model-family animation setup for '{0}' was ignored: {1}",
                        sourcePath ?? sourceLabel,
                        ex.Message);
                    modelFamilyAnimations = null;
                }
            }

            var viewerScene = exportScene is null
                ? null
                : BethesdaViewerSceneGlbAdapter.FromGlbScene(
                    exportScene,
                    sourceLabel,
                    BethesdaViewerScenePurpose.RawNif,
                    game: detectedGame,
                    textureSourcePaths: TexturePaths);
            if (viewerScene is not null && parsedData is not null && parsedNif is not null)
            {
                if (modelFamilyAnimations is not null)
                {
                    viewerScene.SetModelFamilyAnimations(modelFamilyAnimations);
                }

                NifMeshAnimation? animation = null;
                try
                {
                    // The placed-world collectors intentionally erase file-root motion because REFR
                    // placement replaces it. A standalone asset viewer has no REFR and must retain
                    // the authored root transform/controller.
                    animation = NifNodeKeyframeTrackCollector.Collect(
                        parsedData,
                        parsedNif,
                        true);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and
                                               not StackOverflowException and
                                               not OperationCanceledException)
                {
                    // Animation is optional presentation data. A malformed controller must never
                    // turn otherwise renderable geometry into a failed Mesh Viewer load. A broken
                    // per-node chain must not prevent the independent sequence collector below.
                    Log.Warn(
                        "NifBrowserService: embedded node animation for '{0}' was ignored: {1}",
                        sourceLabel,
                        ex.Message);
                }

                if (animation is null)
                {
                    try
                    {
                        animation = NifControllerSequenceTrackCollector.Collect(
                            parsedData,
                            parsedNif,
                            true);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException and
                                                   not StackOverflowException and
                                                   not OperationCanceledException)
                    {
                        Log.Warn(
                            "NifBrowserService: embedded sequence animation for '{0}' was ignored: {1}",
                            sourceLabel,
                            ex.Message);
                    }
                }

                if (animation is not null)
                {
                    try
                    {
                        var clip = BethesdaViewerNifAnimationAdapter.TryCreateClip(
                            viewerScene,
                            animation);
                        if (clip is not null)
                        {
                            viewerScene.AnimationClips.Add(clip);
                        }

                        // Keep the ambient/text-key-selected Embedded Idle first. A compatible
                        // TES3 CYCLE_REVERSE graph additionally exposes its complete controller
                        // window so the native viewer can seek the authored forward/backward pass.
                        var controllerCycleClip =
                            BethesdaViewerNifAnimationAdapter.TryCreateFullControllerCycleClip(
                                viewerScene,
                                animation);
                        if (controllerCycleClip is not null)
                        {
                            viewerScene.AnimationClips.Add(controllerCycleClip);
                        }
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException and
                                                   not StackOverflowException and
                                                   not OperationCanceledException)
                    {
                        Log.Warn(
                            "NifBrowserService: embedded animation binding for '{0}' was ignored: {1}",
                            sourceLabel,
                            ex.Message);
                    }
                }

                // Geometry morph discovery is independent of node-track collection: a flag can
                // animate only vertex positions while every scene node remains static.
                try
                {
                    if (BethesdaViewerGeometryMorphPolicy.TryCreateClip(
                            parsedData, parsedNif, viewerScene, out var morphClip, out var morphError))
                    {
                        if (morphClip is not null) viewerScene.AnimationClips.Add(morphClip);
                    }
                    else
                    {
                        Log.Warn("NifBrowserService: embedded geometry morph for '{0}' was ignored: {1}",
                            sourceLabel, morphError);
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and
                                               not StackOverflowException and
                                               not OperationCanceledException)
                {
                    Log.Warn("NifBrowserService: embedded geometry morph for '{0}' was ignored: {1}",
                        sourceLabel, ex.Message);
                }
            }

            return new NifBrowserViewerSceneBuildResult(
                viewerScene,
                new NifExternalGeometryDiagnostics(
                    externalGeometryResolutions.ToArray(),
                    externalGeometryDecodeFailures.ToArray()));
        }

        byte[]? LoadExternalGeometry(string meshPath)
        {
            var normalized = NormalizeExternalGeometryPath(meshPath);
            if (normalized is null)
            {
                externalGeometryResolutions.Add(
                    new NifExternalGeometryResolution(meshPath, false, null));
                return null;
            }

            var bytes = ReadExternalGeometryBlob(normalized, out var sourcePath);
            externalGeometryResolutions.Add(
                new NifExternalGeometryResolution(normalized, bytes is not null, sourcePath));
            return bytes;
        }

        void RecordExternalGeometryDecodeFailure(string meshPath)
        {
            externalGeometryDecodeFailures.Add(NormalizeExternalGeometryPath(meshPath) ?? meshPath);
        }

        var (data, nif) = ParseAndConvert(nifData);
        if (nif == null) return Finish(null);
        detectedGame = DetectViewerGame(nif);

        // The hierarchy-preserving exporter handles classic NiTriShapeData/NiTriStripsData. Modern
        // games instead keep geometry in the shape itself (FO4/FO76 BSTriShape variants), or in a
        // separate geometries\*.mesh blob named by the shape (Starfield BSGeometry). Route those
        // NIFs through the same modern extractor used by the world renderer, then bridge its already-
        // transformed rigid submeshes into GLB. This keeps legacy skinned export behavior unchanged.
        GlbScene? scene;
        if (nif.Blocks.Any(block => NifSceneGraphWalker.SelfContainedShapeTypes.Contains(block.TypeName)))
        {
            var model = _textureResolver.Use(textureResolver =>
                NifGeometryExtractor.Extract(
                    data,
                    nif,
                    textureResolver,
                    externalMeshLoader: LoadExternalGeometry,
                    onExternalMeshDecodeFailure: RecordExternalGeometryDecodeFailure));
            if (model is not null)
            {
                AddStarfieldNormalMapRequests(model);
            }

            var hasFo76SkinCandidate = Enumerable.Range(0, nif.Blocks.Count)
                .Any(shapeIndex => Fo76BsSkinBindingExtractor.IsCandidate(data, nif, shapeIndex));
            if (hasFo76SkinCandidate)
            {
                scene = NifExportSceneBuilder.Build(data, nif, sourceLabel);
                if (scene is not null && model is not null)
                {
                    // The hierarchy path owns joints/weights/inverse binds; the modern renderer path
                    // owns BGSM-expanded normal maps and render state. Merge by source block so Mesh
                    // Viewer gains skinning without regressing the material it already displayed.
                    NifExportSceneBuilder.ApplyModernMaterialState(scene, model);
                }
            }
            else
            {
                scene = model is null ? null : NifExportSceneBuilder.BuildRenderableModel(model, sourceLabel);
            }
        }
        else
        {
            scene = NifExportSceneBuilder.Build(data, nif, sourceLabel);
        }

        if (scene == null || scene.MeshParts.Count == 0) return Finish(null);

        return Finish(scene, data, nif);
    }

    /// <summary>
    ///     Resolves a selected NIF's canonical external skeleton and bounded KF family through the
    ///     browser-owned VFS. The returned catalog is metadata only; it contains no archive reader,
    ///     lease, stream, or payload delegate and remains safe to snapshot onto a viewer scene.
    /// </summary>
    internal NifModelFamilyAnimationCatalog ResolveModelFamilyAnimations(
        string modelPath,
        byte[] modelData)
    {
        return NifModelFamilyAnimationResolver.Resolve(
            _modelFamilyFiles,
            ToModelFamilyVirtualPath(modelPath),
            modelData);
    }

    /// <summary>Test seam for path/VFS wiring without manufacturing a binary skinned NIF.</summary>
    internal NifModelFamilyAnimationCatalog ResolveModelFamilyAnimations(
        string modelPath,
        byte[] modelData,
        INifModelFamilyRigInspector rigInspector)
    {
        return NifModelFamilyAnimationResolver.Resolve(
            _modelFamilyFiles,
            ToModelFamilyVirtualPath(modelPath),
            modelData,
            rigInspector);
    }

    private bool TryReadModelFamilySkeletonData(
        NifModelFamilySkeletonAsset asset,
        out byte[]? data,
        out string diagnostic)
    {
        data = null;
        diagnostic = string.Empty;
        if (!IsSafeModelFamilyVirtualPath(asset.VirtualPath, ".nif") ||
            string.IsNullOrWhiteSpace(asset.Source) ||
            asset.Size < 0 ||
            asset.Size > NifModelFamilyAnimationResolver.MaximumSkeletonPayloadBytes)
        {
            diagnostic = "The canonical skeleton catalog entry violates its path/size safety contract.";
            return false;
        }

        var current = _modelFamilyFiles.TryReadAllBytesBounded(
            asset.VirtualPath,
            NifModelFamilyAnimationResolver.MaximumSkeletonPayloadBytes);
        if (current is null)
        {
            diagnostic = "The canonical skeleton is no longer readable from the model-family source.";
            return false;
        }

        if (!string.Equals(current.Entry.Path, asset.VirtualPath, StringComparison.OrdinalIgnoreCase) ||
            current.Entry.Size != asset.Size ||
            !string.Equals(current.Entry.Source, asset.Source, StringComparison.OrdinalIgnoreCase))
        {
            diagnostic =
                "The canonical skeleton's path, size, or winning source changed during scene assembly.";
            return false;
        }

        data = current.Data;
        return true;
    }

    /// <summary>
    ///     Adds the exact classic creature <c>*head.nif</c> sibling after the canonical external
    ///     skeleton has been installed. Absence is normal; presence plus an invalid/ambiguous rigid
    ///     hierarchy fails closed and leaves <paramref name="riggedScene" /> untouched.
    /// </summary>
    private bool TryAttachModelFamilyRigidHead(
        GlbScene riggedScene,
        string modelPath,
        out GlbScene result,
        out string? diagnostic)
    {
        result = riggedScene;
        diagnostic = null;
        if (!BethesdaViewerRigidSiblingAssembler.TryResolveHeadSibling(
                modelPath,
                out var siblingPath,
                out var targetNodeName))
        {
            return false;
        }

        try
        {
            var entry = _modelFamilyFiles.TryStat(siblingPath);
            if (entry is null)
            {
                return false;
            }

            if (entry.Size < 0 || entry.Size > BethesdaViewerRigidSiblingAssembler.MaximumPayloadBytes)
            {
                diagnostic =
                    $"Exact sibling '{siblingPath}' exceeds the bounded rigid-attachment payload contract.";
                return false;
            }

            var current = _modelFamilyFiles.TryReadAllBytesBounded(
                siblingPath,
                BethesdaViewerRigidSiblingAssembler.MaximumPayloadBytes);
            if (current is null ||
                !string.Equals(current.Entry.Path, siblingPath, StringComparison.OrdinalIgnoreCase) ||
                current.Entry.Size != entry.Size ||
                !string.Equals(current.Entry.Source, entry.Source, StringComparison.OrdinalIgnoreCase))
            {
                diagnostic = $"Exact sibling '{siblingPath}' changed or became unreadable during scene assembly.";
                return false;
            }

            var (siblingData, siblingNif) = ParseAndConvert(current.Data);
            if (siblingNif is null)
            {
                diagnostic = $"Exact sibling '{siblingPath}' is not a readable NIF.";
                return false;
            }

            var siblingScene = NifExportSceneBuilder.Build(siblingData, siblingNif, siblingPath);
            if (siblingScene is null)
            {
                diagnostic = $"Exact sibling '{siblingPath}' contains no supported renderable parts.";
                return false;
            }

            var attached = BethesdaViewerRigidSiblingAssembler.TryAttach(
                riggedScene,
                siblingScene,
                siblingPath,
                targetNodeName,
                out result,
                out var attachDiagnostic);
            diagnostic = attachDiagnostic;
            return attached;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and
                                       not StackOverflowException and
                                       not OperationCanceledException)
        {
            result = riggedScene;
            diagnostic = $"Exact sibling '{siblingPath}' could not be assembled ({ex.GetType().Name}).";
            return false;
        }
    }

    private static NifModelFamilyAnimationCatalog WithholdModelFamilyAnimations(
        NifModelFamilyAnimationCatalog catalog,
        string diagnostic)
    {
        var combinedDiagnostic = string.IsNullOrWhiteSpace(catalog.Diagnostic)
            ? diagnostic
            : catalog.Diagnostic + " " + diagnostic;
        return catalog with
        {
            Animations = [],
            Diagnostic = combinedDiagnostic +
                         " Standalone KF playback was withheld; the raw model remains viewable."
        };
    }

    /// <summary>
    ///     Lazily reads one catalog-selected KF while preserving the catalog's winning source.
    ///     Metadata is revalidated before payload access, and both loose/archive paths enforce the
    ///     viewer's strict 64 MiB stored/decompressed allocation bound before parsing.
    /// </summary>
    internal byte[] ReadModelFamilyAnimationData(
        NifModelFamilyAnimationAsset asset,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsSafeKfVirtualPath(asset.VirtualPath) ||
            string.IsNullOrWhiteSpace(asset.Source) ||
            asset.Size < 0 ||
            asset.Size > BethesdaViewerKfAnimationBinder.MaximumPayloadBytes)
        {
            throw new InvalidDataException(
                "The selected KF catalog entry violates the 64 MiB path/size safety contract.");
        }

        var current = _modelFamilyFiles.TryStat(asset.VirtualPath) ??
                      throw new FileNotFoundException(
                          "The selected KF is no longer present in the current model-family source.",
                          asset.VirtualPath);
        if (current.Size != asset.Size ||
            !string.Equals(current.Source, asset.Source, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The selected KF's size or winning source changed after its catalog was published; " +
                "reload the model before trying again.");
        }

        if (current.Size > BethesdaViewerKfAnimationBinder.MaximumPayloadBytes)
        {
            throw new InvalidDataException("KF exceeds the 64 MiB safety limit.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        byte[] data;
        var isLooseSource = _modelFamilyLooseRoot is not null &&
                            string.Equals(
                                current.Source,
                                _modelFamilyLooseRoot,
                                StringComparison.OrdinalIgnoreCase);
        if (isLooseSource)
        {
            data = ReadBoundedLooseModelFamilyFile(
                current.Path,
                BethesdaViewerKfAnimationBinder.MaximumPayloadBytes);
        }
        else
        {
            if (!File.Exists(current.Source))
            {
                throw new FileNotFoundException(
                    "The archive containing the selected KF is no longer available.",
                    current.Source);
            }

            using var archiveLease = ArchiveHandleRegistry.Shared.Acquire(current.Source);
            var archiveEntry = archiveLease.Reader.FindEntry(current.Path) ??
                               throw new FileNotFoundException(
                                   "The selected KF is no longer present in its catalogued archive.",
                                   current.Path);
            if (archiveEntry.Size != current.Size)
            {
                throw new InvalidDataException(
                    "The selected KF's archive metadata changed after its catalog was published; " +
                    "reload the model before trying again.");
            }

            data = archiveLease.Reader.ExtractBounded(
                archiveEntry,
                BethesdaViewerKfAnimationBinder.MaximumPayloadBytes);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (data.LongLength != current.Size &&
            // Classic BSA catalog sizes describe stored bytes (and may include an embedded name),
            // so exact extracted length is unavailable until the bounded read has completed.
            (isLooseSource ||
             !string.Equals(Path.GetExtension(current.Source), ".bsa", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                "The selected KF's extracted size no longer matches its catalog metadata.");
        }

        return data;
    }

    /// <summary>
    ///     Export-only projection of a native viewer scene. Animation channels unsupported by the
    ///     legacy GLB DTO remain native; static node, mesh, material, and skin state is handed to the
    ///     existing GLB writer unchanged.
    /// </summary>
    internal byte[] ExportViewerSceneToGlb(BethesdaViewerScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        return _textureResolver.Use(textureResolver =>
            GlbWriter.WriteToBytes(
                BethesdaViewerSceneGlbAdapter.ToGlbScene(scene),
                textureResolver));
    }

    /// <summary>
    ///     NIF BS versions are unambiguous for the modern renderer families targeted by the native
    ///     viewer. Older streams remain Unknown here because several games share their versions;
    ///     their owning plugin/session can supply stronger identity later.
    /// </summary>
    private static BethesdaGame DetectViewerGame(NifInfo nif)
    {
        return nif.BsVersion switch
        {
            >= 170 => BethesdaGame.Starfield,
            >= 155 => BethesdaGame.Fallout76,
            >= 130 => BethesdaGame.Fallout4,
            _ => BethesdaGame.Unknown
        };
    }

    /// <summary>
    ///     Render NIF to PNG sprite bytes.
    /// </summary>
    internal byte[]? RenderPng(byte[] nifData, string sourceLabel, int spriteSize,
        float azimuth, float elevation)
    {
        var (data, nif) = ParseAndConvert(nifData);
        if (nif == null) return null;

        return _textureResolver.Use(textureResolver =>
        {
            var model = NifGeometryExtractor.Extract(
                data,
                nif,
                textureResolver,
                externalMeshLoader: ReadExternalGeometryBlob);
            if (model == null || !model.HasGeometry) return null;
            AddStarfieldNormalMapRequests(model);

            var result = NifSpriteRenderer.Render(
                model,
                textureResolver,
                1.0f,
                32,
                spriteSize,
                azimuth,
                elevation,
                spriteSize);
            if (result == null) return null;

            return PngWriter.EncodeRgba(result.Pixels, result.Width, result.Height);
        });
    }

    /// <summary>
    ///     Starfield's NIF exposes one <c>.mat</c> identity rather than separate diffuse/normal paths.
    ///     The D3D12 cache derives its normal request after decoded-mesh caching; do the equivalent for
    ///     GLB preview so Three.js receives the material database's authored normal slot too.
    /// </summary>
    private static void AddStarfieldNormalMapRequests(NifRenderableModel model)
    {
        foreach (var submesh in model.Submeshes)
        {
            if (submesh.NormalMapTexturePath is null &&
                submesh.DiffuseTexturePath is { } materialPath &&
                MaterialTexturePathResolver.IsStarfieldMaterialPath(materialPath))
            {
                submesh.NormalMapTexturePath =
                    MaterialTexturePathResolver.BuildStarfieldNormalMapRequest(materialPath);
            }
        }
    }

    /// <summary>
    ///     Resolves the path fragment stored by a Starfield <c>BSGeometry</c> block to its external
    ///     <c>geometries\*.mesh</c> payload in the opened BA2, its content-discovered sibling mesh
    ///     archives, or a loose Data tree.
    /// </summary>
    private byte[]? ReadExternalGeometryBlob(string meshPath)
    {
        var normalized = NormalizeExternalGeometryPath(meshPath);
        return normalized is null ? null : ReadExternalGeometryBlob(normalized, out _);
    }

    private byte[]? ReadExternalGeometryBlob(string normalizedPath, out string? sourcePath)
    {
        sourcePath = null;

        if (_archive != null)
        {
            var primaryBytes = _archive.ReadFile(normalizedPath);
            if (primaryBytes is not null)
            {
                sourcePath = _archivePath;
                return primaryBytes;
            }

            // Most Starfield blobs are beside their NIF in the opened archive. Build/index sibling
            // archives only after that fast path misses, so FO76/classic browsing pays no extra open
            // cost and single-archive Starfield meshes do not acquire unnecessary leases.
            var siblingPaths = _archiveSourceSet?.Get().SiblingMeshArchivePaths ?? [];
            if (siblingPaths.Length > 0 && _siblingMeshArchives is not null)
            {
                var sibling = _siblingMeshArchives.Use(archives =>
                {
                    return archives.TryExtractFile(
                        normalizedPath,
                        out var siblingBytes,
                        out var siblingArchivePath)
                        ? (Bytes: (byte[]?)siblingBytes, ArchivePath: (string?)siblingArchivePath)
                        : (Bytes: null, ArchivePath: null);
                });
                if (sibling.Bytes is not null)
                {
                    sourcePath = sibling.ArchivePath;
                    return sibling.Bytes;
                }
            }

            return null;
        }

        if (_rootDirectory == null)
        {
            return null;
        }

        // A user may select either the Data root or its meshes subfolder. In the latter case,
        // Starfield's geometries directory is a sibling of meshes.
        var roots = new List<string> { _rootDirectory };
        if (string.Equals(
                Path.GetFileName(Path.TrimEndingDirectorySeparator(_rootDirectory)),
                "meshes",
                StringComparison.OrdinalIgnoreCase) &&
            Path.GetDirectoryName(_rootDirectory) is { } dataRoot)
        {
            roots.Add(dataRoot);
        }

        foreach (var root in roots)
        {
            var candidate = Path.Combine(root, normalizedPath.Replace('\\', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
            {
                sourcePath = candidate;
                return File.ReadAllBytes(candidate);
            }
        }

        return null;
    }

    private static string? NormalizeExternalGeometryPath(string meshPath)
    {
        var normalized = meshPath.Replace('/', '\\').Trim().TrimStart('\\');
        if (normalized.Length == 0 ||
            normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries)
                .Any(part => part is "." or ".."))
        {
            return null;
        }

        if (!normalized.StartsWith("geometries\\", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "geometries\\" + normalized;
        }

        if (!normalized.EndsWith(".mesh", StringComparison.OrdinalIgnoreCase))
        {
            normalized += ".mesh";
        }

        return normalized;
    }

    #region Private Helpers

    private sealed record ArchiveSourceSet(
        string[] TexturePaths,
        string[] SiblingMeshArchivePaths);

    /// <summary>
    ///     Single-flight owner for the only part of archive source opening that must inspect every
    ///     sibling GNRL name table. Callers can start the work for overlap or synchronously join it
    ///     on first texture/source metadata access or a selected-archive geometry miss.
    /// </summary>
    private sealed class DeferredArchiveSourceSet
    {
        private readonly Lazy<Task<ArchiveSourceSet>> _discovery;

        internal DeferredArchiveSourceSet(Func<ArchiveSourceSet> factory)
        {
            ArgumentNullException.ThrowIfNull(factory);
            _discovery = new Lazy<Task<ArchiveSourceSet>>(
                () => StartObserved(factory),
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        internal Task<ArchiveSourceSet> Begin()
        {
            return _discovery.Value;
        }

        internal ArchiveSourceSet Get()
        {
            return _discovery.Value.GetAwaiter().GetResult();
        }

        private static Task<ArchiveSourceSet> StartObserved(Func<ArchiveSourceSet> factory)
        {
            var task = Task.Run(factory);
            _ = task.ContinueWith(
                static completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return task;
        }
    }

    /// <summary>
    ///     Delays both loose/archive family mount construction and archive discovery until the
    ///     resolver actually touches the VFS. In particular, an invalid or unskinned NIF returns
    ///     after rig inspection without opening or enumerating any sibling animation source.
    /// </summary>
    private sealed class DeferredGameFileSystem : IGameFileSystem
    {
        private readonly SynchronizedLazyDisposable<IGameFileSystem> _files;

        internal DeferredGameFileSystem(string label, Func<IGameFileSystem> factory)
        {
            Label = label;
            _files = new SynchronizedLazyDisposable<IGameFileSystem>(factory);
        }

        public string Label { get; }

        public bool Exists(string path)
        {
            return _files.Use(files => files.Exists(path));
        }

        public GameFileEntry? TryStat(string path)
        {
            return _files.Use(files => files.TryStat(path));
        }

        public byte[]? TryReadAllBytes(string path)
        {
            return _files.Use(files => files.TryReadAllBytes(path));
        }

        public GameFileReadResult? TryReadAllBytesBounded(string path, long maximumBytes)
        {
            return _files.Use(files => files.TryReadAllBytesBounded(path, maximumBytes));
        }

        public IEnumerable<GameFileEntry> EnumerateFiles(string? prefix = null)
        {
            // Materialize while the synchronized owner is held: returning a provider's lazy
            // iterator would let browser disposal race archive enumeration outside the gate.
            return _files.Use(files => files.EnumerateFiles(prefix).ToArray());
        }

        public GameFileEnumerationPage EnumerateFilesBounded(
            string? prefix,
            int maximumEntries)
        {
            return _files.Use(files => files.EnumerateFilesBounded(prefix, maximumEntries));
        }

        public void Dispose()
        {
            _files.Dispose();
        }
    }

    /// <summary>
    ///     Mounts loose assets first, followed by content-classified mesh archives. Archive layers
    ///     are lazy and registry-backed: opening a folder does not parse them, and a later KF lookup
    ///     shares handles with every other browser/render pipeline using the same archive.
    /// </summary>
    private static IGameFileSystem CreateDirectoryModelFamilyFileSystem(string looseRoot)
    {
        var layers = new List<IGameFileSystem> { new LooseFileSystem(looseRoot) };
        var archivePaths = BsaDiscovery.DiscoverInDirectory(looseRoot).MeshesBsaPaths
            .Select(Path.GetFullPath)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static path => path, StringComparer.Ordinal)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var archivePath in archivePaths)
        {
            layers.Add(ArchiveFileSystem.CreateLazy(archivePath, ArchiveHandleRegistry.Shared));
        }

        return layers.Count == 1
            ? layers[0]
            : new LayeredGameFileSystem(layers);
    }

    private byte[] ReadBoundedLooseModelFamilyFile(string virtualPath, long maximumBytes)
    {
        var normalized = VfsPath.Normalize(virtualPath);
        if (_modelFamilyLooseRoot is null ||
            !IsSafeKfVirtualPath(normalized))
        {
            throw new InvalidDataException("The selected KF path is not a safe loose-file path.");
        }

        var osRelative = Path.DirectorySeparatorChar == '\\'
            ? normalized
            : normalized.Replace('\\', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(_modelFamilyLooseRoot, osRelative));
        var relativeCheck = Path.GetRelativePath(_modelFamilyLooseRoot, fullPath);
        if (Path.IsPathRooted(relativeCheck) ||
            relativeCheck.Equals("..", StringComparison.Ordinal) ||
            relativeCheck.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The selected KF path escapes the loose asset root.");
        }

        using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        var length = stream.Length;
        if (length < 0 || length > maximumBytes)
        {
            throw new InvalidDataException("KF exceeds the 64 MiB safety limit.");
        }

        var data = new byte[checked((int)length)];
        stream.ReadExactly(data);
        if (stream.Length != length)
        {
            throw new InvalidDataException("KF changed while it was being read.");
        }

        return data;
    }

    private static bool IsSafeKfVirtualPath(string path)
    {
        return IsSafeModelFamilyVirtualPath(path, ".kf");
    }

    private static bool IsSafeModelFamilyVirtualPath(string path, string extension)
    {
        var normalized = VfsPath.Normalize(path);
        return normalized.Length is > 0 and <= 4096 &&
               !Path.IsPathRooted(normalized) &&
               normalized.EndsWith(extension, StringComparison.OrdinalIgnoreCase) &&
               normalized.Split('\\').Length <= 256 &&
               normalized.Split('\\').All(static segment =>
                   segment.Length > 0 &&
                   segment is not "." and not ".." &&
                   !segment.Contains(':') &&
                   !segment.Contains('\0'));
    }

    /// <summary>
    ///     Mounts the archive explicitly selected by the user first, then deterministic sibling mesh
    ///     archives. Every layer acquires its own shared lease lazily; the browser owns and disposes
    ///     the layered VFS, while viewer scenes receive metadata snapshots only.
    /// </summary>
    private static IGameFileSystem CreateArchiveModelFamilyFileSystem(
        string archivePath,
        string[] siblingMeshArchivePaths)
    {
        var layers = new List<IGameFileSystem>(siblingMeshArchivePaths.Length + 1)
        {
            ArchiveFileSystem.CreateLazy(archivePath, ArchiveHandleRegistry.Shared)
        };
        foreach (var siblingPath in siblingMeshArchivePaths)
        {
            layers.Add(ArchiveFileSystem.CreateLazy(siblingPath, ArchiveHandleRegistry.Shared));
        }

        return layers.Count == 1
            ? layers[0]
            : new LayeredGameFileSystem(layers);
    }

    /// <summary>
    ///     Chooses the root against which canonical Data-relative model-family paths are expressed.
    ///     A selected <c>Data\meshes</c> subtree is rebased to Data so loose and archive paths share
    ///     the same <c>meshes\...</c> identity. Arbitrary extracted folders retain their own root.
    /// </summary>
    private static string FindModelFamilyLooseRoot(string selectedRoot)
    {
        selectedRoot = Path.GetFullPath(selectedRoot);
        var dataChild = Path.Combine(selectedRoot, "Data");
        if (Directory.Exists(Path.Combine(dataChild, "meshes")))
        {
            return dataChild;
        }

        for (var current = new DirectoryInfo(selectedRoot); current is not null; current = current.Parent)
        {
            if (string.Equals(current.Name, "meshes", StringComparison.OrdinalIgnoreCase) &&
                current.Parent is { } dataRoot)
            {
                return dataRoot.FullName;
            }
        }

        return selectedRoot;
    }

    private string ToModelFamilyVirtualPath(string modelPath)
    {
        if (_modelFamilyLooseRoot is null)
        {
            return VfsPath.Normalize(modelPath);
        }

        try
        {
            var fullPath = Path.IsPathRooted(modelPath)
                ? Path.GetFullPath(modelPath)
                : Path.GetFullPath(Path.Combine(_rootDirectory ?? _modelFamilyLooseRoot, modelPath));
            return VfsPath.Normalize(Path.GetRelativePath(_modelFamilyLooseRoot, fullPath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Let NifModelFamilyAnimationResolver return its precise InvalidModelPath diagnostic.
            return modelPath;
        }
    }

    /// <summary>
    ///     Content-classifies sibling archives once so texture/material sources and external-geometry
    ///     fallback archives share the same discovery result. Filename matching is deliberately not
    ///     used: a combined <c>&lt;Mod&gt; - Main.bsa</c> may contribute to both sets.
    /// </summary>
    private static BsaDiscoveryResult DiscoverSiblingArchives(
        string bsaPath,
        bool selectedHasMeshes,
        bool selectedHasTextures)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(bsaPath));
        if (dir == null) return BsaDiscoveryResult.Empty;

        return BsaDiscovery.DiscoverInDirectoryWithKnownArchive(
            dir,
            bsaPath,
            selectedHasMeshes,
            selectedHasTextures);
    }

    private static ArchiveSourceSet DiscoverArchiveSourceSet(
        string archivePath,
        IReadOnlyList<string> preferredTexturePaths,
        bool selectedHasMeshes,
        bool selectedHasTextures)
    {
        var discovery = DiscoverSiblingArchives(
            archivePath,
            selectedHasMeshes,
            selectedHasTextures);
        var siblingMeshArchivePaths = discovery.MeshesBsaPaths
            .Select(Path.GetFullPath)
            .Where(path => !string.Equals(path, archivePath, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.Ordinal)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var texturePaths = MergeTextureSources(preferredTexturePaths, discovery.TexturesBsaPaths);
        return new ArchiveSourceSet(texturePaths, siblingMeshArchivePaths);
    }

    private static (bool Meshes, bool Textures) ClassifyOpenArchiveContent(ArchiveReader archive)
    {
        if (archive.Ba2?.Header.Type == Ba2HeaderType.Texture)
        {
            return (false, true);
        }

        if (archive.Bsa is { } bsa && bsa.Header.FileFlags != BsaFileFlags.None)
        {
            return (
                bsa.Header.FileFlags.HasFlag(BsaFileFlags.Meshes),
                bsa.Header.FileFlags.HasFlag(BsaFileFlags.Textures));
        }

        var hasMeshes = false;
        var hasTextures = false;
        foreach (var path in archive.EnumerateFilePaths())
        {
            if (HasArchiveRoot(path, "meshes") || HasArchiveRoot(path, "geometries"))
            {
                hasMeshes = true;
            }
            else if (HasArchiveRoot(path, "textures") || HasArchiveRoot(path, "materials"))
            {
                hasTextures = true;
            }

            if (hasMeshes && hasTextures)
            {
                break;
            }
        }

        return (hasMeshes, hasTextures);
    }

    private static bool HasArchiveRoot(string path, string root)
    {
        return path.Length > root.Length &&
               path.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
               path[root.Length] is '\\' or '/';
    }

    /// <summary>
    ///     Places explicit sources first so they retain first-hit override precedence, then appends
    ///     every distinct auto-discovered source. Full-path comparison prevents the same archive from
    ///     being opened twice when the user selects a sibling that discovery also returns, while the
    ///     original spelling remains visible in the UI's active-source list.
    /// </summary>
    private static string[] MergeTextureSources(
        IReadOnlyList<string>? preferredSources,
        string[] discoveredSources)
    {
        var merged = new List<string>((preferredSources?.Count ?? 0) + discoveredSources.Length);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Add(preferredSources);
        Add(discoveredSources);
        return merged.ToArray();

        void Add(IReadOnlyList<string>? sources)
        {
            if (sources is null)
            {
                return;
            }

            foreach (var source in sources)
            {
                if (string.IsNullOrWhiteSpace(source))
                {
                    continue;
                }

                string comparisonKey;
                try
                {
                    // Directory pickers commonly retain a trailing separator while discovery does
                    // not. Treat those spellings as one source; TrimEndingDirectorySeparator keeps
                    // an actual filesystem root intact.
                    comparisonKey = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
                {
                    // Preserve the old failure surface for an invalid explicit source: the factory
                    // below will report it while opening. This fallback is only a deduplication key.
                    comparisonKey = source;
                }

                if (seen.Add(comparisonKey))
                {
                    merged.Add(source);
                }
            }
        }
    }

    /// <summary>
    ///     Auto-discover texture sources for a directory of NIF files: content-classified texture
    ///     archives in the directory or its parent, then a loose Data-relative asset root.
    /// </summary>
    private static string[] DiscoverTextureSources(string rootDir)
    {
        string[] archiveSources = [];
        foreach (var dir in new[] { rootDir, Path.GetDirectoryName(rootDir) })
        {
            if (dir == null) continue;

            var textures = BsaDiscovery.DiscoverInDirectory(dir).TexturesBsaPaths;
            if (textures.Length > 0)
            {
                archiveSources = textures;
                break;
            }
        }

        var looseAssetRoot = FindLooseAssetRoot(rootDir);
        if (looseAssetRoot is null ||
            archiveSources.Contains(looseAssetRoot, StringComparer.OrdinalIgnoreCase))
        {
            return archiveSources;
        }

        // Keep the pre-existing archive order/precedence; loose assets extend that source set so a
        // sibling materials database (or an asset absent from the archives) can still resolve.
        return [.. archiveSources, looseAssetRoot];
    }

    /// <summary>
    ///     Finds the directory against which Data-relative asset paths can be resolved. When the
    ///     selected NIF directory is <c>Data\meshes</c> (or any directory beneath it), canonical
    ///     resolver keys still begin with <c>textures\</c> or <c>materials\</c>; registering the
    ///     selected meshes directory would therefore incorrectly probe <c>meshes\textures</c>.
    /// </summary>
    private static string? FindLooseAssetRoot(string rootDir)
    {
        var selectedRoot = Path.GetFullPath(rootDir);

        // Prefer the parent of the nearest meshes path component. This is the game/mod Data root,
        // and one directory source rooted there can resolve both loose textures and materialsbeta.cdb.
        for (var current = new DirectoryInfo(selectedRoot); current != null; current = current.Parent)
        {
            if (string.Equals(current.Name, "meshes", StringComparison.OrdinalIgnoreCase) &&
                current.Parent is { } dataRoot &&
                ContainsLooseTextureOrMaterialAssets(dataRoot.FullName))
            {
                return dataRoot.FullName;
            }
        }

        // Also support callers that select Data itself, or a self-contained mod directory whose
        // meshes and loose texture/material trees sit directly below the selected directory.
        return ContainsLooseTextureOrMaterialAssets(selectedRoot) ? selectedRoot : null;
    }

    private static bool ContainsLooseTextureOrMaterialAssets(string rootDir)
    {
        return Directory.Exists(Path.Combine(rootDir, "textures")) ||
               Directory.Exists(Path.Combine(rootDir, "materials"));
    }

    private static (byte[] Data, NifInfo? Nif) ParseAndConvert(byte[] nifData)
    {
        var nif = NifParser.Parse(nifData);
        if (nif == null) return (nifData, null);

        if (nif.IsBigEndian)
        {
            var converted = NifConverter.Convert(nifData);
            if (!converted.Success || converted.OutputData == null)
                return (nifData, null);

            nifData = converted.OutputData;
            nif = NifParser.Parse(nifData);
        }

        return (nifData, nif);
    }

    private static List<NifTreeEntry> ListNifFilesFromDirectory(
        string rootDir,
        Action<NifBrowserScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var entries = new List<NifTreeEntry>();
        var dirGroups = new Dictionary<string, NifTreeEntry>(StringComparer.OrdinalIgnoreCase);
        var nifFilesFound = 0;

        // A recursive filesystem enumeration has no trustworthy total without paying for a second
        // traversal. Report a running NIF count instead of presenting a fabricated percentage.
        progress?.Invoke(new NifBrowserScanProgress(0, null, 0));
        var lastProgressTimestamp = Stopwatch.GetTimestamp();
        var lastReportedNifCount = 0;

        foreach (var file in Directory.EnumerateFiles(rootDir, "*.nif", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            nifFilesFound++;
            var relativePath = Path.GetRelativePath(rootDir, file);
            var dirPart = Path.GetDirectoryName(relativePath) ?? "";

            if (string.IsNullOrEmpty(dirPart))
            {
                entries.Add(new NifTreeEntry
                {
                    DisplayName = Path.GetFileName(file),
                    FullPath = file,
                    IsDirectory = false
                });
            }
            else
            {
                if (!dirGroups.TryGetValue(dirPart, out var dirEntry))
                {
                    dirEntry = new NifTreeEntry
                    {
                        DisplayName = dirPart,
                        FullPath = Path.Combine(rootDir, dirPart),
                        IsDirectory = true
                    };
                    dirGroups[dirPart] = dirEntry;
                    entries.Add(dirEntry);
                }

                dirEntry.Children.Add(new NifTreeEntry
                {
                    DisplayName = Path.GetFileName(file),
                    FullPath = file,
                    IsDirectory = false
                });
            }

            // Progress<T> posts to the UI dispatcher. Bound the production rate so a fast loose
            // scan cannot leave thousands of stale callbacks ahead of the completed mesh tree.
            if (nifFilesFound == 1 ||
                Stopwatch.GetElapsedTime(lastProgressTimestamp) >= TimeSpan.FromMilliseconds(100))
            {
                progress?.Invoke(new NifBrowserScanProgress(nifFilesFound, null, nifFilesFound));
                lastProgressTimestamp = Stopwatch.GetTimestamp();
                lastReportedNifCount = nifFilesFound;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (lastReportedNifCount != nifFilesFound)
        {
            progress?.Invoke(new NifBrowserScanProgress(nifFilesFound, null, nifFilesFound));
        }

        return entries.OrderBy(e => !e.IsDirectory).ThenBy(e => e.DisplayName).ToList();
    }

    private static List<NifTreeEntry> ListNifFilesFromArchive(
        IEnumerable<string> filePaths,
        int totalEntries,
        Action<NifBrowserScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        // BSA has a folder tree; BA2 is a flat list — group both by the directory portion of the path
        // so the browser shows the same folder grouping regardless of container.
        var entries = new List<NifTreeEntry>();
        var dirGroups = new Dictionary<string, NifTreeEntry>(StringComparer.OrdinalIgnoreCase);
        var nifFilesFound = 0;

        // The parsed archive header supplies the real total without first projecting a second entry
        // graph. Archive/index initialization before this remains opaque.
        progress?.Invoke(new NifBrowserScanProgress(0, totalEntries, 0));
        var progressStride = Math.Max(1L, (totalEntries + 63L) / 64L);
        var nextProgressEntry = progressStride;
        var currentEntry = 0;

        foreach (var fullPath in filePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            currentEntry++;
            if (fullPath.EndsWith(".nif", StringComparison.OrdinalIgnoreCase))
            {
                nifFilesFound++;
                // Archive entry paths use the engine separator whatever the host uses.
                var dirPart = EnginePath.DirectoryName(fullPath);
                var name = EnginePath.FileName(fullPath);

                if (string.IsNullOrEmpty(dirPart))
                {
                    entries.Add(new NifTreeEntry { DisplayName = name, FullPath = fullPath, IsDirectory = false });
                }
                else
                {
                    if (!dirGroups.TryGetValue(dirPart, out var dirEntry))
                    {
                        dirEntry = new NifTreeEntry { DisplayName = dirPart, FullPath = dirPart, IsDirectory = true };
                        dirGroups[dirPart] = dirEntry;
                        entries.Add(dirEntry);
                    }

                    dirEntry.Children.Add(new NifTreeEntry
                    {
                        DisplayName = name,
                        FullPath = fullPath,
                        IsDirectory = false
                    });
                }
            }

            // Keep dispatcher traffic bounded independent of archive size. A 1.6-million-entry
            // Starfield BA2 now emits at most 64 scan updates instead of roughly 6,250.
            if (currentEntry >= nextProgressEntry || currentEntry == totalEntries)
            {
                progress?.Invoke(new NifBrowserScanProgress(currentEntry, totalEntries, nifFilesFound));
                nextProgressEntry += progressStride;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (currentEntry != 0 && currentEntry != totalEntries)
        {
            // A backend reporting a stale/corrupt count remains observable without lying about the
            // header total used by the progress bar.
            progress?.Invoke(new NifBrowserScanProgress(currentEntry, totalEntries, nifFilesFound));
        }

        foreach (var dir in dirGroups.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            dir.Children.Sort((a, b) =>
                string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return entries.OrderBy(e => !e.IsDirectory).ThenBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    #endregion
}

/// <summary>
///     Progress while enumerating a browser source. <see cref="TotalEntries" /> is available only
///     for archives, after their indexes have opened and <c>ListFiles</c> has supplied a real total.
/// </summary>
internal readonly record struct NifBrowserScanProgress(
    int CurrentEntry,
    int? TotalEntries,
    int NifFilesFound);

/// <summary>GLB bytes plus completeness evidence for Starfield's external geometry references.</summary>
internal sealed record NifBrowserGlbBuildResult(
    byte[]? GlbBytes,
    NifExternalGeometryDiagnostics ExternalGeometry);

/// <summary>
///     Native raw-NIF viewer scene plus completeness evidence for Starfield external geometry.
/// </summary>
internal sealed record NifBrowserViewerSceneBuildResult(
    BethesdaViewerScene? Scene,
    NifExternalGeometryDiagnostics ExternalGeometry);

/// <summary>One external <c>BSGeometry</c> request and the archive/file that satisfied it.</summary>
internal sealed record NifExternalGeometryResolution(
    string VirtualPath,
    bool Resolved,
    string? SourcePath);

/// <summary>
///     Completeness diagnostic for external Starfield geometry. References are per shape/request rather
///     than unique paths so two shapes sharing one missing blob are still reported as two dropped parts.
/// </summary>
internal sealed record NifExternalGeometryDiagnostics(
    IReadOnlyList<NifExternalGeometryResolution> Resolutions,
    IReadOnlyList<string> DecodeFailedPaths)
{
    internal int ReferencedCount => Resolutions.Count;

    internal int ResolvedCount => Resolutions.Count(static resolution => resolution.Resolved);

    internal bool IsComplete => ResolvedCount == ReferencedCount && DecodeFailedPaths.Count == 0;

    internal string? IncompleteWarningMessage => IsComplete
        ? null
        : $"External geometry is incomplete: located {ResolvedCount} of {ReferencedCount} referenced " +
          $"blobs; {MissingPaths().Count} missing and {DecodeFailedPaths.Count} failed to decode. " +
          "Preview and exports omit those parts.";

    /// <summary>
    ///     Referenced blobs that never resolved. A method rather than a property: it projects a new
    ///     array each call, and a stored array would drag reference identity into this record's
    ///     value equality.
    /// </summary>
    internal IReadOnlyList<string> MissingPaths()
    {
        return Resolutions
            .Where(static resolution => !resolution.Resolved)
            .Select(static resolution => resolution.VirtualPath)
            .ToArray();
    }
}
