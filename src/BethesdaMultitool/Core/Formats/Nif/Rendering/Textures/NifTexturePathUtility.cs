namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;

/// <summary>
///     Normalizes texture paths into the canonical BSA lookup format.
/// </summary>
internal static class NifTexturePathUtility
{
    internal static string Normalize(string path)
    {
        var normalized = path.Replace('/', '\\').ToLowerInvariant().Trim();

        // Some Bethesda subrecords author the asset path relative to the game directory
        // (the parent of Data\ — where FalloutNV.exe lives) rather than relative to Data\
        // itself. Vanilla FNV's WATR DefaultWater NNAM is the canonical example:
        // "data\textures\water\genaratednoise01.dds". BSA entries are stored relative to
        // Data\, so the explicit "data\" step has to be peeled off before the "textures\"
        // check below — otherwise the prepend doubles into "textures\data\textures\…" and
        // every BSA lookup misses. The engine peels it off silently; we have to match.
        if (normalized.StartsWith("data\\", StringComparison.Ordinal))
        {
            normalized = normalized[5..];
        }

        // Fallout 4 / Fallout 76 material files (.bgsm/.bgem) live under materials\, not textures\ —
        // the BSLightingShaderProperty Name points at one. Leave those (and any already-textures\ path)
        // untouched; everything else is a texture relative to textures\.
        if (!normalized.StartsWith("textures\\", StringComparison.Ordinal) &&
            !normalized.StartsWith("materials\\", StringComparison.Ordinal))
        {
            normalized = "textures\\" + normalized;
        }

        return normalized;
    }

    /// <summary>
    ///     Produces the <c>.dds</c> variant of a texture path for the loader's extension fallback.
    ///     Morrowind (and some Oblivion) NIFs reference textures by their authoring extension
    ///     (<c>.tga</c> / <c>.bmp</c>) while archives store the compiled <c>.dds</c>. Returns false
    ///     when the path has no extension or is already <c>.dds</c> (nothing to swap).
    /// </summary>
    internal static bool TrySwapToDdsExtension(string path, out string ddsPath)
    {
        ddsPath = path;
        if (path.EndsWith(".dds", StringComparison.Ordinal))
        {
            return false;
        }

        var dot = path.LastIndexOf('.');
        var slash = path.LastIndexOf('\\');
        if (dot <= slash || dot < 0)
        {
            return false; // no extension (the last '.' is in a directory name, or absent)
        }

        ddsPath = string.Concat(path.AsSpan(0, dot), ".dds");
        return true;
    }
}
