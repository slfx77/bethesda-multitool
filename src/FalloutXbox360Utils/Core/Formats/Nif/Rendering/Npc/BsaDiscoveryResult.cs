namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc;

internal sealed record BsaDiscoveryResult(
    string? MeshesBsaPath,
    string[]? ExtraMeshesBsaPaths,
    string[] TexturesBsaPaths,
    bool AutoDetected)
{
    public static readonly BsaDiscoveryResult Empty = new(null, null, [], false);

    public bool HasMeshes => MeshesBsaPath != null;
}