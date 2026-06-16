using FalloutXbox360Utils.Core.Formats.Bsa;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc;

/// <summary>
///     Auto-detects BSA files (meshes + textures) in an ESM file's directory. Classifies each archive
///     by its <see cref="BsaFileFlags" /> content bits — the same mechanism the engine uses — rather
///     than by filename, so a mod that packs everything into one <c>&lt;Mod&gt; - Main.bsa</c> (which
///     matches neither <c>*Meshes*.bsa</c> nor <c>*Texture*.bsa</c>) is still found and contributes to
///     both the mesh set and the texture set.
/// </summary>
internal static class BsaDiscovery
{
    internal static BsaDiscoveryResult Discover(string esmPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(esmPath));
        if (dir == null || !Directory.Exists(dir))
        {
            return BsaDiscoveryResult.Empty;
        }

        var bsaPaths = Directory.GetFiles(dir, "*.bsa")
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (bsaPaths.Length == 0)
        {
            return BsaDiscoveryResult.Empty;
        }

        var meshes = new List<string>();
        var textures = new List<string>();
        foreach (var path in bsaPaths)
        {
            var (hasMeshes, hasTextures) = ClassifyContent(path);
            if (hasMeshes)
            {
                meshes.Add(path);
            }

            if (hasTextures)
            {
                textures.Add(path);
            }
        }

        if (meshes.Count == 0 && textures.Count == 0)
        {
            return BsaDiscoveryResult.Empty;
        }

        return new BsaDiscoveryResult(meshes.ToArray(), textures.ToArray(), AutoDetected: true);
    }

    /// <summary>
    ///     Classifies an archive by its header <see cref="BsaFileFlags" /> (cheap, header-only read).
    ///     Falls back to inspecting top-level folder names only when the flags are unset (some
    ///     hand-built archives ship with <see cref="BsaFileFlags.None" />).
    /// </summary>
    private static (bool Meshes, bool Textures) ClassifyContent(string bsaPath)
    {
        var header = BsaParser.TryReadHeader(bsaPath);
        if (header is null)
        {
            return (false, false);
        }

        var hasMeshes = header.FileFlags.HasFlag(BsaFileFlags.Meshes);
        var hasTextures = header.FileFlags.HasFlag(BsaFileFlags.Textures);
        if (hasMeshes || hasTextures || header.FileFlags != BsaFileFlags.None)
        {
            return (hasMeshes, hasTextures);
        }

        // FileFlags unset — inspect folder names (requires IncludeDirectoryNames, which every FNV/FO3
        // BSA sets). A full parse is acceptable here because this path is rare.
        try
        {
            var archive = BsaParser.Parse(bsaPath);
            foreach (var folder in archive.Folders)
            {
                if (folder.Name is not { } name)
                {
                    continue;
                }

                if (name.StartsWith("meshes", StringComparison.OrdinalIgnoreCase))
                {
                    hasMeshes = true;
                }
                else if (name.StartsWith("textures", StringComparison.OrdinalIgnoreCase))
                {
                    hasTextures = true;
                }

                if (hasMeshes && hasTextures)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            // Unreadable/corrupt archive — leave unclassified.
        }

        return (hasMeshes, hasTextures);
    }
}
