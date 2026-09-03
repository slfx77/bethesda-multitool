using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

namespace BethesdaMultitool.Core.WorldData;

/// <summary>
///     Pure scene classification for weather-driven sky rendering. Classic "behaves like exterior"
///     and Creation "show sky" are deliberately separate: the latter exposes sky geometry without
///     replacing the cell's authored interior lighting and image-space semantics.
/// </summary>
internal readonly record struct ResolvedSkySceneContext(
    bool IsInterior,
    bool BehavesLikeExterior,
    bool ShowsSky,
    bool UsesSkyLighting,
    bool RendersExteriorSky,
    WorldspaceRecord? Worldspace,
    uint? CellClimateFormId,
    uint? WorldspaceClimateFormId)
{
    /// <summary>CELL XCCM wins over the parent WRLD CNAM when both are authored.</summary>
    internal uint? PreferredClimateFormId => RendersExteriorSky
        ? CellClimateFormId ?? WorldspaceClimateFormId
        : null;
}

internal static class SkySceneContextResolver
{
    /// <summary>
    ///     Resolves the sky-bearing worldspace and climate identity for an exterior selection or an
    ///     interior selection. The supplied parent is accepted only when its FormID matches the CELL's
    ///     retained parent identity, preventing a stale UI selection from supplying another world's sky.
    /// </summary>
    internal static ResolvedSkySceneContext Resolve(
        CellRecord? selectedInterior,
        WorldspaceRecord? selectedExteriorWorldspace,
        WorldspaceRecord? retainedInteriorParentWorldspace)
    {
        if (selectedInterior is null)
        {
            return new ResolvedSkySceneContext(
                false,
                false,
                false,
                false,
                true,
                selectedExteriorWorldspace,
                null,
                selectedExteriorWorldspace?.ClimateFormId);
        }

        var behavesLikeExterior = selectedInterior.BehavesLikeExterior;
        var showsSky = selectedInterior.ShowsSky;
        var usesSkyLighting = selectedInterior.UsesSkyLighting;
        var rendersExteriorSky = behavesLikeExterior || showsSky;
        if (!rendersExteriorSky)
        {
            return new ResolvedSkySceneContext(
                true,
                false,
                false,
                usesSkyLighting,
                false,
                null,
                // Retain the authored XCCM identity for diagnostics even though it is not applied.
                selectedInterior.ClimateFormId,
                null);
        }

        var parent = selectedInterior.WorldspaceFormId is { } expectedParentId
                     && retainedInteriorParentWorldspace?.FormId == expectedParentId
            ? retainedInteriorParentWorldspace
            : null;
        return new ResolvedSkySceneContext(
            true,
            behavesLikeExterior,
            showsSky,
            usesSkyLighting,
            true,
            parent,
            selectedInterior.ClimateFormId,
            parent?.ClimateFormId);
    }
}
