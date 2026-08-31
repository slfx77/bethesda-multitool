using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.Reflection;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing.Reflection;

/// <summary>
///     Strict entry point from a Starfield ATMO BETH stream to the bounded structural patch. The
///     reflection reader establishes complete OBJT-versus-indexed-DIFF framing before projection.
/// </summary>
internal static class StarfieldAtmosphereDecoder
{
    private const string RootType = "BGSAtmosphere";

    internal static bool TryDecode(
        ReadOnlySpan<byte> data,
        StarfieldAtmospherePayloadKind payloadKind,
        out StarfieldAtmospherePatch? patch,
        out string? error)
    {
        patch = null;
        error = null;
        if (payloadKind is not StarfieldAtmospherePayloadKind.FullObject and
            not StarfieldAtmospherePayloadKind.Diff)
        {
            error = $"Unsupported ATMO reflection payload kind '{payloadKind}'.";
            return false;
        }

        var expectDiff = payloadKind == StarfieldAtmospherePayloadKind.Diff;
        if (!BethesdaReflectionReader.TryReadObject(
                data, expectDiff, RootType, out var reflected, out error))
        {
            return false;
        }

        return StarfieldAtmosphereProjector.TryProject(
            reflected!, payloadKind, out patch, out error);
    }
}

/// <summary>
///     Projects only the proven ATMO inheritance, sun-preset, and climate references. It does not
///     project or infer atmospheric scattering parameters or rendering equations.
/// </summary>
/// <remarks>
///     BETH reflection schemas are self-described: the generic reader can prove that bytes obey
///     the stream's declared classes, but cannot authenticate those declarations against an
///     external Bethesda schema. This projector closes that boundary by requiring the exact known
///     class names, member paths, and Ref&lt;UInt32&gt; value shape before exposing typed data.
/// </remarks>
internal static class StarfieldAtmosphereProjector
{
    private const string RootType = "BGSAtmosphere";
    private const string SettingsType = "BGSAtmosphere::AtmosphereSettings";
    private const string OverridesType = "BGSAtmosphere::OverrideSettings";
    private const string MiscType = "BGSAtmosphere::MiscSettings";

    private static readonly string[] RootMisplacedFields =
        ["pParent", "Overrides", "Misc", "pSunPresetOverride", "pClimateOverride"];

    private static readonly string[] SettingsMisplacedFields =
        ["Settings", "pSunPresetOverride", "pClimateOverride"];

    private static readonly string[] OverridesMisplacedFields =
        ["Settings", "pParent", "Misc", "pClimateOverride"];

    private static readonly string[] MiscMisplacedFields =
        ["Settings", "pParent", "Overrides", "pSunPresetOverride"];

    internal static bool TryProject(
        BethesdaReflectionObject reflected,
        StarfieldAtmospherePayloadKind payloadKind,
        out StarfieldAtmospherePatch? patch,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(reflected);
        patch = null;
        error = null;

        if (!string.Equals(reflected.TypeName, RootType, StringComparison.Ordinal))
        {
            error = $"ATMO reflection root '{reflected.TypeName}' is not '{RootType}'.";
            return false;
        }

        if (payloadKind is not StarfieldAtmospherePayloadKind.FullObject and
            not StarfieldAtmospherePayloadKind.Diff)
        {
            error = $"Unsupported ATMO reflection payload kind '{payloadKind}'.";
            return false;
        }

        // Unprojected scattering members remain allowed. Known structural names at any of the
        // wrong projected levels do not: accepting one would silently turn a schema/path change
        // into an inherited null.
        if (!TryRejectMisplaced(reflected, RootMisplacedFields, out error))
        {
            return false;
        }

        var required = payloadKind == StarfieldAtmospherePayloadKind.FullObject;
        if (!TryReadObject(
                reflected, "Settings", SettingsType, required, out var settings, out error))
        {
            return false;
        }

        if (settings is null)
        {
            patch = new StarfieldAtmospherePatch();
            return true;
        }

        if (!TryRejectMisplaced(settings, SettingsMisplacedFields, out error) ||
            !TryReadReference(settings, "pParent", required, out var parent, out error) ||
            !TryReadObject(
                settings, "Overrides", OverridesType, required, out var overrides, out error) ||
            !TryReadObject(settings, "Misc", MiscType, required, out var misc, out error))
        {
            return false;
        }

        uint? sunPresetOverride = null;
        if (overrides is not null &&
            (!TryRejectMisplaced(overrides, OverridesMisplacedFields, out error) ||
             !TryReadReference(
                 overrides, "pSunPresetOverride", required, out sunPresetOverride, out error)))
        {
            return false;
        }

        uint? climateOverride = null;
        if (misc is not null &&
            (!TryRejectMisplaced(misc, MiscMisplacedFields, out error) ||
             !TryReadReference(
                 misc, "pClimateOverride", required, out climateOverride, out error)))
        {
            return false;
        }

        patch = new StarfieldAtmospherePatch
        {
            ParentFormId = parent,
            SunPresetOverrideFormId = sunPresetOverride,
            ClimateOverrideFormId = climateOverride
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
            error = $"ATMO field '{parent.TypeName}.{fieldName}' is not Ref<UInt32>.";
            return false;
        }

        value = (uint)unsigned.Value;
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
            error = $"ATMO field '{parent.TypeName}.{fieldName}' is not an object of exact type " +
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
            return true;
        }

        value = null;
        if (!required)
        {
            return true;
        }

        error = $"Full ATMO object '{parent.TypeName}' is missing field '{fieldName}'.";
        return false;
    }

    private static bool TryRejectMisplaced(
        BethesdaReflectionObject value,
        IReadOnlyList<string> misplacedFieldNames,
        out string? error)
    {
        foreach (var fieldName in misplacedFieldNames)
        {
            if (value.Fields.ContainsKey(fieldName))
            {
                error = $"ATMO field '{fieldName}' appears at invalid path '{value.TypeName}'; " +
                        $"expected '{ExpectedPath(fieldName)}'.";
                return false;
            }
        }

        error = null;
        return true;
    }

    private static string ExpectedPath(string fieldName)
    {
        return fieldName switch
        {
            "Settings" => "BGSAtmosphere.Settings",
            "pParent" => "Settings.pParent",
            "Overrides" => "Settings.Overrides",
            "pSunPresetOverride" => "Settings.Overrides.pSunPresetOverride",
            "Misc" => "Settings.Misc",
            "pClimateOverride" => "Settings.Misc.pClimateOverride",
            _ => fieldName
        };
    }
}
