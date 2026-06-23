using BethesdaMultitool.Core.Formats.Bsa;
using BethesdaMultitool.Core.Formats.Bsa.Ba2;
using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc;

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
        var ba2Paths = Directory.GetFiles(dir, "*.ba2")
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (bsaPaths.Length == 0 && ba2Paths.Length == 0)
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

        // BA2 (Fallout 4 / Fallout 76). DX10 archives hold textures only; GNRL archives hold
        // meshes/materials/etc. Both the texture path (NifTextureArchiveSourceFactory) and the mesh
        // path (MeshArchiveSet) are now BA2-aware, so classify by content and route accordingly.
        // DX10 → textures by definition. GNRL → scan its name-table paths for meshes\/textures\
        // prefixes (BA2 has no BSA-style content-flag bits).
        foreach (var path in ba2Paths)
        {
            var header = Ba2Parser.TryReadHeader(path);
            if (header is null)
            {
                continue;
            }

            if (header.Type == Ba2HeaderType.Texture)
            {
                textures.Add(path);
                continue;
            }

            var (hasMeshes, hasTextures) = ClassifyBa2GeneralContent(path);
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

    /// <summary>
    ///     Classifies a GNRL (general) BA2 by scanning its name-table paths for <c>meshes\</c> /
    ///     <c>textures\</c> / <c>materials\</c> prefixes. BA2 has no BSA-style content-flag bits, so this
    ///     is the only signal. A <c>materials\</c> archive (Fallout 4 / 76 ship a <c>… - Materials.ba2</c>
    ///     full of <c>.bgsm</c>/<c>.bgem</c>) counts as a TEXTURE source: a FO4/76 NIF's
    ///     BSLightingShaderProperty Name is a <c>.bgsm</c> path, and the texture resolver follows it
    ///     through the material file to the real textures (<c>NifTextureResolver.LoadFromMaterial</c>), so
    ///     the materials archive must reach the resolver's source set — without it every FO76 shape
    ///     resolves no diffuse and the whole world renders untextured. A GNRL BA2 with no usable name
    ///     table (hash-only paths) classifies as neither — it can't be path-resolved anyway.
    /// </summary>
    private static (bool Meshes, bool Textures) ClassifyBa2GeneralContent(string ba2Path)
    {
        try
        {
            var archive = Ba2Parser.Parse(ba2Path);
            var hasMeshes = false;
            var hasTextures = false;
            foreach (var file in archive.AllFiles)
            {
                var path = file.FullPath;
                if (path.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase))
                {
                    hasMeshes = true;
                }
                else if (path.StartsWith("textures\\", StringComparison.OrdinalIgnoreCase) ||
                         path.StartsWith("materials\\", StringComparison.OrdinalIgnoreCase))
                {
                    hasTextures = true;
                }

                if (hasMeshes && hasTextures)
                {
                    break;
                }
            }

            return (hasMeshes, hasTextures);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            return (false, false);
        }
    }
}
