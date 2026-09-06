using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Composition;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Assets;

/// <summary>
///     Selects the actor's body atlas using PC Oblivion 0047AC20's material/name gate.
///     This is a composition policy, not a raw-NIF shader or texture-filename heuristic.
/// </summary>
internal static class OblivionNpcBodyMaterialPolicy
{
    internal static string? ResolveTextureOverride(RenderableSubmesh submesh, NpcBodyTextureSet textures)
    {
        return ResolveTexturePart(submesh) switch
        {
            NpcBodyTexturePart.UpperBody => textures.UpperBody,
            NpcBodyTexturePart.LowerBody => textures.LowerBody,
            NpcBodyTexturePart.Hands => textures.Hands,
            NpcBodyTexturePart.Feet => textures.Feet,
            NpcBodyTexturePart.Tail => textures.Tail,
            _ => null
        };
    }

    internal static NpcBodyTexturePart? ResolveTexturePart(RenderableSubmesh submesh)
    {
        ArgumentNullException.ThrowIfNull(submesh);
        if (!string.Equals(submesh.LegacyMaterialName, "skin", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(submesh.ShapeName))
        {
            return null;
        }

        var name = submesh.ShapeName;
        if (name.StartsWith("UpperBody", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Arms", StringComparison.OrdinalIgnoreCase))
        {
            return NpcBodyTexturePart.UpperBody;
        }

        if (name.StartsWith("Hand", StringComparison.OrdinalIgnoreCase))
        {
            return NpcBodyTexturePart.Hands;
        }

        if (name.StartsWith("Foot", StringComparison.OrdinalIgnoreCase))
        {
            return NpcBodyTexturePart.Feet;
        }

        if (name.StartsWith("Tail", StringComparison.OrdinalIgnoreCase))
        {
            return NpcBodyTexturePart.Tail;
        }

        // Retail initializes part 3 before its prefix dispatch. Both LowerBody and an
        // unrecognized nonempty skin shape retain that lower-body atlas (the latter warns).
        // Missing names are rejected above instead of reproducing retail's unsafe dereference.
        return NpcBodyTexturePart.LowerBody;
    }
}
