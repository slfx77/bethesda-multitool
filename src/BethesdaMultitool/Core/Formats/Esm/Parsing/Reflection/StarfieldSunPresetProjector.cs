using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.Reflection;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing.Reflection;

/// <summary>Strict entry point from a standalone Starfield SUNP BETH stream to a typed patch.</summary>
internal static class StarfieldSunPresetDecoder
{
    internal static bool TryDecode(
        ReadOnlySpan<byte> data,
        StarfieldSunPresetPayloadKind payloadKind,
        out StarfieldSunPresetPatch? patch,
        out string? error)
    {
        patch = null;
        error = null;
        if (payloadKind is not StarfieldSunPresetPayloadKind.FullObject and
            not StarfieldSunPresetPayloadKind.Diff)
        {
            error = $"Unsupported SUNP reflection payload kind '{payloadKind}'.";
            return false;
        }

        var expectDiff = payloadKind == StarfieldSunPresetPayloadKind.Diff;
        if (!StarfieldSunPresetSchemaValidator.TryValidate(data, expectDiff, out error) ||
            !BethesdaReflectionReader.TryReadObject(
                data,
                expectDiff,
                StarfieldSunPresetSchemaValidator.RootType,
                out var reflected,
                out error))
        {
            return false;
        }

        return StarfieldSunPresetProjector.TryProject(
            reflected!, payloadKind, out patch, out error);
    }
}

/// <summary>
///     Losslessly projects only the exact source-authored sun, disk, dawn/dusk, and night fields
///     proven by the retail CLAS schema. These are data values, not a claim about CE2 interpolation,
///     photometric conversion, glare response, or any other runtime rendering equation.
/// </summary>
internal static class StarfieldSunPresetProjector
{
    private static readonly HashSet<string> RootFields =
    [
        "pParent",
        "SunColor",
        "SunIlluminance",
        "SunGlareColor",
        "SunDiskTexture",
        "SunDiskScreenSizeMin",
        "SunDiskScreenSizeMax",
        "DuskDawnPreset",
        "NightPreset"
    ];

    private static readonly HashSet<string> Float4Fields = ["x", "y", "z", "w"];

    private static readonly HashSet<string> DawnDuskFields =
        ["DirectionalColor", "TransitionStartAngle", "TransitionEndAngle"];

    private static readonly HashSet<string> NightFields =
        ["DirectionalColor", "DirectionalIlluminance", "GlareColor"];

    internal static bool TryProject(
        BethesdaReflectionObject reflected,
        StarfieldSunPresetPayloadKind payloadKind,
        out StarfieldSunPresetPatch? patch,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(reflected);
        patch = null;
        error = null;

        if (!string.Equals(
                reflected.TypeName,
                StarfieldSunPresetSchemaValidator.RootType,
                StringComparison.Ordinal))
        {
            error = $"SUNP reflection root '{reflected.TypeName}' is not " +
                    $"'{StarfieldSunPresetSchemaValidator.RootType}'.";
            return false;
        }

        if (payloadKind is not StarfieldSunPresetPayloadKind.FullObject and
            not StarfieldSunPresetPayloadKind.Diff)
        {
            error = $"Unsupported SUNP reflection payload kind '{payloadKind}'.";
            return false;
        }

        if (!TryRejectUnexpectedFields(reflected, RootFields, out error))
        {
            return false;
        }

        var required = payloadKind == StarfieldSunPresetPayloadKind.FullObject;
        if (!TryReadReference(reflected, "pParent", required, out var parent, out error) ||
            !TryReadFloat4(reflected, "SunColor", required, out var sunColor, out error) ||
            !TryReadFloat(
                reflected, "SunIlluminance", required, out var sunIlluminance, out error) ||
            !TryReadFloat4(
                reflected, "SunGlareColor", required, out var sunGlareColor, out error) ||
            !TryReadString(
                reflected, "SunDiskTexture", required, out var sunDiskTexture, out error) ||
            !TryReadFloat(
                reflected,
                "SunDiskScreenSizeMin",
                required,
                out var sunDiskScreenSizeMin,
                out error) ||
            !TryReadFloat(
                reflected,
                "SunDiskScreenSizeMax",
                required,
                out var sunDiskScreenSizeMax,
                out error) ||
            !TryReadDawnDusk(
                reflected, "DuskDawnPreset", required, out var dawnDusk, out error) ||
            !TryReadNight(reflected, "NightPreset", required, out var night, out error))
        {
            return false;
        }

        patch = new StarfieldSunPresetPatch
        {
            ParentFormId = parent,
            SunColor = sunColor,
            SunIlluminance = sunIlluminance,
            SunGlareColor = sunGlareColor,
            SunDiskTexture = sunDiskTexture,
            SunDiskScreenSizeMin = sunDiskScreenSizeMin,
            SunDiskScreenSizeMax = sunDiskScreenSizeMax,
            DuskDawnPreset = dawnDusk,
            NightPreset = night
        };
        return true;
    }

    private static bool TryReadDawnDusk(
        BethesdaReflectionObject parent,
        string fieldName,
        bool required,
        out StarfieldSunPresetDawnDuskPatch? patch,
        out string? error)
    {
        patch = null;
        if (!TryReadObject(
                parent,
                fieldName,
                StarfieldSunPresetSchemaValidator.DawnDuskType,
                required,
                out var reflected,
                out error) ||
            reflected is null)
        {
            return reflected is null && error is null;
        }

        if (!TryRejectUnexpectedFields(reflected, DawnDuskFields, out error) ||
            !TryReadFloat4(
                reflected, "DirectionalColor", required, out var directionalColor, out error) ||
            !TryReadFloat(
                reflected,
                "TransitionStartAngle",
                required,
                out var transitionStart,
                out error) ||
            !TryReadFloat(
                reflected,
                "TransitionEndAngle",
                required,
                out var transitionEnd,
                out error))
        {
            return false;
        }

        patch = new StarfieldSunPresetDawnDuskPatch
        {
            DirectionalColor = directionalColor,
            TransitionStartAngle = transitionStart,
            TransitionEndAngle = transitionEnd
        };
        return true;
    }

    private static bool TryReadNight(
        BethesdaReflectionObject parent,
        string fieldName,
        bool required,
        out StarfieldSunPresetNightPatch? patch,
        out string? error)
    {
        patch = null;
        if (!TryReadObject(
                parent,
                fieldName,
                StarfieldSunPresetSchemaValidator.NightType,
                required,
                out var reflected,
                out error) ||
            reflected is null)
        {
            return reflected is null && error is null;
        }

        if (!TryRejectUnexpectedFields(reflected, NightFields, out error) ||
            !TryReadFloat4(
                reflected, "DirectionalColor", required, out var directionalColor, out error) ||
            !TryReadFloat(
                reflected,
                "DirectionalIlluminance",
                required,
                out var directionalIlluminance,
                out error) ||
            !TryReadFloat4(reflected, "GlareColor", required, out var glareColor, out error))
        {
            return false;
        }

        patch = new StarfieldSunPresetNightPatch
        {
            DirectionalColor = directionalColor,
            DirectionalIlluminance = directionalIlluminance,
            GlareColor = glareColor
        };
        return true;
    }

    private static bool TryReadFloat4(
        BethesdaReflectionObject parent,
        string fieldName,
        bool required,
        out StarfieldSunPresetFloat4Patch? patch,
        out string? error)
    {
        patch = null;
        if (!TryReadObject(
                parent,
                fieldName,
                StarfieldSunPresetSchemaValidator.Float4Type,
                required,
                out var reflected,
                out error) ||
            reflected is null)
        {
            return reflected is null && error is null;
        }

        if (!TryRejectUnexpectedFields(reflected, Float4Fields, out error) ||
            !TryReadFloat(reflected, "x", required, out var x, out error) ||
            !TryReadFloat(reflected, "y", required, out var y, out error) ||
            !TryReadFloat(reflected, "z", required, out var z, out error) ||
            !TryReadFloat(reflected, "w", required, out var w, out error))
        {
            return false;
        }

        patch = new StarfieldSunPresetFloat4Patch { X = x, Y = y, Z = z, W = w };
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
            error = $"SUNP field '{parent.TypeName}.{fieldName}' is not Ref<UInt32>.";
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
            error = $"SUNP field '{parent.TypeName}.{fieldName}' is not a finite Float.";
            return false;
        }

        value = (float)reflected.Value;
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
            error = $"SUNP field '{parent.TypeName}.{fieldName}' is not String.";
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

        if (field is not BethesdaReflectionObjectValue reflected ||
            !string.Equals(reflected.Value.TypeName, expectedType, StringComparison.Ordinal))
        {
            error = $"SUNP field '{parent.TypeName}.{fieldName}' is not an object of exact type " +
                    $"'{expectedType}'.";
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
            if (value is not null)
            {
                return true;
            }

            error = $"SUNP field '{parent.TypeName}.{fieldName}' has a null reflected value.";
            return false;
        }

        value = null;
        if (!required)
        {
            return true;
        }

        error = $"Full SUNP object '{parent.TypeName}' is missing field '{fieldName}'.";
        return false;
    }

    private static bool TryRejectUnexpectedFields(
        BethesdaReflectionObject value,
        IReadOnlySet<string> allowedFields,
        out string? error)
    {
        foreach (var fieldName in value.Fields.Keys)
        {
            if (!allowedFields.Contains(fieldName))
            {
                error = $"SUNP object '{value.TypeName}' contains unexpected field '{fieldName}'.";
                return false;
            }
        }

        error = null;
        return true;
    }
}
