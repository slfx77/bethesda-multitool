using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Scene;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Lighting;

/// <summary>Per-cell local-light cap policy shared by the live upload path and unit tests.</summary>
internal static class PlacedLightSelector
{
    /// <summary>
    ///     Appends at most <paramref name="maxPerCell" /> enabled emitters, nearest to the camera.
    ///     Per-reference On/Off previews override the emitter's authored disabled state and the global
    ///     show-disabled diagnostic; the caller remains responsible for scene-wide lighting gates.
    ///     For emitters a day/night script drives, the current-hour state replaces the authored
    ///     REFR/XESP initial state — but a base LIGH authored Off By Default stays dark even inside
    ///     the scripted on-window, matching the engine (the script enables the REFERENCE; the light
    ///     object itself still loads unlit). Returns the number of eligible lights clipped by the cap.
    /// </summary>
    internal static int AppendNearest(
        IReadOnlyList<PlacedLight> source,
        Vector3 cameraPosition,
        int maxPerCell,
        ReferenceEnabledOverrideStore enabledOverrides,
        bool includeInitiallyDisabled,
        List<PlacedLight> destination,
        List<PlacedLight> scratch,
        DayNightRefStateStore? dayNightStates = null)
    {
        if (maxPerCell <= 0 || source.Count == 0) return 0;

        scratch.Clear();
        foreach (var light in source)
        {
            var authoredDisabled = light.IsInitiallyDisabled;
            if (dayNightStates?.TryGetDisabled(light.FormId, out var hourDisabled) == true)
            {
                authoredDisabled = hourDisabled || (light.Flags & PlacedLight.OffByDefaultFlag) != 0;
            }

#pragma warning disable S1244 // exact zero intensity means the light emits nothing; near-zero lights still render
            if (!enabledOverrides.IsVisible(
                    light.FormId, authoredDisabled, includeInitiallyDisabled) ||
                !light.HasEmission)
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
