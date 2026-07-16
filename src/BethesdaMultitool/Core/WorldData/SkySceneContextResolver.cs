using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

namespace BethesdaMultitool;

/// <summary>
///     Pure scene classification for weather-driven sky rendering. CELL DATA bit 7 is the single
///     authority for "behaves like exterior"; callers must not independently reinterpret that bit.
/// </summary>
internal readonly record struct ResolvedSkySceneContext(
    bool IsInterior,
    bool BehavesLikeExterior,
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
                IsInterior: false,
                BehavesLikeExterior: false,
                RendersExteriorSky: true,
                selectedExteriorWorldspace,
                CellClimateFormId: null,
                WorldspaceClimateFormId: selectedExteriorWorldspace?.ClimateFormId);
        }

        if (!selectedInterior.BehavesLikeExterior)
        {
            return new ResolvedSkySceneContext(
                IsInterior: true,
                BehavesLikeExterior: false,
                RendersExteriorSky: false,
                Worldspace: null,
                // Retain the authored XCCM identity for diagnostics even though it is not applied.
                CellClimateFormId: selectedInterior.ClimateFormId,
                WorldspaceClimateFormId: null);
        }

        var parent = selectedInterior.WorldspaceFormId is { } expectedParentId
                     && retainedInteriorParentWorldspace?.FormId == expectedParentId
            ? retainedInteriorParentWorldspace
            : null;
        return new ResolvedSkySceneContext(
            IsInterior: true,
            BehavesLikeExterior: true,
            RendersExteriorSky: true,
            parent,
            selectedInterior.ClimateFormId,
            parent?.ClimateFormId);
    }
}
