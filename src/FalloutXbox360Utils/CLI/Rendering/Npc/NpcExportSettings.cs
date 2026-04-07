namespace FalloutXbox360Utils.CLI.Rendering.Npc;

internal sealed class NpcExportSettings
{
    public required string MeshesBsaPath { get; init; }
    public string[]? ExtraMeshesBsaPaths { get; init; }
    public required string EsmPath { get; init; }
    public string[]? ExplicitTexturesBsaPaths { get; init; }
    public required string OutputDir { get; init; }
    public string[]? NpcFilters { get; init; }
    public string? DmpPath { get; init; }
    public bool HeadOnly { get; init; }
    public bool NoEquip { get; init; }
    public bool IncludeWeapon { get; init; }
    public bool NoEgm { get; init; }
    public bool NoEgt { get; init; }
    public bool BindPose { get; init; }
    public string? AnimOverride { get; init; }
    public bool NoTextures { get; set; }
    public bool DiagnoseNormals { get; set; }
    public bool NoHair { get; set; }
}
