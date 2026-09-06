using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Composition;

/// <summary>Distinct body UV atlases; sharing a tint basis never shares their composed diffuse.</summary>
internal readonly record struct NpcBodyTextureSet(
    string? UpperBody,
    string? LowerBody,
    string? Hands,
    string? Feet,
    string? Tail)
{
    internal static NpcBodyTextureSet FromAppearance(NpcAppearance appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        return new NpcBodyTextureSet(
            appearance.BodyTexturePath,
            appearance.LowerBodyTexturePath,
            appearance.HandTexturePath,
            appearance.FootTexturePath,
            appearance.TailTexturePath);
    }
}
