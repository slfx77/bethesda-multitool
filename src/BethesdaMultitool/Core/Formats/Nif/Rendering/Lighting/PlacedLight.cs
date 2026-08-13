using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Scene;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Lighting;

/// <summary>
///     A placed LIGH emitter baked from a REFR independently of its optional renderable model.
///     Position/direction are absolute world-space values; the per-frame GPU upload subtracts the
///     active render origin so they share the terrain/reference shaders' camera-relative space.
/// </summary>
internal readonly record struct PlacedLight(
    uint FormId,
    uint BaseFormId,
    Vector3 Position,
    float Radius,
    Vector3 Color,
    float FalloffExponent,
    float FieldOfView,
    float Intensity,
    uint Flags,
    bool IsInitiallyDisabled)
{
    internal const uint NegativeFlag = 0x0000_0004;
    internal const uint OffByDefaultFlag = 0x0000_0020;
    internal const uint SpotLightFlag = 0x0000_0200;

    internal bool HasSpotFlag => (Flags & SpotLightFlag) != 0;

    /// <summary>
    ///     Builds an emitter even when <paramref name="placement" /> has no ModelPath. The optional
    ///     model is handled separately by <see cref="RenderableReference.TryBuild" />.
    /// </summary>
    internal static PlacedLight? TryBuild(
        PlacedReference placement,
        LightRecord light,
        bool xespDisabled = false,
        BethesdaGame game = BethesdaGame.Unknown)
    {
        if (placement.RecordType is "ACHR" or "ACRE" ||
            !float.IsFinite(placement.X) || !float.IsFinite(placement.Y) || !float.IsFinite(placement.Z))
        {
            return null;
        }

        var radius = ResolveEffectiveRadius(placement, light, game);
        if (!float.IsFinite(radius) || radius <= 0f)
        {
            return null;
        }

        var r = (byte)(light.Color & 0xff);
        var g = (byte)((light.Color >> 8) & 0xff);
        var b = (byte)((light.Color >> 16) & 0xff);
        var color = new Vector3(r / 255f, g / 255f, b / 255f);

        // Retain the record fields for diagnostics/future engine-family work, but do not interpret
        // them as a cone. FNV TESObjectLIGH::GenDynamic constructs NiPointLight and never reads DATA
        // FalloffExponent/FOV; no NiSpotLight axis or angular equation was recovered from that path.
        var falloffExponent = float.IsFinite(light.FalloffExponent) ? light.FalloffExponent : 0f;
        var fieldOfView = float.IsFinite(light.Fov) ? light.Fov : 0f;

        var fade = light.Fade is { } authoredFade && float.IsFinite(authoredFade)
            ? MathF.Max(authoredFade, 0f)
            : 1f;
        var intensity = (light.Flags & NegativeFlag) != 0 ? -fade : fade;
        var initiallyDisabled = placement.IsInitiallyDisabled || xespDisabled ||
                                (light.Flags & OffByDefaultFlag) != 0;

        return new PlacedLight(
            placement.FormId,
            light.FormId,
            new Vector3(placement.X, placement.Y, placement.Z),
            radius,
            color,
            falloffExponent,
            fieldOfView,
            intensity,
            light.Flags,
            initiallyDisabled);
    }

    /// <summary>
    ///     Resolves the runtime point-light radius. FNV <c>TESObjectLIGH::GenDynamic</c>
    ///     (Xbox VA <c>0x8234C288</c>) copies <c>TESObjectREFR::GetRadius()</c> into all three
    ///     components of <c>NiLight::m_kSpec</c>. For a LIGH reference, <c>GetRadius</c>
    ///     (VA <c>0x8239B698</c>) returns the base LIGH DATA radius plus ExtraRadius/XRDS and does
    ///     not consume XSCL. Other games retain the viewer's established scale-based behavior until
    ///     their own runtime paths are recovered.
    /// </summary>
    internal static float ResolveEffectiveRadius(
        PlacedReference placement,
        LightRecord light,
        BethesdaGame game)
    {
        // FO3 parity 2026-08-10: FO3's TESObjectREFR::GetRadius (0x0075DB50) implements the same
        // law — base LIGH radius + ExtraRadius, no scale multiply (fo3-decompile-spotchecks.md).
        if (game is BethesdaGame.FalloutNewVegas or BethesdaGame.Fallout3)
        {
            var extraRadius = placement.Radius ?? 0f;
            return float.IsFinite(extraRadius)
                ? light.Radius + extraRadius
                : float.NaN;
        }

        var placementScale = float.IsFinite(placement.Scale) && placement.Scale > 0f
            ? placement.Scale
            : 1f;
        return light.Radius * placementScale;
    }

    /// <summary>
    ///     Exact FO3/FNV SLS2128 radial term recovered from the shipped PC shader:
    ///     <c>1 - dot((lightPos-worldPos)/radius, ...)</c>, i.e. <c>1-(d/r)^2</c>. The original
    ///     draw/light selection keeps pixels inside the light volume; the global forward loop uses
    ///     the equivalent saturate so pixels outside that volume contribute zero instead of negative
    ///     light. The LIGH FalloffExponent/FOV fields are not consumed by FNV's recovered dynamic
    ///     LIGH creation path and therefore do not alter this curve.
    /// </summary>
    internal static float RadialAttenuation(float distance, float radius)
    {
        if (!float.IsFinite(distance) || !float.IsFinite(radius) || radius <= 0f)
        {
            return 0f;
        }

        var ratio = distance / radius;
        return Math.Clamp(1f - ratio * ratio, 0f, 1f);
    }

}
