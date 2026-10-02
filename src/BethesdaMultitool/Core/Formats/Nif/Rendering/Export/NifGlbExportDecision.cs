namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Export;

/// <summary>The writer a NIF export will use, made by <see cref="NifGlbExportRouting.Decide" />.</summary>
/// <param name="Route">
///     The writer to run. When <paramref name="Refused" /> is true this is the route the preference demanded and could
///     not have; nothing may be written.
/// </param>
/// <param name="Preference">The preference the decision was made for.</param>
/// <param name="Stage">
///     <see cref="NifGlbDeclineStage.None" /> for the normalized route, <see cref="NifGlbDeclineStage.Requested" />
///     when the native writer was asked for, else the stage that declined the normalized route.
/// </param>
/// <param name="Reason">Why the normalized route was not taken, or null when it was.</param>
/// <param name="Refused">
///     True when <see cref="NifGlbWriterPreference.Normalized" /> met an ineligible input. The caller reports an error
///     and writes nothing; there is no silent fallback to the native writer.
/// </param>
internal sealed record NifGlbExportDecision(
    NifGlbExportRoute Route,
    NifGlbWriterPreference Preference,
    NifGlbDeclineStage Stage,
    string? Reason,
    bool Refused)
{
    /// <summary>Whether this decision produces bytes through the normalized shared writer.</summary>
    public bool IsNormalized => Route == NifGlbExportRoute.Normalized && !Refused;
}
