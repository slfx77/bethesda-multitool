using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.Reflection;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing.Reflection;

/// <summary>
///     Strict entry point from a Starfield WTHS BETH stream to the bounded public weather patch.
///     The reflection reader enforces OBJT-versus-DIFF framing before semantic projection begins.
/// </summary>
internal static class StarfieldWeatherSettingsDecoder
{
    private const string RootType = "BGSWeatherSettingsForm";

    internal static bool TryDecode(
        ReadOnlySpan<byte> data,
        StarfieldWeatherSettingsPayloadKind payloadKind,
        out StarfieldWeatherSettingsPatch? patch,
        out string? error)
    {
        patch = null;
        error = null;
        if (payloadKind is not StarfieldWeatherSettingsPayloadKind.FullObject and
            not StarfieldWeatherSettingsPayloadKind.Diff)
        {
            error = $"Unsupported WTHS reflection payload kind '{payloadKind}'.";
            return false;
        }

        var expectDiff = payloadKind == StarfieldWeatherSettingsPayloadKind.Diff;
        if (!BethesdaReflectionReader.TryReadObject(
                data, expectDiff, RootType, out var reflected, out error))
        {
            return false;
        }

        return StarfieldWeatherSettingsProjector.TryProject(
            reflected!, payloadKind, out patch, out error);
    }
}

/// <summary>
///     Projects the proven subset of <c>BGSWeatherSettingsForm</c>. Unsupported semantic classes
///     (precipitation, sounds, spells, foliage, keywords, and unverified list element layouts) remain
///     structurally validated by <see cref="BethesdaReflectionReader" /> but are intentionally not
///     guessed here.
/// </summary>
internal static class StarfieldWeatherSettingsProjector
{
    private const string RootType = "BGSWeatherSettingsForm";
    private const string WeatherChoiceType = "BGSWeatherSettingsForm::WeatherChoiceSettings";
    private const string ColorSettingsType = "BGSWeatherSettingsForm::ColorSettings";
    private const string BlendableColorType = "BSBlendable::ColorValue";
    private const string BlendableFloatType = "BSBlendable::FloatValue";
    private const string Float4Type = "XMFLOAT4";

    internal static bool TryProject(
        BethesdaReflectionObject reflected,
        StarfieldWeatherSettingsPayloadKind payloadKind,
        out StarfieldWeatherSettingsPatch? patch,
        out string? error)
    {
        patch = null;
        error = null;
        if (!string.Equals(reflected.TypeName, RootType, StringComparison.Ordinal))
        {
            error = $"WTHS reflection root '{reflected.TypeName}' is not '{RootType}'.";
            return false;
        }

        if (payloadKind is not StarfieldWeatherSettingsPayloadKind.FullObject and
            not StarfieldWeatherSettingsPayloadKind.Diff)
        {
            error = $"Unsupported WTHS reflection payload kind '{payloadKind}'.";
            return false;
        }

        var required = payloadKind == StarfieldWeatherSettingsPayloadKind.FullObject;
        if (!TryReadReference(reflected, "pParent", required, out var parent, out error) ||
            !TryReadReference(
                reflected, "pDisplayNameKeyword", required, out var displayNameKeyword, out error) ||
            !TryReadWeatherChoice(reflected, required, out var weatherChoice, out error) ||
            !TryReadReference(reflected, "pImageSpace", required, out var imageSpace, out error) ||
            !TryReadReference(
                reflected, "pImageSpaceNight", required, out var imageSpaceNight, out error) ||
            !TryReadReference(
                reflected, "pVolumeticLighting", required, out var volumetricLighting, out error) ||
            !TryReadReference(reflected, "pClouds", required, out var clouds, out error) ||
            !TryReadColors(reflected, required, out var colors, out error) ||
            !TryReadReference(
                reflected, "pPrecipitationEffect", required, out var precipitationEffect, out error) ||
            !TryReadReference(
                reflected, "pOptionalPhotoModeEffect", required, out var photoModeEffect, out error) ||
            !TryReadReference(reflected, "pLensFlare", required, out var lensFlare, out error) ||
            !TryReadFloat(
                reflected, "LensFlareCloudOcclusionStrength", required,
                out var lensFlareCloudOcclusion, out error) ||
            !TryReadReference(reflected, "pWindForce", required, out var windForce, out error) ||
            !TryReadBlendableFloat(
                reflected, "WindDirectionRange", required, out var windDirectionRange, out error) ||
            !TryReadBlendableFloat(
                reflected, "WindTurbulence", required, out var windTurbulence, out error) ||
            !TryReadBool(
                reflected, "WindDirectionOverrideEnabled", required,
                out var windDirectionOverrideEnabled, out error) ||
            !TryReadBlendableFloat(
                reflected, "WindDirectionOverrideValue", required,
                out var windDirectionOverrideValue, out error) ||
            !TryReadFloat(reflected, "TransDelta", required, out var transDelta, out error) ||
            !TryReadBlendableFloat(
                reflected, "VolatilityMultiplier", required, out var volatilityMultiplier, out error) ||
            !TryReadBlendableFloat(
                reflected, "VisibilityMultiplier", required, out var visibilityMultiplier, out error))
        {
            return false;
        }

        patch = new StarfieldWeatherSettingsPatch
        {
            ParentFormId = parent,
            DisplayNameKeywordFormId = displayNameKeyword,
            WeatherChoice = weatherChoice,
            ImageSpaceFormId = imageSpace,
            ImageSpaceNightFormId = imageSpaceNight,
            VolumetricLightingFormId = volumetricLighting,
            CloudsFormId = clouds,
            Colors = colors,
            PrecipitationEffectFormId = precipitationEffect,
            OptionalPhotoModeEffectFormId = photoModeEffect,
            LensFlareFormId = lensFlare,
            LensFlareCloudOcclusionStrength = lensFlareCloudOcclusion,
            WindForceFormId = windForce,
            WindDirectionRange = windDirectionRange,
            WindTurbulence = windTurbulence,
            WindDirectionOverrideEnabled = windDirectionOverrideEnabled,
            WindDirectionOverrideValue = windDirectionOverrideValue,
            TransDelta = transDelta,
            VolatilityMultiplier = volatilityMultiplier,
            VisibilityMultiplier = visibilityMultiplier
        };
        return true;
    }

    private static bool TryReadWeatherChoice(
        BethesdaReflectionObject parent,
        bool required,
        out StarfieldWeatherChoicePatch? patch,
        out string? error)
    {
        patch = null;
        if (!TryReadObject(parent, "WeatherChoice", WeatherChoiceType, required, out var value, out error) ||
            value is null)
        {
            return value is null && error is null;
        }

        if (!TryReadUnsigned(value, "Weight", required, out var weight, out error))
        {
            return false;
        }

        // SubWeathers is intentionally not projected until its retail LIST element type is proven.
        patch = new StarfieldWeatherChoicePatch { Weight = weight };
        return true;
    }

    private static bool TryReadColors(
        BethesdaReflectionObject parent,
        bool required,
        out StarfieldWeatherColorSettingsPatch? patch,
        out string? error)
    {
        patch = null;
        if (!TryReadObject(parent, "Colors", ColorSettingsType, required, out var value, out error) ||
            value is null)
        {
            return value is null && error is null;
        }

        if (!TryReadBlendableColor(value, "EffectLighting", required, out var effectLighting, out error) ||
            !TryReadBlendableColor(value, "FogFar", required, out var fogFar, out error) ||
            !TryReadBlendableColor(value, "FogFarHigh", required, out var fogFarHigh, out error) ||
            !TryReadBlendableColor(value, "FogNear", required, out var fogNear, out error) ||
            !TryReadBlendableColor(value, "FogNearHigh", required, out var fogNearHigh, out error) ||
            !TryReadBlendableColor(value, "Sun", required, out var sun, out error) ||
            !TryReadBlendableColor(value, "SunGlare", required, out var sunGlare, out error) ||
            !TryReadBlendableColor(value, "Sunlight", required, out var sunlight, out error) ||
            !TryReadBlendableColor(value, "MoonGlare", required, out var moonGlare, out error) ||
            !TryReadBlendableColor(value, "Moonlight", required, out var moonlight, out error))
        {
            return false;
        }

        patch = new StarfieldWeatherColorSettingsPatch
        {
            EffectLighting = effectLighting,
            FogFar = fogFar,
            FogFarHigh = fogFarHigh,
            FogNear = fogNear,
            FogNearHigh = fogNearHigh,
            Sun = sun,
            SunGlare = sunGlare,
            Sunlight = sunlight,
            MoonGlare = moonGlare,
            Moonlight = moonlight
        };
        return true;
    }

    private static bool TryReadBlendableColor(
        BethesdaReflectionObject parent,
        string fieldName,
        bool required,
        out StarfieldBlendableColorPatch? patch,
        out string? error)
    {
        patch = null;
        if (!TryReadObject(parent, fieldName, BlendableColorType, required, out var value, out error) ||
            value is null)
        {
            return value is null && error is null;
        }

        if (!TryReadString(value, "Op", required, out var operation, out error) ||
            !TryReadFloat4(value, "Value", required, out var color, out error) ||
            !TryReadFloat(value, "BlendAmount", required, out var blendAmount, out error))
        {
            return false;
        }

        patch = new StarfieldBlendableColorPatch
        {
            Operation = operation,
            Value = color,
            BlendAmount = blendAmount
        };
        return true;
    }

    private static bool TryReadFloat4(
        BethesdaReflectionObject parent,
        string fieldName,
        bool required,
        out StarfieldFloat4Patch? patch,
        out string? error)
    {
        patch = null;
        if (!TryReadObject(parent, fieldName, Float4Type, required, out var value, out error) ||
            value is null)
        {
            return value is null && error is null;
        }

        if (!TryReadFloat(value, "x", required, out var x, out error) ||
            !TryReadFloat(value, "y", required, out var y, out error) ||
            !TryReadFloat(value, "z", required, out var z, out error) ||
            !TryReadFloat(value, "w", required, out var w, out error))
        {
            return false;
        }

        patch = new StarfieldFloat4Patch { X = x, Y = y, Z = z, W = w };
        return true;
    }

    private static bool TryReadBlendableFloat(
        BethesdaReflectionObject parent,
        string fieldName,
        bool required,
        out StarfieldBlendableFloatPatch? patch,
        out string? error)
    {
        patch = null;
        if (!TryReadObject(parent, fieldName, BlendableFloatType, required, out var value, out error) ||
            value is null)
        {
            return value is null && error is null;
        }

        if (!TryReadString(value, "Op", required, out var operation, out error) ||
            !TryReadFloat(value, "Value", required, out var scalar, out error) ||
            !TryReadFloat(value, "BlendAmount", required, out var blendAmount, out error))
        {
            return false;
        }

        patch = new StarfieldBlendableFloatPatch
        {
            Operation = operation,
            Value = scalar,
            BlendAmount = blendAmount
        };
        return true;
    }

    private static bool TryReadReference(
        BethesdaReflectionObject parent,
        string fieldName,
        bool required,
        out uint? value,
        out string? error)
    {
        value = null;
        if (!TryGetField(parent, fieldName, required, out var field, out error) || field is null)
        {
            return field is null && error is null;
        }

        if (field is not BethesdaReflectionReferenceValue
            {
                ValueType: "UInt32",
                Value: BethesdaReflectionUnsignedValue unsigned
            } || unsigned.Value > uint.MaxValue)
        {
            error = $"Reflected field '{parent.TypeName}.{fieldName}' is not Ref<UInt32>.";
            return false;
        }

        value = (uint)unsigned.Value;
        return true;
    }

    private static bool TryReadUnsigned(
        BethesdaReflectionObject parent,
        string fieldName,
        bool required,
        out uint? value,
        out string? error)
    {
        value = null;
        if (!TryGetField(parent, fieldName, required, out var field, out error) || field is null)
        {
            return field is null && error is null;
        }

        if (field is not BethesdaReflectionUnsignedValue unsigned || unsigned.Value > uint.MaxValue)
        {
            error = $"Reflected field '{parent.TypeName}.{fieldName}' is not UInt32.";
            return false;
        }

        value = (uint)unsigned.Value;
        return true;
    }

    private static bool TryReadFloat(
        BethesdaReflectionObject parent,
        string fieldName,
        bool required,
        out float? value,
        out string? error)
    {
        value = null;
        if (!TryGetField(parent, fieldName, required, out var field, out error) || field is null)
        {
            return field is null && error is null;
        }

        if (field is not BethesdaReflectionFloatValue reflected ||
            !double.IsFinite(reflected.Value) ||
            reflected.Value is < -float.MaxValue or > float.MaxValue)
        {
            error = $"Reflected field '{parent.TypeName}.{fieldName}' is not a finite Float.";
            return false;
        }

        value = (float)reflected.Value;
        return true;
    }

    private static bool TryReadBool(
        BethesdaReflectionObject parent,
        string fieldName,
        bool required,
        out bool? value,
        out string? error)
    {
        value = null;
        if (!TryGetField(parent, fieldName, required, out var field, out error) || field is null)
        {
            return field is null && error is null;
        }

        if (field is not BethesdaReflectionBoolValue reflected)
        {
            error = $"Reflected field '{parent.TypeName}.{fieldName}' is not Bool.";
            return false;
        }

        value = reflected.Value;
        return true;
    }

    private static bool TryReadString(
        BethesdaReflectionObject parent,
        string fieldName,
        bool required,
        out string? value,
        out string? error)
    {
        value = null;
        if (!TryGetField(parent, fieldName, required, out var field, out error) || field is null)
        {
            return field is null && error is null;
        }

        if (field is not BethesdaReflectionStringValue reflected)
        {
            error = $"Reflected field '{parent.TypeName}.{fieldName}' is not String.";
            return false;
        }

        value = reflected.Value;
        return true;
    }

    private static bool TryReadObject(
        BethesdaReflectionObject parent,
        string fieldName,
        string expectedType,
        bool required,
        out BethesdaReflectionObject? value,
        out string? error)
    {
        value = null;
        if (!TryGetField(parent, fieldName, required, out var field, out error) || field is null)
        {
            return field is null && error is null;
        }

        if (field is not BethesdaReflectionObjectValue reflected)
        {
            error = $"Reflected field '{parent.TypeName}.{fieldName}' is not an object of type '{expectedType}'.";
            return false;
        }

        if (!string.Equals(reflected.Value.TypeName, expectedType, StringComparison.Ordinal))
        {
            error = $"Reflected field '{parent.TypeName}.{fieldName}' has object type " +
                    $"'{reflected.Value.TypeName}', expected '{expectedType}'.";
            return false;
        }

        value = reflected.Value;
        return true;
    }

    private static bool TryGetField(
        BethesdaReflectionObject parent,
        string fieldName,
        bool required,
        out BethesdaReflectionValue? value,
        out string? error)
    {
        error = null;
        if (parent.Fields.TryGetValue(fieldName, out value))
        {
            return true;
        }

        value = null;
        if (!required)
        {
            return true;
        }

        error = $"Full reflected object '{parent.TypeName}' is missing field '{fieldName}'.";
        return false;
    }
}
