namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc;

/// <summary>
///     BSAs discovered in a Data folder, classified by content rather than filename. A single archive
///     can provide both meshes and textures (e.g. a mod's <c>&lt;Mod&gt; - Main.bsa</c>), so the same
///     path may appear in both <see cref="MeshesBsaPaths" /> and <see cref="TexturesBsaPaths" />.
/// </summary>
internal sealed record BsaDiscoveryResult(
    string[] MeshesBsaPaths,
    string[] TexturesBsaPaths,
    bool AutoDetected)
{
    public static readonly BsaDiscoveryResult Empty = new([], [], false);

    public bool HasMeshes => MeshesBsaPaths.Length > 0;
}
