using Slfx77.Multitool.Core.Documents;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Shadowkey;

/// <summary>
///     The diagnostics the Shadowkey mesh and zone readers raise (cut-2 plan sections 3.3 to 3.7 and 4.6). Every message
///     is raw text; each code is raised at most once per document it is created for.
/// </summary>
internal static class ShadowkeyModelDiagnostics
{
    /// <summary>A skin holds 0x0F0F texels, drawn opaque (plan section 3.3; the fog table does not pin the key).</summary>
    public const string MagentaOpaque = "bmt.shadowkey.mesh.magenta-opaque";

    /// <summary>A record carries several skins; the entity table or a script picks one, skin 0 is shown (D4).</summary>
    public const string SkinSelection = "bmt.shadowkey.mesh.skin-selection";

    /// <summary>An animated record's clips are Step, Assumed (gap G2).</summary>
    public const string StepInterpolation = "bmt.shadowkey.mesh.step-interpolation";

    /// <summary>A multi-frame sequence at rate 1, which runs as many seconds as it has frames under fps (gap G6).</summary>
    public const string RateSuspect = "bmt.shadowkey.mesh.rate-suspect";

    /// <summary>The record carries the humanoid sequence table: bodies and weapons play it in lockstep (gap G4).</summary>
    public const string ActorAssembly = "bmt.shadowkey.mesh.actor-assembly";

    /// <summary>
    ///     A closed record winds clockwise seen from outside, so a single-sided material culls its outside (cut-2 review
    ///     finding 5; 4 of 79 closed retail records).
    /// </summary>
    public const string ReversedWinding = "bmt.shadowkey.mesh.reversed-winding";

    /// <summary>Always on a zone: which surface a tile uses is unresolved; tiles are untextured (D9).</summary>
    public const string TileTexturingUnresolved = "bmt.shadowkey.zone.tile-texturing-unresolved";

    /// <summary>A zone places meshes: the proper (unmirrored) mesh-to-zone map is assumed (plan section 4.4).</summary>
    public const string ChiralityAssumed = "bmt.shadowkey.zone.chirality-assumed";

    /// <summary>A placement carries a non-zero Angle0 or Angle1, which is not applied (plan section 4.4).</summary>
    public const string PitchRollUnapplied = "bmt.shadowkey.zone.pitch-roll-unapplied";

    /// <summary>A placement resolves to an animated record, shown at frame 0 (D6).</summary>
    public const string StaticPlacements = "bmt.shadowkey.zone.static-placements";

    /// <summary>The zone's sky payload has the uncounted texture header (plan section 4.5).</summary>
    public const string SkyTextureHeader = "bmt.shadowkey.zone.sky-texture-header";

    /// <summary>A placed record carries several skins; the placement shows skin 0 (D4, D6).</summary>
    public const string PlacedSkinDefault = "bmt.shadowkey.zone.placed-skin-default";

    /// <summary>A placed record's skin 0 holds 0x0F0F texels, drawn opaque.</summary>
    public const string PlacedMagentaOpaque = "bmt.shadowkey.zone.placed-magenta-opaque";

    /// <summary>A placed record is closed and wound clockwise seen from outside (<see cref="ReversedWinding" />).</summary>
    public const string PlacedReversedWinding = "bmt.shadowkey.zone.placed-reversed-winding";

    /// <summary>A placement resolves to no resident mesh (0 retail); it is kept in native state only.</summary>
    public const string PlacementUnresolved = "bmt.shadowkey.zone.placement-unresolved";

    /// <summary>A raw-text diagnostic.</summary>
    public static SceneDiagnostic Create(string code, string message)
    {
        return new SceneDiagnostic(code, DocumentText.Raw(message));
    }
}
