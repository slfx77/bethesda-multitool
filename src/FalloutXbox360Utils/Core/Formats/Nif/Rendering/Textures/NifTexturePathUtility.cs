namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Textures;

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

        if (!normalized.StartsWith("textures\\", StringComparison.Ordinal))
        {
            normalized = "textures\\" + normalized;
        }

        return normalized;
    }
}
