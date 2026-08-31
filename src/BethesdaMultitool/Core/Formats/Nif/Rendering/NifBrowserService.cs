using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Esm.Analysis.Geometry;
using BethesdaMultitool.Core.Formats.Nif.Conversion;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Rasterization;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Skinning;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using BethesdaMultitool.Core.Vfs;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering;

/// <summary>
///     GUI-facing service for browsing, viewing, and exporting individual NIF files.
///     No Spectre.Console dependency — suitable for WinUI 3 consumption.
/// </summary>
internal sealed class NifBrowserService : IDisposable
{
    private readonly ArchiveReader? _archive;
    private readonly ArchiveLease? _archiveLease;
    private readonly string? _rootDirectory;
    private readonly NifTextureResolver _textureResolver;

    private NifBrowserService(
        string? rootDirectory,
        ArchiveLease? archiveLease,
        NifTextureResolver textureResolver,
        string[] texturePaths)
    {
        _rootDirectory = rootDirectory;
        _archiveLease = archiveLease;
        _archive = archiveLease?.Reader;
        _textureResolver = textureResolver;
        TexturePaths = texturePaths;
    }

    public bool IsBsaMode => _archive != null;

    /// <summary>
    ///     Texture sources actually in use (either caller-supplied or auto-detected).
    /// </summary>
    internal string[] TexturePaths { get; }

    public void Dispose()
    {
        _archiveLease?.Dispose();
        _textureResolver.Dispose();
    }

    /// <summary>
    ///     Create from a filesystem directory containing NIF files.
    /// </summary>
    internal static NifBrowserService CreateFromDirectory(string rootDir, string[]? texturePaths = null)
    {
        texturePaths ??= DiscoverTextureSources(rootDir);
        var resolver = texturePaths.Length > 0
            ? new NifTextureResolver(texturePaths)
            : new NifTextureResolver();
        return new NifBrowserService(rootDir, null, resolver, texturePaths);
    }

    /// <summary>
    ///     Create from a mesh archive containing NIF files — a classic BSA or a Bethesda Archive 2
    ///     (<c>.ba2</c>), dispatched by magic.
    /// </summary>
    internal static NifBrowserService CreateFromBsa(string archivePath, string[]? texturePaths = null)
    {
        // Shared handle: the browser is long-lived and read-only, and its meshes archive often
        // overlaps what the 3D viewer / extractor tab already hold open.
        var archiveLease = ArchiveHandleRegistry.Shared.Acquire(archivePath);
        try
        {
            texturePaths ??= DiscoverTextureBsas(archivePath);
            var resolver = texturePaths.Length > 0
                ? new NifTextureResolver(texturePaths)
                : new NifTextureResolver();
            return new NifBrowserService(null, archiveLease, resolver, texturePaths);
        }
        catch
        {
            // A failed texture-resolver open must not strand the meshes lease in the registry.
            archiveLease.Dispose();
            throw;
        }
    }

    /// <summary>
    ///     List all NIF files available for browsing.
    /// </summary>
    internal List<NifTreeEntry> ListNifFiles()
    {
        if (_archive != null)
        {
            return ListNifFilesFromArchive(_archive.ListFiles());
        }

        if (_rootDirectory != null)
        {
            return ListNifFilesFromDirectory(_rootDirectory);
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
    ///     Build GLB bytes from NIF data (parse, endian-convert if needed, build scene, write GLB).
    /// </summary>
    internal byte[]? BuildGlb(byte[] nifData, string sourceLabel)
    {
        var (data, nif) = ParseAndConvert(nifData);
        if (nif == null) return null;

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
                externalMeshLoader: ReadExternalGeometryBlob);
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

        if (scene == null || scene.MeshParts.Count == 0) return null;

        return GlbWriter.WriteToBytes(scene, _textureResolver);
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
    ///     <c>geometries\*.mesh</c> payload in the currently opened BA2 or loose Data tree.
    /// </summary>
    private byte[]? ReadExternalGeometryBlob(string meshPath)
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

        if (_archive != null)
        {
            return _archive.ReadFile(normalized);
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
            var candidate = Path.Combine(root, normalized.Replace('\\', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
            {
                return File.ReadAllBytes(candidate);
            }
        }

        return null;
    }

    #region Private Helpers

    /// <summary>
    ///     Auto-discover texture archives (BSA or BA2) in the same directory as a meshes archive,
    ///     classified by archive content flags/paths (<see cref="BsaDiscovery" />) rather than
    ///     filename — a combined <c>&lt;Mod&gt; - Main.bsa</c> that packs textures next to its meshes
    ///     is included too.
    /// </summary>
    private static string[] DiscoverTextureBsas(string bsaPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(bsaPath));
        if (dir == null) return [];

        return BsaDiscovery.DiscoverInDirectory(dir).TexturesBsaPaths;
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

    private static List<NifTreeEntry> ListNifFilesFromDirectory(string rootDir)
    {
        var entries = new List<NifTreeEntry>();
        var dirGroups = new Dictionary<string, NifTreeEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.EnumerateFiles(rootDir, "*.nif", SearchOption.AllDirectories))
        {
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
        }

        return entries.OrderBy(e => !e.IsDirectory).ThenBy(e => e.DisplayName).ToList();
    }

    private static List<NifTreeEntry> ListNifFilesFromArchive(IReadOnlyList<ArchiveReader.ArchiveEntry> files)
    {
        // BSA has a folder tree; BA2 is a flat list — group both by the directory portion of the path
        // so the browser shows the same folder grouping regardless of container.
        var entries = new List<NifTreeEntry>();
        var dirGroups = new Dictionary<string, NifTreeEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var fullPath = file.FullPath;
            if (!fullPath.EndsWith(".nif", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var dirPart = Path.GetDirectoryName(fullPath) ?? "";
            var name = Path.GetFileName(fullPath);

            if (string.IsNullOrEmpty(dirPart))
            {
                entries.Add(new NifTreeEntry { DisplayName = name, FullPath = fullPath, IsDirectory = false });
                continue;
            }

            if (!dirGroups.TryGetValue(dirPart, out var dirEntry))
            {
                dirEntry = new NifTreeEntry { DisplayName = dirPart, FullPath = dirPart, IsDirectory = true };
                dirGroups[dirPart] = dirEntry;
                entries.Add(dirEntry);
            }

            dirEntry.Children.Add(new NifTreeEntry { DisplayName = name, FullPath = fullPath, IsDirectory = false });
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
