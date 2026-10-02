using System.Globalization;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Modeling.Nif;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Starfield;

/// <summary>
///     The units and source basis of a Starfield <c>.mesh</c> document (cut-2 plan sections 3.1 and 4.2, decision D12).
///     A <c>.mesh</c> belongs only to Starfield, so the unit row is always the Starfield row of <see cref="GameProfiles" />
///     (1.0 m per unit, Assumed, the design's units-table row "Starfield NIF + .mesh"), with that row's own evidence.
///     The basis is the NIF basis object itself (<see cref="NifModelUnits.Basis" />): a <c>.mesh</c> is one level of one
///     <c>BSGeometry</c> shape and is authored in that block's local frame, which is the NIF frame.
/// </summary>
/// <remarks>
///     The <see cref="BethesdaModelRegistration.GameOption" /> app option may be absent, <c>auto</c> or name Starfield
///     (any case); any other value throws <see cref="ArgumentException" />, because the user asserted a game the file
///     cannot belong to (as the XnGine readers do under a NIF-era game).
/// </remarks>
internal static class StarfieldMeshModelUnits
{
    /// <summary>The evidence prefix stated when no game option names Starfield.</summary>
    public const string ImpliedGameEvidence = "Starfield (a .mesh belongs only to Starfield)";

    /// <summary>The Starfield unit row this reader declares.</summary>
    public static WorldUnitScale Row => GameProfiles.For(BethesdaGame.Starfield).Units;

    /// <summary>The source basis: the NIF basis object (+Z up, +Y forward, right-handed, Assumed).</summary>
    public static SceneSourceBasis Basis => NifModelUnits.Basis;

    /// <summary>The units a document declares when no option is given, as the format metadata states them.</summary>
    public static SceneUnits Default { get; } = new(Row.MetersPerUnit, NifModelUnits.ToSceneProvenance(Row.Provenance),
        ImpliedGameEvidence + ": " + Row.Evidence);

    /// <summary>Resolves the document units from the read's app options (see the type remarks).</summary>
    /// <exception cref="ArgumentException">The game option names a game other than Starfield.</exception>
    public static SceneUnits Resolve(IReadOnlyDictionary<string, string> appOptions)
    {
        ArgumentNullException.ThrowIfNull(appOptions);
        if (!appOptions.TryGetValue(BethesdaModelRegistration.GameOption, out var value) ||
            string.Equals(value.Trim(), "auto", StringComparison.OrdinalIgnoreCase))
        {
            return Default;
        }

        if (!string.Equals(value.Trim(), nameof(BethesdaGame.Starfield), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                    $"The {BethesdaModelRegistration.GameOption} option '{value}' names a game other than Starfield, " +
                    $"but a .mesh belongs only to Starfield (use starfield or auto)."),
                nameof(appOptions));
        }

        var source = appOptions.TryGetValue(BethesdaModelRegistration.GameEvidenceOption, out var evidence) &&
                     !string.IsNullOrWhiteSpace(evidence)
            ? $"{BethesdaModelRegistration.GameOption}={value} ({evidence})"
            : $"{BethesdaModelRegistration.GameOption}={value}";
        var row = Row;
        return new SceneUnits(row.MetersPerUnit, NifModelUnits.ToSceneProvenance(row.Provenance),
            string.Create(CultureInfo.InvariantCulture, $"{BethesdaGame.Starfield} per {source}: {row.Evidence}"));
    }
}
