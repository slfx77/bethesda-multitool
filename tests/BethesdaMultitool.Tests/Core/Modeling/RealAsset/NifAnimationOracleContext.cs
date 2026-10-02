using System.Text.Json.Nodes;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>What one hop A1-anim comparison (<see cref="NifAnimationDocumentOracle" />) works from and writes into.</summary>
/// <param name="Read">The read under comparison.</param>
/// <param name="Facts">The probe's animation facts by block.</param>
/// <param name="Payloads">The probe's payloads by block.</param>
/// <param name="SquadPolicy">The Squad policy the read's platform should give (null for PS3).</param>
/// <param name="Options">The control mutations.</param>
/// <param name="Report">The report being filled.</param>
internal sealed record NifAnimationOracleContext(
    Cut1bAnimationRead Read,
    IReadOnlyDictionary<int, JsonObject> Facts,
    IReadOnlyDictionary<int, JsonObject> Payloads,
    SceneGamebryoSquadPolicy? SquadPolicy,
    NifAnimationDocumentOracleOptions Options,
    NifAnimationDocumentOracleReport Report);
