using BethesdaMultitool.Core;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;

/// <summary>
///     Selects a per-game grass shader pair, or none.
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
/// <param name="Enabled">Whether a recovered per-game grass shader pair exists.</param>
/// <param name="VertexShaderName">Embedded vertex shader file name.</param>
/// <param name="PixelShaderName">Embedded pixel shader file name.</param>
internal readonly record struct GrassShaderProfile(
    bool Enabled,
    string? VertexShaderName,
    string? PixelShaderName)
{
    internal static GrassShaderProfile ForGame(BethesdaGame game)
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

    private static GrassShaderProfile Select(BethesdaGame game) => game switch
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
        // FNV deliberately stays here: its grass goes through the recovered GRASS2000 TallGrass wind
        // route on the shared shaders, and nothing about that has been shown to need its own pair.
        _ => default,
    };
}
