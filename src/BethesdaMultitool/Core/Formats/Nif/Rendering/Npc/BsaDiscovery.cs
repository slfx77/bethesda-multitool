using BethesdaMultitool.Core.Formats.Bsa.Ba2;
using BethesdaMultitool.Core.Formats.Bsa.Models;
using BethesdaMultitool.Core.Formats.Bsa.Parsing;

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
        return dir == null ? BsaDiscoveryResult.Empty : DiscoverInDirectory(dir);
    }

    /// <summary>
    ///     Content-classifies every archive in <paramref name="dir" /> directly. Entry point for
    ///     callers that start from a directory or an archive path rather than an ESM (e.g. the
    ///     NifConverter tab's texture auto-detection).
    /// </summary>
    internal static BsaDiscoveryResult DiscoverInDirectory(string dir)
    {
        if (!Directory.Exists(dir))
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
            // A game-install ROOT is a natural directory to hand this API (asset-donor dirs get
            // configured that way), and its archives live one level down in Data\. Falling through
            // to that matters more than it looks: a donor root that silently discovers zero
            // archives degrades every render that depended on the donor with no individual image
            // looking obviously wrong — the FO3 and FNV-final corpus donors shipped exactly this
            // way and contributed nothing.
            var dataDir = Path.Combine(dir, "Data");
            return Directory.Exists(dataDir) ? DiscoverInDirectory(dataDir) : BsaDiscoveryResult.Empty;
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

        return new BsaDiscoveryResult(meshes.ToArray(), textures.ToArray(), true);
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
                // "geometries\" counts as mesh content: Starfield splits every vertex/index buffer out
                // of the NIF into hash-named blobs under that root (288,231 of them in Meshes01, with
                // zero .nif among them). An archive holding only those would otherwise classify as
                // NEITHER meshes nor textures and be dropped from the mesh set entirely.
                if (path.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("geometries\\", StringComparison.OrdinalIgnoreCase))
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
