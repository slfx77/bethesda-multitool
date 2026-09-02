using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Diagnostics;
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
    private readonly string? _rootDirectory;
    private readonly SynchronizedLazyDisposable<MeshArchiveSet>? _siblingMeshArchives;
    private readonly string[] _siblingMeshArchivePaths;
    private readonly NifTextureResolver _textureResolver;

    private NifBrowserService(
        string? rootDirectory,
        ArchiveLease? archiveLease,
        NifTextureResolver textureResolver,
        string[] texturePaths,
        string? archivePath = null,
        string[]? siblingMeshArchivePaths = null)
    {
        _rootDirectory = rootDirectory;
        _archiveLease = archiveLease;
        _archive = archiveLease?.Reader;
        _archivePath = archivePath;
        _textureResolver = textureResolver;
        TexturePaths = texturePaths;
        _siblingMeshArchivePaths = siblingMeshArchivePaths ?? [];
        if (_siblingMeshArchivePaths.Length > 0)
        {
            _siblingMeshArchives = new SynchronizedLazyDisposable<MeshArchiveSet>(
                () => MeshArchiveSet.Open(
                    _siblingMeshArchivePaths[0],
                    _siblingMeshArchivePaths.Length == 1 ? null : _siblingMeshArchivePaths[1..]));
        }
    }

    public bool IsBsaMode => _archive != null;

    /// <summary>
    ///     Texture sources actually in use (either caller-supplied or auto-detected).
    /// </summary>
    internal string[] TexturePaths { get; }

    /// <summary>
    ///     Deterministic external-geometry search order. The archive explicitly opened by the user is
    ///     always first; content-discovered siblings follow in ordinal-ignore-case path order with
    ///     an ordinal tie-breaker.
    /// </summary>
    internal IReadOnlyList<string> ExternalMeshArchivePaths => _archivePath is null
        ? _siblingMeshArchivePaths
        : [_archivePath, .. _siblingMeshArchivePaths];

    public void Dispose()
    {
        _siblingMeshArchives?.Dispose();
        _archiveLease?.Dispose();
        _textureResolver.Dispose();
    }

    /// <summary>
    ///     Create from a filesystem directory containing NIF files. Caller-supplied texture sources
    ///     have first-hit precedence, while auto-discovered sibling archives / the loose Data root
    ///     remain as fallbacks. Starfield needs that union: one selected override cannot provide both
    ///     <c>materials\materialsbeta.cdb</c> and every DX10 texture shard it references.
    /// </summary>
    internal static NifBrowserService CreateFromDirectory(string rootDir, string[]? texturePaths = null)
    {
        texturePaths = MergeTextureSources(texturePaths, DiscoverTextureSources(rootDir));
        var resolver = texturePaths.Length > 0
            ? new NifTextureResolver(texturePaths)
            : new NifTextureResolver();
        return new NifBrowserService(rootDir, null, resolver, texturePaths);
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
        NifTextureResolver? resolver = null;
        try
        {
            var discovery = DiscoverSiblingArchives(archivePath);
            var siblingMeshArchivePaths = discovery.MeshesBsaPaths
                .Select(Path.GetFullPath)
                .Where(path => !string.Equals(path, archivePath, StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(path => path, StringComparer.Ordinal)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            texturePaths = MergeTextureSources(texturePaths, discovery.TexturesBsaPaths);
            resolver = texturePaths.Length > 0
                ? new NifTextureResolver(texturePaths)
                : new NifTextureResolver();
            return new NifBrowserService(
                null,
                archiveLease,
                resolver,
                texturePaths,
                archivePath,
                siblingMeshArchivePaths);
        }
        catch
        {
            // A failed source-set or texture-resolver open must not strand shared registry leases.
            resolver?.Dispose();
            archiveLease.Dispose();
            throw;
        }
    }

    /// <summary>
    ///     List all NIF files available for browsing.
    /// </summary>
    internal List<NifTreeEntry> ListNifFiles(Action<NifBrowserScanProgress>? progress = null)
    {
        if (_archive != null)
        {
            return ListNifFilesFromArchive(_archive.ListFiles(), progress);
        }

        if (_rootDirectory != null)
        {
            return ListNifFilesFromDirectory(_rootDirectory, progress);
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
        string sourceLabel)
    {
        var externalGeometryResolutions = new List<NifExternalGeometryResolution>();
        var externalGeometryDecodeFailures = new List<string>();
        var detectedGame = BethesdaGame.Unknown;

        NifBrowserViewerSceneBuildResult Finish(
            GlbScene? exportScene,
            byte[]? parsedData = null,
            NifInfo? parsedNif = null)
        {
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
                NifMeshAnimation? animation = null;
                try
                {
                    // The placed-world collectors intentionally erase file-root motion because REFR
                    // placement replaces it. A standalone asset viewer has no REFR and must retain
                    // the authored root transform/controller.
                    animation = NifNodeKeyframeTrackCollector.Collect(
                        parsedData,
                        parsedNif,
                        preserveFileRootTransformAndTrack: true);
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
                            preserveFileRootTransformAndTrack: true);
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
            var model = NifGeometryExtractor.Extract(
                data,
                nif,
                _textureResolver,
                externalMeshLoader: LoadExternalGeometry,
                onExternalMeshDecodeFailure: RecordExternalGeometryDecodeFailure);
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
    ///     Export-only projection of a native viewer scene. Animation channels unsupported by the
    ///     legacy GLB DTO remain native; static node, mesh, material, and skin state is handed to the
    ///     existing GLB writer unchanged.
    /// </summary>
    internal byte[] ExportViewerSceneToGlb(BethesdaViewerScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        return GlbWriter.WriteToBytes(
            BethesdaViewerSceneGlbAdapter.ToGlbScene(scene),
            _textureResolver);
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

        var model = NifGeometryExtractor.Extract(
            data,
            nif,
            _textureResolver,
            externalMeshLoader: ReadExternalGeometryBlob);
        if (model == null || !model.HasGeometry) return null;
        AddStarfieldNormalMapRequests(model);

        var result = NifSpriteRenderer.Render(
            model,
            _textureResolver,
            1.0f,
            32,
            spriteSize,
            azimuth,
            elevation,
            spriteSize);
        if (result == null) return null;

        return PngWriter.EncodeRgba(result.Pixels, result.Width, result.Height);
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
            if (_siblingMeshArchives is not null)
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

    /// <summary>
    ///     Content-classifies sibling archives once so texture/material sources and external-geometry
    ///     fallback archives share the same discovery result. Filename matching is deliberately not
    ///     used: a combined <c>&lt;Mod&gt; - Main.bsa</c> may contribute to both sets.
    /// </summary>
    private static BsaDiscoveryResult DiscoverSiblingArchives(string bsaPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(bsaPath));
        if (dir == null) return BsaDiscoveryResult.Empty;

        return BsaDiscovery.DiscoverInDirectory(dir);
    }

    /// <summary>
    ///     Places explicit sources first so they retain first-hit override precedence, then appends
    ///     every distinct auto-discovered source. Full-path comparison prevents the same archive from
    ///     being opened twice when the user selects a sibling that discovery also returns, while the
    ///     original spelling remains visible in the UI's active-source list.
    /// </summary>
    private static string[] MergeTextureSources(
        IReadOnlyList<string>? preferredSources,
        IReadOnlyList<string> discoveredSources)
    {
        var merged = new List<string>((preferredSources?.Count ?? 0) + discoveredSources.Count);
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
        Action<NifBrowserScanProgress>? progress)
    {
        var entries = new List<NifTreeEntry>();
        var dirGroups = new Dictionary<string, NifTreeEntry>(StringComparer.OrdinalIgnoreCase);
        var nifFilesFound = 0;

        // A recursive filesystem enumeration has no trustworthy total without paying for a second
        // traversal. Report a running NIF count instead of presenting a fabricated percentage.
        progress?.Invoke(new NifBrowserScanProgress(0, null, 0));

        foreach (var file in Directory.EnumerateFiles(rootDir, "*.nif", SearchOption.AllDirectories))
        {
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

            if (nifFilesFound == 1 || (nifFilesFound & 63) == 0)
            {
                progress?.Invoke(new NifBrowserScanProgress(nifFilesFound, null, nifFilesFound));
            }
        }

        progress?.Invoke(new NifBrowserScanProgress(nifFilesFound, null, nifFilesFound));

        return entries.OrderBy(e => !e.IsDirectory).ThenBy(e => e.DisplayName).ToList();
    }

    private static List<NifTreeEntry> ListNifFilesFromArchive(
        IReadOnlyList<ArchiveReader.ArchiveEntry> files,
        Action<NifBrowserScanProgress>? progress)
    {
        // BSA has a folder tree; BA2 is a flat list — group both by the directory portion of the path
        // so the browser shows the same folder grouping regardless of container.
        var entries = new List<NifTreeEntry>();
        var dirGroups = new Dictionary<string, NifTreeEntry>(StringComparer.OrdinalIgnoreCase);
        var nifFilesFound = 0;

        // ListFiles has materialized the real archive-entry total. This is the first point at which a
        // determinate progress value is honest; archive/index initialization before this remains opaque.
        progress?.Invoke(new NifBrowserScanProgress(0, files.Count, 0));

        for (var index = 0; index < files.Count; index++)
        {
            var file = files[index];
            var fullPath = file.FullPath;
            if (fullPath.EndsWith(".nif", StringComparison.OrdinalIgnoreCase))
            {
                nifFilesFound++;
                var dirPart = Path.GetDirectoryName(fullPath) ?? "";
                var name = Path.GetFileName(fullPath);

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

            var currentEntry = index + 1;
            if ((currentEntry & 255) == 0 || currentEntry == files.Count)
            {
                progress?.Invoke(new NifBrowserScanProgress(currentEntry, files.Count, nifFilesFound));
            }
        }

        foreach (var dir in dirGroups.Values)
        {
            dir.Children.Sort((a, b) =>
                string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        }

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

    internal IReadOnlyList<string> MissingPaths => Resolutions
        .Where(static resolution => !resolution.Resolved)
        .Select(static resolution => resolution.VirtualPath)
        .ToArray();

    internal bool IsComplete => ResolvedCount == ReferencedCount && DecodeFailedPaths.Count == 0;

    internal string? IncompleteWarningMessage => IsComplete
        ? null
        : $"External geometry is incomplete: located {ResolvedCount} of {ReferencedCount} referenced " +
          $"blobs; {MissingPaths.Count} missing and {DecodeFailedPaths.Count} failed to decode. " +
          "Preview and exports omit those parts.";
}
