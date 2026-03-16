using FalloutXbox360Utils.Core.Formats.Bsa;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Textures;

/// <summary>
///     Builds indexed texture archive sources for one or more texture BSAs.
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

            sources.Add(new NifTextureArchiveSource(extractor, fileIndex));
        }

        return sources;
    }
}
