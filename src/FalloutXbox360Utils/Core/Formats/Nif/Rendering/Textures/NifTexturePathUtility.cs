namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Textures;

/// <summary>
///     Normalizes texture paths into the canonical BSA lookup format.
/// </summary>
internal static class NifTexturePathUtility
{
    internal static string Normalize(string path)
    {
        var normalized = path.Replace('/', '\\').ToLowerInvariant().Trim();
        if (!normalized.StartsWith("textures\\", StringComparison.Ordinal))
        {
            normalized = "textures\\" + normalized;
        }

        return normalized;
    }
}
