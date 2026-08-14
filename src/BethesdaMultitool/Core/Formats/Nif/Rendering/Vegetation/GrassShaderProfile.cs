using BethesdaMultitool.Core;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Abstractions;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Vegetation;

/// <summary>
///     Selects the per-game grass <see cref="GameShaderPair" />, or none.
///     <para>
///         The axis is deliberately PER-VARIANT, not per-game: a profile arm exists only where that
///         game's own shaders have actually been recovered and transcribed. Games with no recovered
///         grass shader fall to <c>default</c> and keep rendering through the shared reference path
///         exactly as before — no empty per-game file is created for them, because an empty slot is
///         an invitation to invent engine behaviour rather than derive it.
///     </para>
///     <para>
///         Sibling of <see cref="GrassScatterProfile" /> and <c>ClassicSpecularLodProfile</c>, and
///         the same <c>ForGame</c> shape used across the renderer's per-game registries.
///     </para>
/// </summary>
internal static class GrassShaderProfile
{
    internal static GameShaderPair ForGame(BethesdaGame game)
    {
        // Explicit opt-OUT so the per-game lighting can be compared against the shared path in one
        // session without a rebuild. Unset means enabled — the recovered shader is the default.
        if (string.Equals(
                EnvironmentVariables.Get(EnvironmentVariables.Viewer.PerGameGrassShader),
                "0",
                StringComparison.Ordinal))
        {
            return default;
        }

        return Select(game);
    }

    /// <summary>
    ///     The INSTANCED-opaque axis, distinct from <see cref="ForGame" />'s blended per-draw axis.
    ///     A pair returned here is compiled against the instance ABI (SV_InstanceID + the t8 world
    ///     matrix buffer) and is consumed by the pipeline factory's grass cutout PSOs; it must never
    ///     be handed to the blended route, whose shaders take their world matrix from the per-draw
    ///     constant buffer instead. One env knob opts out of both axes.
    /// </summary>
    internal static GameShaderPair InstancedForGame(BethesdaGame game)
    {
        if (string.Equals(
                EnvironmentVariables.Get(EnvironmentVariables.Viewer.PerGameGrassShader),
                "0",
                StringComparison.Ordinal))
        {
            return default;
        }

        return SelectInstanced(game);
    }

    /// <summary>
    ///     The INSTANCED + BLENDED axis: games whose grass is alpha-BLENDED (so it cannot join the
    ///     cutout PSOs <see cref="InstancedForGame" /> feeds) but which still batch, via a vertex
    ///     shader compiled with GRASS_INSTANCED. This is the same pair <see cref="ForGame" /> returns
    ///     — deliberately, so the recovered per-game lighting has ONE implementation and the two
    ///     routes cannot drift; only the ABI the VS is compiled against differs.
    /// </summary>
    internal static GameShaderPair InstancedBlendForGame(BethesdaGame game)
    {
        if (string.Equals(
                EnvironmentVariables.Get(EnvironmentVariables.Viewer.PerGameGrassShader),
                "0",
                StringComparison.Ordinal))
        {
            return default;
        }

        // TES4 only. FO3/FNV grass is alpha-TESTED (0x12EC), so it already batches through the
        // cutout PSOs and must not be handed a blended pipeline.
        return game is BethesdaGame.Oblivion ? Select(game) : default;
    }

    private static GameShaderPair SelectInstanced(BethesdaGame game) => game switch
    {
        // FO3/FNV: retail Shaders\shaderpackage019.sdp, GRASS2002.vso + GRASS2002.pso. The engine
        // lights grass from terrain data baked into each instance (land normal + land-colour
        // luminance), composes ambient ADDITIVELY without vertex colour, applies a 1.5x sun boost,
        // and attenuates ONLY the sun term by the shadow map. Both games share TallGrassShader.cpp
        // and every shipped GRAS record in both authors DATA flags 0x06, so one pair serves both.
        BethesdaGame.FalloutNewVegas or BethesdaGame.Fallout3 => new(
            true, "reference_grass_fnv.vert.hlsl", "reference_grass_fnv.frag.hlsl"),

        _ => default,
    };

    private static GameShaderPair Select(BethesdaGame game) => game switch
    {
        // Oblivion: retail Shaders\shaderpackage019.sdp, GRASS2020.vso + GRASS2002.pso, disassembled
        // 2026-07-26 (tools/GhidraProject/oblivion_grass_shaderpackage019_disassembled.txt). Retail
        // lights grass from the TERRAIN normal with no surface normal anywhere and composes ambient
        // ADDITIVELY; the shared path uses the blade card's own near-vertical normal multiplicatively,
        // which collapses at high sun and is the reported "grass too dark".
        BethesdaGame.Oblivion => new(
            true, "reference_grass_oblivion.vert.hlsl", "reference_grass_oblivion.frag.hlsl"),

        // Everything else — including BethesdaGame.Unknown, which is what the headless NIF renderer
        // passes (NifHeadlessRenderer hands ReferenceRenderer12 a default WorldRenderCache). The
        // fallback is STRUCTURAL rather than a branch at the call site: disabled simply means the
        // existing shared shaders are used, so no path needs to know a per-game shader might exist.
        // FO3/FNV grass is not here because it draws INSTANCED, not blended — its recovered pair
        // lives on the instanced axis (see InstancedForGame).
        _ => default,
    };
}
