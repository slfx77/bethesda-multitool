namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc;

/// <summary>
///     Auto-detects BSA files (meshes + textures) from an ESM file's directory.
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

        var meshesBsas = Directory.GetFiles(dir, "*Meshes*.bsa")
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var texturesBsas = Directory.GetFiles(dir, "*Texture*.bsa")
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (meshesBsas.Length == 0)
        {
            return BsaDiscoveryResult.Empty;
        }

        return new BsaDiscoveryResult(
            meshesBsas[0],
            meshesBsas.Length > 1 ? meshesBsas[1..] : null,
            texturesBsas,
            true);
    }
}