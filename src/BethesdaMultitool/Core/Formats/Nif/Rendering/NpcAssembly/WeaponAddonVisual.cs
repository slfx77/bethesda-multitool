namespace BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;

/// <summary>A resolved weapon add-on visual: its mesh path and the biped slot flags that govern where it attaches.</summary>
internal sealed class WeaponAddonVisual
{
    public uint BipedFlags { get; init; }
    public string MeshPath { get; init; } = "";
}
