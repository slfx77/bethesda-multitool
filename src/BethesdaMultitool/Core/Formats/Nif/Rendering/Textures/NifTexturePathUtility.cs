namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;

/// <summary>
///     Normalizes texture paths into the canonical BSA lookup format.
/// </summary>
internal static class NifTexturePathUtility
{
    internal static string Normalize(string path)
    {
        var normalized = path.Replace('/', '\\').ToLowerInvariant().Trim();

        // Peel any path prefix down to the Data-relative portion that archives index by. Two forms:
        //  • A leading "data\" step — vanilla FNV's WATR DefaultWater NNAM authors the path relative
        //    to the game directory (parent of Data\), e.g. "data\textures\water\genaratednoise01.dds".
        //  • An absolute developer build path baked into the asset — extremely common on FO4/FO76,
        //    where a NIF's BSLightingShaderProperty Name (and some material texture entries) ships as
        //    e.g. "C:\Projects\Fallout4\Build\PC\Data\Materials\Architecture\X.bgsm". Without peeling,
        //    the lookup misses and every such FO4/FO76 shape renders untextured (white).
        // Archive entries are stored relative to Data\, so strip everything up to and including the
        // "...\data\" segment (or the leading "data\"). The engine roots at Data\ the same way.
        var dataSegment = normalized.IndexOf("\\data\\", StringComparison.Ordinal);
        if (dataSegment >= 0)
        {
            normalized = normalized[(dataSegment + "\\data\\".Length)..];
        }
        else if (normalized.StartsWith("data\\", StringComparison.Ordinal))
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
