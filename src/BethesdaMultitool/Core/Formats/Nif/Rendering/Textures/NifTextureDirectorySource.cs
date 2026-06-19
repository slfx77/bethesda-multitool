using BethesdaMultitool.Core.Formats.Dds;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;

/// <summary>
///     Loose-file texture source rooted at a local game data directory.
/// </summary>
internal sealed class NifTextureDirectorySource(string rootPath) : INifTextureSource
{
    private readonly string _rootPath = Path.GetFullPath(rootPath);

    public DecodedTexture? TryLoad(string path)
    {
        var primaryPath = ResolveLocalPath(path);
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

    public bool Exists(string path)
    {
        if (File.Exists(ResolveLocalPath(path)))
        {
            return true;
        }

        // Mirror TryLoad's .dds<->.ddx fallback so a probe matches what a load would actually find.
        var alt = path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)
            ? Path.ChangeExtension(ResolveLocalPath(path), ".ddx")
            : path.EndsWith(".ddx", StringComparison.OrdinalIgnoreCase)
                ? Path.ChangeExtension(ResolveLocalPath(path), ".dds")
                : null;
        return alt is not null && File.Exists(alt);
    }

    public byte[]? TryLoadRaw(string path)
    {
        var primaryPath = ResolveLocalPath(path);
        if (!File.Exists(primaryPath))
        {
            return null;
        }

        try
        {
            return File.ReadAllBytes(primaryPath);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
    }

    private string ResolveLocalPath(string path) =>
        Path.Combine(
            _rootPath,
            path.Replace('\\', Path.DirectorySeparatorChar));
}
