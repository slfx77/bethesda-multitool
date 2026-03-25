using FalloutXbox360Utils.Core.Formats.Dds;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Textures;

/// <summary>
///     Loose-file texture source rooted at a local game data directory.
/// </summary>
internal sealed class NifTextureDirectorySource(string rootPath) : INifTextureSource
{
    private readonly string _rootPath = Path.GetFullPath(rootPath);

    public DecodedTexture? TryLoad(string path)
    {
        var primaryPath = Path.Combine(
            _rootPath,
            path.Replace('\\', Path.DirectorySeparatorChar));
        string? alternatePath;
        if (path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
        {
            alternatePath = Path.ChangeExtension(primaryPath, ".ddx");
        }
        else if (path.EndsWith(".ddx", StringComparison.OrdinalIgnoreCase))
        {
            alternatePath = Path.ChangeExtension(primaryPath, ".dds");
        }
        else
        {
            alternatePath = null;
        }

        foreach (var candidate in new[] { primaryPath, alternatePath })
        {
            if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate))
            {
                continue;
            }

            try
            {
                var rawData = File.ReadAllBytes(candidate);
                var texture = NifTextureLoader.DecodeTextureData(rawData);
                if (texture != null)
                {
                    return texture;
                }
            }
            catch
            {
                // Try the next candidate.
            }
        }

        return null;
    }

    public void Dispose()
    {
    }
}
