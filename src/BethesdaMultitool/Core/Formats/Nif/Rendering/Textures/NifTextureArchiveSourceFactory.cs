using BethesdaMultitool.Core.Formats.Bsa;
using BethesdaMultitool.Core.Formats.Bsa.Ba2;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;

/// <summary>
///     Builds indexed texture archive sources for one or more texture archives. Each path may be a
///     loose directory, a BSA (Morrowind→Skyrim/FNV), or a BA2 (Fallout 4 / Fallout 76) — dispatched
///     by magic, not extension — so the resolver loads textures uniformly across formats.
/// </summary>
internal static class NifTextureArchiveSourceFactory
{
    internal static List<INifTextureSource> Create(params string[] textureSourcePaths)
    {
        var sources = new List<INifTextureSource>(textureSourcePaths.Length);
        foreach (var sourcePath in textureSourcePaths)
        {
            if (Directory.Exists(sourcePath))
            {
                sources.Add(new NifTextureDirectorySource(sourcePath));
                continue;
            }

            if (Ba2Parser.IsBa2File(sourcePath))
            {
                sources.Add(CreateBa2Source(sourcePath));
            }
            else
            {
                sources.Add(CreateBsaSource(sourcePath));
            }
        }

        return sources;
    }

    private static NifTextureArchiveSource CreateBsaSource(string sourcePath)
    {
        var archive = BsaParser.Parse(sourcePath);
        var extractor = new BsaExtractor(sourcePath);
        var fileIndex = new Dictionary<string, BsaFileRecord>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in archive.AllFiles)
        {
            var path = file.FullPath;
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }

            fileIndex[path.Replace('/', '\\')] = file;
        }

        return new NifTextureArchiveSource(extractor, fileIndex);
    }

    private static Ba2TextureArchiveSource CreateBa2Source(string sourcePath)
    {
        // The Ba2Extractor ctor parses the archive once; reuse its Archive for the path index.
        var extractor = new Ba2Extractor(sourcePath);
        var fileIndex = new Dictionary<string, Ba2FileRecord>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in extractor.Archive.AllFiles)
        {
            var path = file.FullPath;
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }

            fileIndex[path.Replace('/', '\\')] = file;
        }

        return new Ba2TextureArchiveSource(extractor, fileIndex);
    }
}
