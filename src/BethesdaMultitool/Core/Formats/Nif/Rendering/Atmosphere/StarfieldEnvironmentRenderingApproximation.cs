using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Atmosphere;

/// <summary>
///     Source channels projected by the bounded Starfield environment approximation. This is an
///     observability contract as well as a bit mask: a capture can distinguish a real authored input
///     from the legacy fallback without claiming that the unrecovered CE2 atmosphere equations ran.
/// </summary>
[Flags]
internal enum StarfieldEnvironmentApproximationChannels
{
    None = 0,
    SunPresetDiscColor = 1 << 0,
    SunPresetGlareColor = 1 << 1,
    WeatherFogNear = 1 << 2,
    WeatherFogFar = 1 << 3,
    WeatherSunDisc = 1 << 4,
    WeatherSunGlare = 1 << 5
}

/// <summary>One allocation-free projection result suitable for the per-frame atmosphere path.</summary>
internal readonly record struct StarfieldEnvironmentApproximationResult(
    AtmosphereState.Resolved Atmosphere,
    StarfieldEnvironmentApproximationChannels AppliedChannels,
    StarfieldEnvironmentApproximationChannels RejectedChannels)
{
    internal const string Name = "starfield-source-backed-environment-approx";
}

/// <summary>
///     Bounded bridge from resolved Starfield SUNP/WTHS source values into renderer channels whose
///     names and units already match. This is deliberately not a CE2 scattering implementation:
///     altitude fog, TODD scheduling, sunlight/moonlight switching, illuminance/exposure, cloud
///     integration, and sun-size equations remain untouched until independently recovered.
/// </summary>
internal static class StarfieldEnvironmentRenderingApproximation
{
    internal static StarfieldEnvironmentApproximationResult Apply(
        AtmosphereState.Resolved baseline,
        StarfieldWeatherSettingsPatch? weather,
        StarfieldSunPresetPatch? sunPreset)
    {
        var result = baseline;
        var applied = StarfieldEnvironmentApproximationChannels.None;
        var rejected = StarfieldEnvironmentApproximationChannels.None;

        if (sunPreset?.SunColor is { } presetSun)
        {
            if (TryColor(presetSun, out var color))
            {
                result = result with { SunDiscColor = color };
                applied |= StarfieldEnvironmentApproximationChannels.SunPresetDiscColor;
            }
            else
            {
                rejected |= StarfieldEnvironmentApproximationChannels.SunPresetDiscColor;
            }
        }

        if (sunPreset?.SunGlareColor is { } presetGlare)
        {
            if (TryColor(presetGlare, out var color))
            {
                result = result with { SunGlareColor = color };
                applied |= StarfieldEnvironmentApproximationChannels.SunPresetGlareColor;
            }
            else
            {
                rejected |= StarfieldEnvironmentApproximationChannels.SunPresetGlareColor;
            }
        }

        var colors = weather?.Colors;
        if (colors?.FogNear is { } fogNear)
        {
            if (TrySetColor(fogNear, new Vector4(result.FogColor, 1f), out var color))
            {
                result = result with { FogColor = new Vector3(color.X, color.Y, color.Z) };
                applied |= StarfieldEnvironmentApproximationChannels.WeatherFogNear;
            }
            else
            {
                rejected |= StarfieldEnvironmentApproximationChannels.WeatherFogNear;
            }
        }

        if (colors?.FogFar is { } fogFar)
        {
            if (TrySetColor(fogFar, new Vector4(result.FogFarColor, 1f), out var color))
            {
                result = result with { FogFarColor = new Vector3(color.X, color.Y, color.Z) };
                applied |= StarfieldEnvironmentApproximationChannels.WeatherFogFar;
            }
            else
            {
                rejected |= StarfieldEnvironmentApproximationChannels.WeatherFogFar;
            }
        }

        if (colors?.Sun is { } sun)
        {
            if (TrySetColor(sun, result.SunDiscColor, out var color))
            {
                result = result with { SunDiscColor = color };
                applied |= StarfieldEnvironmentApproximationChannels.WeatherSunDisc;
            }
            else
            {
                rejected |= StarfieldEnvironmentApproximationChannels.WeatherSunDisc;
            }
        }

        if (colors?.SunGlare is { } sunGlare)
        {
            if (TrySetColor(sunGlare, result.SunGlareColor, out var color))
            {
                result = result with { SunGlareColor = color };
                applied |= StarfieldEnvironmentApproximationChannels.WeatherSunGlare;
            }
            else
            {
                rejected |= StarfieldEnvironmentApproximationChannels.WeatherSunGlare;
            }
        }

        return new StarfieldEnvironmentApproximationResult(result, applied, rejected);
    }

    private static bool TrySetColor(
        StarfieldBlendableColorPatch patch,
        Vector4 baseline,
        out Vector4 color)
    {
        color = baseline;
        if (!string.Equals(patch.Operation, "Set", StringComparison.OrdinalIgnoreCase) ||
            patch.Value is not { } value ||
            patch.BlendAmount is not { } blendAmount ||
            !float.IsFinite(blendAmount) || blendAmount < 0f || blendAmount > 1f ||
            !TryColor(value, out var authored))
        {
            return false;
        }

        color = Vector4.Lerp(baseline, authored, blendAmount);
        return true;
    }

    private static bool TryColor(StarfieldSunPresetFloat4Patch source, out Vector4 color)
    {
        color = default;
        if (source is not { X: { } x, Y: { } y, Z: { } z, W: { } w } ||
            !float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z) || !float.IsFinite(w))
        {
            return false;
        }

        color = new Vector4(x, y, z, w);
        return true;
    }

    private static bool TryColor(StarfieldFloat4Patch source, out Vector4 color)
    {
        color = default;
        if (source is not { X: { } x, Y: { } y, Z: { } z, W: { } w } ||
            !float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z) || !float.IsFinite(w))
        {
            return false;
        }

        color = new Vector4(x, y, z, w);
        return true;
    }
}
