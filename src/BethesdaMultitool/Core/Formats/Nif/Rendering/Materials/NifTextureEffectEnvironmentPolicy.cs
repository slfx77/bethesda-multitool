using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;

/// <summary>
///     Resolves scene-graph <c>NiTextureEffect</c> ENVIRONMENT_MAP + SPHERE_MAP effects to the
///     shapes they cover. This is the TES3/TES4-era authored-reflection route: the env map is a
///     2D SPHERE map (view-space reflection lookup), NOT the FO3/FNV cube in texture-set slot 4 —
///     it rides the classic env-map submesh payload with <c>ClassicEnvironmentMapIsSphereMap</c>.
///     <para>
///         Application rule (grounded 2026-08-19): a TextureType-2 effect applies to every
///         renderable shape under the node hosting it (nearest hosting ancestor wins), restricted
///         to the authored affected-node subtrees when that list is non-empty, and gated on the
///         Switch State bool. The shape's NiTexturingProperty ApplyMode carries NO signal and does
///         not gate the effect: retail TES4 windows author plain MODULATE
///         (<c>leyawiinwindow01.nif</c> — all three shapes applyMode=2) and retail TES3 chrome
///         (<c>a_glass_boots_gnd.nif</c>) also authors MODULATE beside its effects. Notably, retail
///         TES4 ships ZERO authored NiTextureEffect blocks (sweep of all 8,032 base + 1,580 SI/DLC
///         NIFs) — Oblivion's window reflections are an engine-side RUNTIME NiTextureEffect
///         attachment — so on retail data this policy lights up only for Morrowind glass/ebony
///         chrome; TES4-era support exists for modded and runtime-captured content, which authors
///         the identical block layout.
///     </para>
/// </summary>
internal static class NifTextureEffectEnvironmentPolicy
{
    /// <summary>
    ///     Engine-typical scale for the authored sphere-map pass, v1. UNRECOVERED: the exact
    ///     runtime multiplier (TES4 bDynamicWindowReflections / TES3 chrome blend) still needs a
    ///     decompile or ini audit; NiTextureEffect itself authors no scale field, so 1 is a
    ///     deliberate neutral stand-in rather than a measured value.
    /// </summary>
    internal const float DefaultScale = 1f;

    /// <summary>
    ///     Maps shape block index → sphere-map texture path for every shape covered by an enabled
    ///     ENVIRONMENT_MAP/SPHERE_MAP NiTextureEffect. Returns null when the NIF has none (the
    ///     overwhelmingly common case — the type pre-scan keeps this pass free for ordinary NIFs).
    /// </summary>
    internal static Dictionary<int, string>? ResolveShapeEnvironmentMaps(
        byte[] data,
        NifInfo nif,
        IReadOnlyDictionary<int, List<int>> nodeChildren,
        IEnumerable<int> shapeIndices)
    {
        var hasTextureEffect = false;
        foreach (var block in nif.Blocks)
        {
            if (block.TypeName == "NiTextureEffect")
            {
                hasTextureEffect = true;
                break;
            }
        }

        if (!hasTextureEffect)
        {
            return null;
        }

        // Parse every enabled sphere-map environment effect once and resolve its texture path.
        var effects = new Dictionary<int, (int[] AffectedNodes, string Path)>();
        for (var i = 0; i < nif.Blocks.Count; i++)
        {
            if (nif.Blocks[i].TypeName != "NiTextureEffect")
            {
                continue;
            }

            if (NifTextureEffectReader.Parse(
                    data, nif.Blocks[i], nif.BsVersion, nif.BinaryVersion, nif.IsBigEndian,
                    nif.HasInlineStrings) is not
                {
                    SwitchState: true,
                    TextureType: NifTextureEffectReader.TextureTypeEnvironmentMap,
                    CoordGenType: NifTextureEffectReader.CoordGenTypeSphereMap
                } effect)
            {
                continue;
            }

            if (effect.SourceTextureRef < 0 || effect.SourceTextureRef >= nif.Blocks.Count ||
                !NifTexturingPropertyReader.TryReadSourceTextureFileName(
                    data, nif, effect.SourceTextureRef, out var path) ||
                string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            effects[i] = (effect.AffectedNodes, path);
        }

        if (effects.Count == 0)
        {
            return null;
        }

        // node → the env effect it hosts. Checks the Children list FIRST: Bethesda's TES3 exporter
        // lists the effect only among children with an EMPTY Effects array (byte-verified on
        // a_glass_boots_gnd.nif's root — 25 children incl. 4 effects, Num Effects 0); well-formed
        // Gamebryo files list it in Effects, so both are honored.
        var hostedBy = new Dictionary<int, int>();
        foreach (var (nodeIndex, children) in nodeChildren)
        {
            foreach (var child in children)
            {
                if (effects.ContainsKey(child))
                {
                    hostedBy[nodeIndex] = child;
                    break;
                }
            }

            if (hostedBy.ContainsKey(nodeIndex))
            {
                continue;
            }

            var effectRefs = NifSceneGraphBlockReader.ParseNodeEffects(
                data, nif.Blocks[nodeIndex], nif.BsVersion, nif.BinaryVersion, nif.IsBigEndian,
                nif.HasInlineStrings);
            if (effectRefs is null)
            {
                continue;
            }

            foreach (var effectRef in effectRefs)
            {
                if (effects.ContainsKey(effectRef))
                {
                    hostedBy[nodeIndex] = effectRef;
                    break;
                }
            }
        }

        if (hostedBy.Count == 0)
        {
            return null;
        }

        var parentOf = new Dictionary<int, int>();
        foreach (var (parent, children) in nodeChildren)
        {
            foreach (var child in children)
            {
                parentOf.TryAdd(child, parent);
            }
        }

        var result = new Dictionary<int, string>();
        foreach (var shapeIndex in shapeIndices)
        {
            // Nearest hosting ancestor wins; a scoped-out effect defers to farther ancestors.
            var current = shapeIndex;
            var guard = 0;
            while (parentOf.TryGetValue(current, out var parent) && guard++ < 64)
            {
                if (hostedBy.TryGetValue(parent, out var effectIndex))
                {
                    var (affectedNodes, path) = effects[effectIndex];
                    if (affectedNodes.Length == 0 ||
                        HasAncestorIn(shapeIndex, affectedNodes, parentOf))
                    {
                        result[shapeIndex] = path;
                        break;
                    }
                }

                current = parent;
            }
        }

        return result.Count > 0 ? result : null;
    }

    private static bool HasAncestorIn(
        int shapeIndex, int[] affectedNodes, Dictionary<int, int> parentOf)
    {
        var scope = new HashSet<int>(affectedNodes);
        var current = shapeIndex;
        var guard = 0;
        while (parentOf.TryGetValue(current, out var parent) && guard++ < 64)
        {
            if (scope.Contains(parent))
            {
                return true;
            }

            current = parent;
        }

        return false;
    }
}
