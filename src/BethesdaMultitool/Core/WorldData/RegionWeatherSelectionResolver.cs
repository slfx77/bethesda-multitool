using System.Globalization;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

namespace BethesdaMultitool;

/// <summary>Why a CELL/REGN weather query resolved or fell back to the owning climate.</summary>
internal enum RegionWeatherSelectionStatus
{
    Resolved,
    NoCell,
    InteriorCell,
    InvalidPosition,
    NoWorldspace,
    CellWorldspaceMismatch,
    NoRegionLinks,
    RegionIndexUnavailable,
    UnresolvedRegion,
    RegionWorldspaceMismatch,
    MissingOrInvalidGeometry,
    NoContainingWeatherRegion,
    MultipleContainingWeatherRegions,
    GlobalGatedWeather,
    WeightedWeather,
    AmbiguousWeatherEntries,
    UnresolvedWeather,
}

/// <summary>
///     Pure, fail-closed result for the bounded static-viewer REGN weather policy. A null
///     <see cref="Weather" /> means the caller must keep the climate's default weather.
/// </summary>
internal readonly record struct ResolvedRegionWeatherSelection(
    WeatherRecord? Weather,
    RegionWeatherSelectionStatus Status,
    uint? CellFormId,
    uint? RegionFormId,
    float WorldX,
    float WorldY)
{
    internal bool UsesRegionWeather => Status == RegionWeatherSelectionStatus.Resolved && Weather is not null;

    internal string Telemetry
    {
        get
        {
            static string Id(uint? value) => value is { } id ? $"{id:X8}" : "none";
            var source = UsesRegionWeather ? "region-rdwt" : "climate-fallback";
            var reason = Status switch
            {
                RegionWeatherSelectionStatus.Resolved => "resolved",
                RegionWeatherSelectionStatus.NoCell => "no-cell",
                RegionWeatherSelectionStatus.InteriorCell => "interior-cell",
                RegionWeatherSelectionStatus.InvalidPosition => "invalid-position",
                RegionWeatherSelectionStatus.NoWorldspace => "no-worldspace",
                RegionWeatherSelectionStatus.CellWorldspaceMismatch => "cell-worldspace-mismatch",
                RegionWeatherSelectionStatus.NoRegionLinks => "no-xclr",
                RegionWeatherSelectionStatus.RegionIndexUnavailable => "region-index-unavailable",
                RegionWeatherSelectionStatus.UnresolvedRegion => "unresolved-region",
                RegionWeatherSelectionStatus.RegionWorldspaceMismatch => "region-worldspace-mismatch",
                RegionWeatherSelectionStatus.MissingOrInvalidGeometry => "missing-or-invalid-rpld",
                RegionWeatherSelectionStatus.NoContainingWeatherRegion => "outside-region-polygons",
                RegionWeatherSelectionStatus.MultipleContainingWeatherRegions => "multiple-containing-weather-regions",
                RegionWeatherSelectionStatus.GlobalGatedWeather => "global-gated-rdwt",
                RegionWeatherSelectionStatus.WeightedWeather => "weighted-rdwt",
                RegionWeatherSelectionStatus.AmbiguousWeatherEntries => "ambiguous-rdwt",
                RegionWeatherSelectionStatus.UnresolvedWeather => "unresolved-weather",
                _ => "unknown",
            };
            return $"regionWeatherSource={source} cell={Id(CellFormId)} region={Id(RegionFormId)} " +
                   $"weather={Id(Weather?.FormId)} point=({WorldX.ToString("0.###", CultureInfo.InvariantCulture)}," +
                   $"{WorldY.ToString("0.###", CultureInfo.InvariantCulture)}) reason={reason}";
        }
    }
}

/// <summary>
///     Resolves a deterministic region weather at one exterior world position. CELL XCLR only
///     supplies candidate REGNs; the point must be inside retained RPLI/RPLD geometry. This first
///     bounded policy deliberately rejects engine-state-dependent cases: overlapping weather
///     regions, weighted lists, and GLOB-gated entries all fall back to CLMT.
/// </summary>
internal static class RegionWeatherSelectionResolver
{
    internal static ResolvedRegionWeatherSelection Resolve(
        CellRecord? cell,
        float worldX,
        float worldY,
        uint? activeWorldspaceFormId,
        IReadOnlyDictionary<uint, RegionRecord>? regionsByFormId,
        IReadOnlyDictionary<uint, WeatherRecord>? weathersByFormId)
    {
        ResolvedRegionWeatherSelection Fail(
            RegionWeatherSelectionStatus status,
            uint? regionFormId = null) =>
            new(null, status, cell?.FormId, regionFormId, worldX, worldY);

        if (cell is null)
        {
            return Fail(RegionWeatherSelectionStatus.NoCell);
        }

        if (cell.IsInterior)
        {
            return Fail(RegionWeatherSelectionStatus.InteriorCell);
        }

        if (!float.IsFinite(worldX) || !float.IsFinite(worldY))
        {
            return Fail(RegionWeatherSelectionStatus.InvalidPosition);
        }

        if (activeWorldspaceFormId is not { } worldspaceFormId || worldspaceFormId == 0)
        {
            return Fail(RegionWeatherSelectionStatus.NoWorldspace);
        }

        if (cell.WorldspaceFormId is { } cellWorldspaceFormId and not 0 &&
            cellWorldspaceFormId != worldspaceFormId)
        {
            return Fail(RegionWeatherSelectionStatus.CellWorldspaceMismatch);
        }

        var regionFormIds = cell.RegionFormIds;
        if (regionFormIds.Count == 0)
        {
            return Fail(RegionWeatherSelectionStatus.NoRegionLinks);
        }

        if (regionsByFormId is null)
        {
            return Fail(RegionWeatherSelectionStatus.RegionIndexUnavailable);
        }

        var containingWeatherRegions = new List<RegionRecord>(1);
        foreach (var regionFormId in regionFormIds.Distinct())
        {
            if (!regionsByFormId.TryGetValue(regionFormId, out var region))
            {
                // A partial capture cannot prove that the missing candidate has no weather data.
                return Fail(RegionWeatherSelectionStatus.UnresolvedRegion, regionFormId);
            }

            if (region.WorldspaceFormId != worldspaceFormId)
            {
                return Fail(RegionWeatherSelectionStatus.RegionWorldspaceMismatch, region.FormId);
            }

            if (region.Areas.Count == 0 || region.Areas.Any(area => !IsValidPolygon(area.Points)))
            {
                return Fail(RegionWeatherSelectionStatus.MissingOrInvalidGeometry, region.FormId);
            }

            // Runtime-only REGN records do not retain point lists or RDAT payloads. Checking
            // geometry before WeatherTypes means such a candidate cannot masquerade as a proven
            // non-weather region when no authoritative runtime Sky snapshot is available.
            if (region.WeatherTypes.Count == 0)
            {
                continue;
            }

            if (region.Areas.Any(area => ContainsPoint(area.Points, worldX, worldY)))
            {
                containingWeatherRegions.Add(region);
            }
        }

        if (containingWeatherRegions.Count == 0)
        {
            return Fail(RegionWeatherSelectionStatus.NoContainingWeatherRegion);
        }

        if (containingWeatherRegions.Count != 1)
        {
            return Fail(RegionWeatherSelectionStatus.MultipleContainingWeatherRegions);
        }

        var selectedRegion = containingWeatherRegions[0];
        var weatherTypes = selectedRegion.WeatherTypes;
        if (weatherTypes.Any(entry => entry.GlobalFormId != 0))
        {
            return Fail(RegionWeatherSelectionStatus.GlobalGatedWeather, selectedRegion.FormId);
        }

        if (weatherTypes.Any(entry => entry.Chance != 100))
        {
            return Fail(RegionWeatherSelectionStatus.WeightedWeather, selectedRegion.FormId);
        }

        if (weatherTypes.Count != 1)
        {
            return Fail(RegionWeatherSelectionStatus.AmbiguousWeatherEntries, selectedRegion.FormId);
        }

        var weatherFormId = weatherTypes[0].WeatherFormId;
        if (weatherFormId == 0 ||
            weathersByFormId?.TryGetValue(weatherFormId, out var weather) != true)
        {
            return Fail(RegionWeatherSelectionStatus.UnresolvedWeather, selectedRegion.FormId);
        }

        return new ResolvedRegionWeatherSelection(
            weather,
            RegionWeatherSelectionStatus.Resolved,
            cell.FormId,
            selectedRegion.FormId,
            worldX,
            worldY);
    }

    private static bool IsValidPolygon(IReadOnlyList<RegionPoint> points)
    {
        if (points.Count < 3 || points.Any(point => !float.IsFinite(point.X) || !float.IsFinite(point.Y)))
        {
            return false;
        }

        double twiceArea = 0;
        for (var i = 0; i < points.Count; i++)
        {
            var current = points[i];
            var next = points[(i + 1) % points.Count];
            twiceArea += ((double)current.X * next.Y) - ((double)next.X * current.Y);
        }

        return Math.Abs(twiceArea) > 1e-4;
    }

    private static bool ContainsPoint(IReadOnlyList<RegionPoint> points, float worldX, float worldY)
    {
        var inside = false;
        for (int i = 0, previousIndex = points.Count - 1; i < points.Count; previousIndex = i++)
        {
            var current = points[i];
            var previous = points[previousIndex];
            if (PointOnSegment(previous, current, worldX, worldY))
            {
                return true;
            }

            var crossesScanline = (current.Y > worldY) != (previous.Y > worldY);
            if (!crossesScanline)
            {
                continue;
            }

            var intersectionX = previous.X +
                                (((double)worldY - previous.Y) * (current.X - previous.X) /
                                 (current.Y - previous.Y));
            if (worldX < intersectionX)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static bool PointOnSegment(RegionPoint start, RegionPoint end, float x, float y)
    {
        var dx = (double)end.X - start.X;
        var dy = (double)end.Y - start.Y;
        var lengthSquared = (dx * dx) + (dy * dy);
        const double epsilon = 1e-4;
        if (lengthSquared <= epsilon * epsilon)
        {
            var pointDx = (double)x - start.X;
            var pointDy = (double)y - start.Y;
            return (pointDx * pointDx) + (pointDy * pointDy) <= epsilon * epsilon;
        }

        var cross = (((double)x - start.X) * dy) - (((double)y - start.Y) * dx);
        if (Math.Abs(cross) > epsilon * Math.Sqrt(lengthSquared))
        {
            return false;
        }

        var dot = (((double)x - start.X) * dx) + (((double)y - start.Y) * dy);
        return dot >= -epsilon && dot <= lengthSquared + epsilon;
    }
}
