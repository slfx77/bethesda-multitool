using System.Globalization;
using System.Numerics;
using BethesdaMultitool.Core.Games;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     The NIF document's units and source basis (plan section 3, "Document and header"; design section 4.1). Units come
///     from <see cref="GameProfiles" /> for the game named by the <see cref="BethesdaModelRegistration.GameOption" /> app
///     option (the shell sets it from <c>--game</c> or from the ESM found under the data root), with the profile's own
///     provenance and evidence. Without the option the game is not established, and the FNV value is used as
///     <see cref="SceneValueProvenance.Assumed" />: BS 14 to 34 ship in both FNV and FO3, whose values differ by 0.0012%.
/// </summary>
/// <remarks>
///     Accepted option values are <c>fnv</c>, <c>fo3</c> and the <see cref="BethesdaGame" /> names of the games that ship
///     Gamebryo 20.x NIF streams (Oblivion, Fallout3, FalloutNewVegas, Skyrim, Fallout4, Fallout76, Starfield), matched
///     without regard to case; <c>auto</c> means the shell did not establish a game and is treated as absent. Any other
///     value is an error rather than a silent fallback.
/// </remarks>
internal static class NifModelUnits
{
    /// <summary>The evidence stated when no game is established.</summary>
    public const string AssumedGameEvidence =
        "BS 14\u201334 ship in FNV and FO3; game not established; FO3's 1/69.9904 differs by 0.0012%";

    /// <summary>The evidence stated for the NIF source basis.</summary>
    public const string BasisEvidence =
        "Gamebryo Z-up right-handed; BMT GLB exporter maps (x,y,z)\u2192(x,z,\u2212y)";

    private static readonly BethesdaGame[] NifGames =
    [
        BethesdaGame.Oblivion, BethesdaGame.Fallout3, BethesdaGame.FalloutNewVegas, BethesdaGame.Skyrim,
        BethesdaGame.Fallout4, BethesdaGame.Fallout76, BethesdaGame.Starfield
    ];

    /// <summary>
    ///     The NIF basis: +Z up, +Y forward, right-handed, not normalized by the reader, Assumed. The GLB writer requires
    ///     cardinal up and forward directions (ModelGltfCoordinates.cs:125-141).
    /// </summary>
    public static SceneSourceBasis Basis { get; } = new(Vector3.UnitZ, Vector3.UnitY, SceneHandedness.RightHanded,
        normalizedByReader: false, SceneValueProvenance.Assumed, BasisEvidence);

    /// <summary>Resolves the document units from the read's app options.</summary>
    /// <exception cref="ArgumentException">The game option names no NIF-era game.</exception>
    public static SceneUnits Resolve(IReadOnlyDictionary<string, string> appOptions)
    {
        ArgumentNullException.ThrowIfNull(appOptions);
        if (!appOptions.TryGetValue(BethesdaModelRegistration.GameOption, out var value) ||
            string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase))
        {
            var assumed = GameProfiles.For(BethesdaGame.FalloutNewVegas).Units;
            return new SceneUnits(assumed.MetersPerUnit, SceneValueProvenance.Assumed, AssumedGameEvidence);
        }

        var game = ParseGame(value);
        var units = GameProfiles.For(game).Units;
        var source = appOptions.TryGetValue(BethesdaModelRegistration.GameEvidenceOption, out var evidence) &&
                     !string.IsNullOrWhiteSpace(evidence)
            ? $"{BethesdaModelRegistration.GameOption}={value} ({evidence})"
            : $"{BethesdaModelRegistration.GameOption}={value}";
        return new SceneUnits(units.MetersPerUnit, ToSceneProvenance(units.Provenance),
            string.Create(CultureInfo.InvariantCulture, $"{game} per {source}: {units.Evidence}"));
    }

    /// <summary>Parses a game option value (see the type remarks).</summary>
    /// <exception cref="ArgumentException">The value names no NIF-era game.</exception>
    public static BethesdaGame ParseGame(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var trimmed = value.Trim();
        if (string.Equals(trimmed, "fnv", StringComparison.OrdinalIgnoreCase))
        {
            return BethesdaGame.FalloutNewVegas;
        }

        if (string.Equals(trimmed, "fo3", StringComparison.OrdinalIgnoreCase))
        {
            return BethesdaGame.Fallout3;
        }

        foreach (var game in NifGames)
        {
            if (string.Equals(trimmed, game.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return game;
            }
        }

        throw new ArgumentException(
            $"The {BethesdaModelRegistration.GameOption} option '{value}' names no game that ships Gamebryo 20.x NIF " +
            "files (use fnv, fo3 or one of Oblivion, Fallout3, FalloutNewVegas, Skyrim, Fallout4, Fallout76, Starfield).",
            nameof(value));
    }

    /// <summary>Maps a game-profile provenance to the document's provenance vocabulary.</summary>
    public static SceneValueProvenance ToSceneProvenance(UnitProvenance provenance)
    {
        return provenance switch
        {
            UnitProvenance.ReverseEngineered => SceneValueProvenance.ReverseEngineered,
            UnitProvenance.Authored => SceneValueProvenance.Authored,
            UnitProvenance.Assumed => SceneValueProvenance.Assumed,
            _ => throw new ArgumentOutOfRangeException(nameof(provenance), provenance, "Unknown unit provenance.")
        };
    }
}
