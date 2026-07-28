using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Lighting;

/// <summary>Per-cell local-light cap policy shared by the live upload path and unit tests.</summary>
internal static class PlacedLightSelector
{
    /// <summary>
    ///     Appends at most <paramref name="maxPerCell" /> enabled emitters, nearest to the camera.
    ///     Returns the number of eligible lights clipped by the cap.
    /// </summary>
    internal static int AppendNearest(
        IReadOnlyList<PlacedLight> source,
        Vector3 cameraPosition,
        int maxPerCell,
        bool includeInitiallyDisabled,
        List<PlacedLight> destination,
        List<PlacedLight> scratch)
    {
        if (maxPerCell <= 0 || source.Count == 0) return 0;

        scratch.Clear();
        foreach (var light in source)
        {
#pragma warning disable S1244 // exact zero intensity means the light emits nothing; near-zero lights still render
            if ((!includeInitiallyDisabled && light.IsInitiallyDisabled) ||
                light.Intensity == 0f || light.Radius <= 0f)
#pragma warning restore S1244
            {
                continue;
            }
            scratch.Add(light);
        }

        if (scratch.Count > maxPerCell)
        {
            scratch.Sort((left, right) =>
            {
                var distanceOrder = Vector3.DistanceSquared(left.Position, cameraPosition)
                    .CompareTo(Vector3.DistanceSquared(right.Position, cameraPosition));
                return distanceOrder != 0 ? distanceOrder : left.FormId.CompareTo(right.FormId);
            });
        }

        var selected = Math.Min(scratch.Count, maxPerCell);
        for (var i = 0; i < selected; i++) destination.Add(scratch[i]);
        return scratch.Count - selected;
    }
}
