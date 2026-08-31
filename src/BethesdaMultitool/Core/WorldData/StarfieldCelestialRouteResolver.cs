using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

namespace BethesdaMultitool.Core.WorldData;

/// <summary>Why the proven PNDT-to-STDT-to-SUNP static route did or did not resolve.</summary>
internal enum StarfieldCelestialRouteStatus
{
    Resolved,
    StarDataResolutionFailed,
    SunPresetResolutionFailed
}

/// <summary>
///     One selected STDT and its optional SUNP resolution. <see cref="AuthoredSunPresetFormId" />
///     retains the distinction between an omitted PNAM (null) and an authored null FormID (zero).
/// </summary>
internal sealed record StarfieldCelestialStarRoute(
    StarfieldStarDataRecord Star,
    uint? AuthoredSunPresetFormId,
    StarfieldSunPresetResolution? SunPreset)
{
    internal bool RequiresSunPresetResolution =>
        AuthoredSunPresetFormId is { } formId && formId != 0;

    internal bool IsSunPresetSatisfied =>
        !RequiresSunPresetResolution || SunPreset?.IsResolved == true;

    internal StarfieldSunPresetPatch? EffectiveSunPreset => SunPreset?.EffectivePatch;
}

/// <summary>
///     Source-backed celestial routing selected from one already-resolved PNDT inverse candidate.
///     STDT ambiguity and SUNP inheritance diagnostics remain attached to the result; no TODD,
///     orbital placement, stellar lighting, or other rendering equation is inferred.
/// </summary>
internal sealed record StarfieldCelestialRoute(
    StarfieldCelestialRouteStatus Status,
    StarfieldPlanetWorldspaceCandidate PlanetCandidate,
    StarfieldStarDataResolution StarData,
    StarfieldCelestialStarRoute? PrimaryStar,
    StarfieldCelestialStarRoute? BinaryStar,
    string? FailureDetail = null)
{
    internal bool IsResolved =>
        Status == StarfieldCelestialRouteStatus.Resolved &&
        StarData.IsResolved &&
        PrimaryStar is { IsSunPresetSatisfied: true } &&
        (BinaryStar is null || BinaryStar.IsSunPresetSatisfied);

    internal uint SystemId => PlanetCandidate.Planet.Body.SystemId;
}

/// <summary>
///     Resolves the proven static route PNDT.SystemId -&gt; STDT and every nonzero STDT.PNAM -&gt;
///     SUNP. A selected binary companion is retained and evaluated independently. Missing or zero
///     PNAM is preserved as authored state and does not trigger a SUNP lookup.
/// </summary>
internal static class StarfieldCelestialRouteResolver
{
    internal static StarfieldCelestialRoute Resolve(
        StarfieldPlanetWorldspaceCandidate planetCandidate,
        StarfieldStarDataIndex starDataIndex,
        IReadOnlyDictionary<uint, StarfieldSunPresetRecord> sunPresetsByFormId)
    {
        ArgumentNullException.ThrowIfNull(planetCandidate);
        ArgumentNullException.ThrowIfNull(starDataIndex);
        ArgumentNullException.ThrowIfNull(sunPresetsByFormId);

        var systemId = planetCandidate.Planet.Body.SystemId;
        var starData = StarfieldStarDataResolver.ResolveSystem(systemId, starDataIndex);
        if (!starData.IsResolved)
        {
            return new StarfieldCelestialRoute(
                StarfieldCelestialRouteStatus.StarDataResolutionFailed,
                planetCandidate,
                starData,
                null,
                null,
                starData.FailureDetail ??
                $"Scalar system ID 0x{systemId:X8} did not resolve to a unique STDT route.");
        }

        var primary = ResolveStar(starData.Primary!, sunPresetsByFormId);
        var binary = starData.BinaryStar is null
            ? null
            : ResolveStar(starData.BinaryStar, sunPresetsByFormId);

        var failures = new List<string>(2);
        AddSunPresetFailure("primary", primary, failures);
        if (binary is not null)
        {
            AddSunPresetFailure("binary", binary, failures);
        }

        return failures.Count == 0
            ? new StarfieldCelestialRoute(
                StarfieldCelestialRouteStatus.Resolved,
                planetCandidate,
                starData,
                primary,
                binary)
            : new StarfieldCelestialRoute(
                StarfieldCelestialRouteStatus.SunPresetResolutionFailed,
                planetCandidate,
                starData,
                primary,
                binary,
                string.Join(" ", failures));
    }

    private static StarfieldCelestialStarRoute ResolveStar(
        StarfieldStarDataRecord star,
        IReadOnlyDictionary<uint, StarfieldSunPresetRecord> sunPresetsByFormId)
    {
        var authoredSunPresetFormId = star.Routing!.SunPresetFormId;
        var sunPreset = authoredSunPresetFormId is { } formId && formId != 0
            ? StarfieldSunPresetResolver.Resolve(formId, sunPresetsByFormId)
            : null;
        return new StarfieldCelestialStarRoute(star, authoredSunPresetFormId, sunPreset);
    }

    private static void AddSunPresetFailure(
        string role,
        StarfieldCelestialStarRoute starRoute,
        ICollection<string> failures)
    {
        if (!starRoute.RequiresSunPresetResolution || starRoute.SunPreset?.IsResolved == true)
        {
            return;
        }

        var formId = starRoute.AuthoredSunPresetFormId!.Value;
        failures.Add(
            $"STDT 0x{starRoute.Star.FormId:X8} {role} PNAM 0x{formId:X8} failed: " +
            (starRoute.SunPreset?.FailureDetail ?? "SUNP resolver returned no diagnostic."));
    }
}
