using FalloutXbox360Utils.Core.Formats.Bsa;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis;
using FalloutXbox360Utils.Core.Formats.Nif.Conversion;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Export;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     GUI-facing service for browsing, viewing, and exporting individual NIF files.
///     No Spectre.Console dependency — suitable for WinUI 3 consumption.
/// </summary>
internal sealed class NifBrowserService : IDisposable
{
    private readonly BsaArchive? _bsaArchive;
    private readonly BsaExtractor? _bsaExtractor;
    private readonly string? _rootDirectory;
    private readonly NifTextureResolver _textureResolver;

    private NifBrowserService(
        string? rootDirectory,
        BsaExtractor? bsaExtractor,
        BsaArchive? bsaArchive,
        NifTextureResolver textureResolver,
        string[] texturePaths)
    {
        _rootDirectory = rootDirectory;
        _bsaExtractor = bsaExtractor;
        _bsaArchive = bsaArchive;
        _textureResolver = textureResolver;
        TexturePaths = texturePaths;
    }

    public bool IsBsaMode => _bsaExtractor != null;

    /// <summary>
    ///     Texture sources actually in use (either caller-supplied or auto-detected).
    /// </summary>
    internal string[] TexturePaths { get; }

    public void Dispose()
    {
        _bsaExtractor?.Dispose();
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
        return new NifBrowserService(rootDir, null, null, resolver, texturePaths);
    }

    /// <summary>
    ///     Create from a BSA archive containing NIF files.
    /// </summary>
    internal static NifBrowserService CreateFromBsa(string bsaPath, string[]? texturePaths = null)
    {
        var extractor = new BsaExtractor(bsaPath);
        texturePaths ??= DiscoverTextureBsas(bsaPath);
        var resolver = texturePaths.Length > 0
            ? new NifTextureResolver(texturePaths)
            : new NifTextureResolver();
        return new NifBrowserService(null, extractor, extractor.Archive, resolver, texturePaths);
    }

    /// <summary>
    ///     List all NIF files available for browsing.
    /// </summary>
    internal List<NifTreeEntry> ListNifFiles()
    {
        if (_bsaArchive != null)
        {
            return ListNifFilesFromBsa(_bsaArchive);
        }

        if (_rootDirectory != null)
        {
            return ListNifFilesFromDirectory(_rootDirectory);
        }

        return [];
    }

    /// <summary>
    ///     Read NIF file data from the source (filesystem or BSA).
    /// </summary>
    internal byte[]? ReadNifData(string path)
    {
        if (_bsaExtractor != null && _bsaArchive != null)
        {
            var file = _bsaArchive.FindFile(path);
            return file != null ? _bsaExtractor.ExtractFile(file) : null;
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

        var scene = NifExportSceneBuilder.Build(data, nif, sourceLabel);
        if (scene == null || scene.MeshParts.Count == 0) return null;

        return NpcGlbWriter.WriteToBytes(scene, _textureResolver);
    }

    /// <summary>
    ///     Render NIF to PNG sprite bytes.
    /// </summary>
    internal byte[]? RenderPng(byte[] nifData, string sourceLabel, int spriteSize,
        float azimuth, float elevation)
    {
        var (data, nif) = ParseAndConvert(nifData);
        if (nif == null) return null;

        var model = NifGeometryExtractor.Extract(data, nif, _textureResolver);
        if (model == null || !model.HasGeometry) return null;

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

    #region Private Helpers

    /// <summary>
    ///     Auto-discover *Texture*.bsa files in the same directory as a meshes BSA.
    /// </summary>
    private static string[] DiscoverTextureBsas(string bsaPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(bsaPath));
        if (dir == null || !Directory.Exists(dir)) return [];

        return Directory.GetFiles(dir, "*Texture*.bsa")
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    ///     Auto-discover texture sources for a directory of NIF files.
    ///     Looks for texture BSAs or a textures subfolder in the directory and its parent.
    /// </summary>
    private static string[] DiscoverTextureSources(string rootDir)
    {
        var sources = new List<string>();

        // Check for texture BSAs in same directory or parent
        foreach (var dir in new[] { rootDir, Path.GetDirectoryName(rootDir) })
        {
            if (dir == null || !Directory.Exists(dir)) continue;

            var bsas = Directory.GetFiles(dir, "*Texture*.bsa");
            if (bsas.Length > 0)
            {
                sources.AddRange(bsas.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
                return sources.ToArray();
            }
        }

        // Check for a textures subdirectory
        var texturesDir = Path.Combine(rootDir, "textures");
        if (Directory.Exists(texturesDir))
        {
            sources.Add(texturesDir);
        }

        return sources.ToArray();
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

    private static List<NifTreeEntry> ListNifFilesFromBsa(BsaArchive archive)
    {
        var entries = new List<NifTreeEntry>();

        foreach (var folder in archive.Folders.OrderBy(f => f.Name))
        {
            var nifFiles = folder.Files
                .Where(f => f.FullPath.EndsWith(".nif", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.Name)
                .ToList();

            if (nifFiles.Count == 0) continue;

            var dirEntry = new NifTreeEntry
            {
                DisplayName = folder.Name ?? $"folder_{folder.NameHash:X16}",
                FullPath = folder.Name ?? $"folder_{folder.NameHash:X16}",
                IsDirectory = true
            };

            foreach (var file in nifFiles)
            {
                dirEntry.Children.Add(new NifTreeEntry
                {
                    DisplayName = file.Name ?? file.FullPath,
                    FullPath = file.FullPath,
                    IsDirectory = false
                });
            }

            entries.Add(dirEntry);
        }

        return entries;
    }

    #endregion
}
