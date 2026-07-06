using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Esm.Script;

/// <summary>
///     Game-keyed access to the per-game script command tables. FNV's engine-extracted table doubles
///     as the FO3 table (FO3's is a strict subset with matching opcodes) and as the fallback for
///     unknown inputs — the pre-existing behavior when every consumer called the static
///     <see cref="ScriptFunctionTable" /> directly. Oblivion gets its own engine-extracted table;
///     reading TES4 bytecode/conditions through the FNV table produced wrong names from index 0x172
///     on and wrong param typing from raw id 32 on.
/// </summary>
public static class ScriptFunctionTables
{
    private static readonly ScriptFunctionSet FalloutSet =
        new(BethesdaGame.FalloutNewVegas, ScriptFunctionTable.All);

    private static readonly ScriptFunctionSet OblivionSet =
        new(BethesdaGame.Oblivion, OblivionScriptFunctionTable.Functions);

    public static ScriptFunctionSet For(BethesdaGame game) => game switch
    {
        BethesdaGame.Oblivion => OblivionSet,
        _ => FalloutSet,
    };
}
