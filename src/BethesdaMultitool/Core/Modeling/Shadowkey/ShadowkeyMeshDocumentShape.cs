namespace BethesdaMultitool.Core.Modeling.Shadowkey;

/// <summary>The three shapes one mesh record's document takes (cut-2 plan section 2, decision D6).</summary>
internal enum ShadowkeyMeshDocumentShape
{
    /// <summary>
    ///     A record read on its own (<c>mesh info</c>, <c>mesh convert</c> of a slot): every skin as an exclusive layer
    ///     set, frames 1 to N-1 as absolute morph targets, one clip per sequence, the native rows, the diagnostics.
    /// </summary>
    Standalone,

    /// <summary>
    ///     A record placed by a zone's <c>.ent</c>: frame 0 and skin 0 only, no targets, clips, native rows or
    ///     diagnostics (the zone states them once), and the short unit and basis evidence, so a zone of 1,072 placements
    ///     stays well inside the composition budgets. One document serves every placement of its slot.
    /// </summary>
    Placement,

    /// <summary>A zone's <c>.zsk</c> payload: the Standalone document without targets and clips (skies have one frame).</summary>
    Sky
}
