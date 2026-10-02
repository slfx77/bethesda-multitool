using BethesdaMultitool.Core.Games;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Units;

/// <summary>
///     One row of the classic (XnGine) unit table (design section 4.1; cut-1c plan section 6.3): the population the row
///     covers, the game it belongs to, the exact meters-per-native-unit factor, its provenance, the evidence behind it
///     and the reverse-engineering item that could replace it.
/// </summary>
/// <param name="Name">The population the row covers, as the design spells it (for example "XnGine .3D, Daggerfall ARCH3D").</param>
/// <param name="Game">The game the row belongs to; <see cref="BethesdaGame.Unknown" /> for the guard row.</param>
/// <param name="MetersPerUnit">The exact factor, as the double literal the reader applies.</param>
/// <param name="Provenance">The factor's provenance (Assumed on every classic row; Unknown on the guard row).</param>
/// <param name="Evidence">The design's evidence, quoted in every document that uses the row.</param>
/// <param name="ReverseEngineeringItem">The design's RE item (RE-2, RE-3, RE-4), or "none" for the guard row.</param>
internal sealed record ClassicModelUnitRow(
    string Name,
    BethesdaGame Game,
    double MetersPerUnit,
    SceneValueProvenance Provenance,
    string Evidence,
    string ReverseEngineeringItem)
{
    /// <summary>The row as the document's unit metadata: the factor, its provenance and its evidence, unchanged.</summary>
    public SceneUnits ToSceneUnits()
    {
        return new SceneUnits(MetersPerUnit, Provenance, Evidence);
    }
}
