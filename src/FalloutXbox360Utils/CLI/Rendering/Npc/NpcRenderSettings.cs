using FalloutXbox360Utils.CLI;

namespace FalloutXbox360Utils.CLI.Rendering.Npc;

internal sealed class NpcRenderSettings
{
    public required string MeshesBsaPath { get; init; }
    public required string EsmPath { get; init; }
    public string? ExplicitTexturesBsaPath { get; init; }
    public required string OutputDir { get; init; }
    public string[]? NpcFilters { get; init; }
    public int SpriteSize { get; init; } = 512;
    public string? DmpPath { get; init; }
    public bool ExportEgt { get; init; }
    public bool NoBilinear { get; init; }
    public bool NoEgm { get; init; }
    public bool NoEgt { get; init; }
    public bool NoBump { get; init; }
    public bool NoTex { get; init; }
    public float? BumpStrength { get; init; }
    public bool HeadOnly { get; init; }
    public bool NoEquip { get; init; }
    public bool NoWeapon { get; init; }
    public bool ForceGpu { get; init; }
    public bool ForceCpu { get; init; }
    public bool Skeleton { get; init; }
    public bool BindPose { get; init; }
    public string? AnimOverride { get; init; }
    public CameraConfig Camera { get; init; } = new();
}
