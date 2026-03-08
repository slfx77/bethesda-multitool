using FalloutXbox360Utils.Core.Formats.Bsa;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Textures;

/// <summary>
///     Builds indexed texture archive sources for one or more texture BSAs.
/// </summary>
internal static class NifTextureArchiveSourceFactory
{
    internal static List<NifTextureArchiveSource> Create(params string[] texturesBsaPaths)
    {
        var sources = new List<NifTextureArchiveSource>(texturesBsaPaths.Length);
        foreach (var bsaPath in texturesBsaPaths)
        {
            var archive = BsaParser.Parse(bsaPath);
            var extractor = new BsaExtractor(bsaPath);
            var fileIndex = new Dictionary<string, BsaFileRecord>(
                StringComparer.OrdinalIgnoreCase);

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
