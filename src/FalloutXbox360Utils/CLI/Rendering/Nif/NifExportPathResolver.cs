namespace FalloutXbox360Utils.CLI.Rendering.Nif;

internal static class NifExportPathResolver
{
    internal static bool TryResolve(
        NifExportSettings settings,
        out NifExportSettings? resolvedSettings,
        out string? error)
    {
        resolvedSettings = null;
        error = null;

        if (!File.Exists(settings.InputPath))
        {
            error = $"NIF file not found: {settings.InputPath}";
            return false;
        }

        var inputPath = Path.GetFullPath(settings.InputPath);
        if (!inputPath.EndsWith(".nif", StringComparison.OrdinalIgnoreCase))
        {
            error = $"Input is not a NIF file: {settings.InputPath}";
            return false;
        }

        var outputPath = ResolveOutputPath(inputPath, settings.OutputPath);
        var textureSourcePaths =
            ResolveTextureSourcePaths(inputPath, settings.DataRoots, settings.TextureSourcePaths, out error);
        if (textureSourcePaths == null)
        {
            return false;
        }

        resolvedSettings = new NifExportSettings
        {
            InputPath = inputPath,
            OutputPath = outputPath,
            DataRoots = settings.DataRoots,
            TextureSourcePaths = textureSourcePaths
        };
        return true;
    }

    internal static string ResolveOutputPath(string inputPath, string outputPath)
    {
        var fullOutputPath = Path.GetFullPath(outputPath);
        return fullOutputPath.EndsWith(".glb", StringComparison.OrdinalIgnoreCase)
            ? fullOutputPath
            : Path.Combine(
                fullOutputPath,
                Path.GetFileNameWithoutExtension(inputPath) + ".glb");
    }

    internal static string[]? ResolveTextureSourcePaths(
        string inputPath,
        string[]? explicitDataRoots,
        string[]? explicitTextureSourcePaths,
        out string? error)
    {
        error = null;

        var resolved = new List<string>();
        if (explicitDataRoots is { Length: > 0 })
        {
            foreach (var dataRoot in explicitDataRoots)
            {
                if (!Directory.Exists(dataRoot))
                {
                    error = $"Data root not found: {dataRoot}";
                    return null;
                }

                resolved.Add(Path.GetFullPath(dataRoot));
            }
        }
        else if (TryDetectDataRoot(inputPath, out var detectedDataRoot))
        {
            resolved.Add(detectedDataRoot);
        }

        if (explicitTextureSourcePaths is { Length: > 0 })
        {
            foreach (var sourcePath in explicitTextureSourcePaths)
            {
                if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
                {
                    error = $"Texture source not found: {sourcePath}";
                    return null;
                }

                resolved.Add(Path.GetFullPath(sourcePath));
            }
        }

        return resolved.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static bool TryDetectDataRoot(string inputPath, out string dataRoot)
    {
        var current = Path.GetDirectoryName(Path.GetFullPath(inputPath));
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (Directory.Exists(Path.Combine(current, "textures")))
            {
                dataRoot = current;
                return true;
            }

            current = Path.GetDirectoryName(current);
        }

        dataRoot = string.Empty;
        return false;
    }
}
