using System.CommandLine;

namespace FalloutXbox360Utils.CLI.Rendering.Npc;

internal static class NpcExportCommandSupport
{
    internal static bool TryCreateSettings(
        ParseResult parseResult,
        Argument<string> meshesBsaArgument,
        Option<string> esmOption,
        Option<string[]?> texturesBsaOption,
        Option<string> outputOption,
        Option<string[]?> npcOption,
        Option<bool> verboseOption,
        Option<string?> dmpOption,
        Option<bool> headOnlyOption,
        Option<bool> noEquipOption,
        Option<bool> noEgmOption,
        Option<bool> noEgtOption,
        Option<bool> bindPoseOption,
        Option<string?> animOption,
        Option<bool> weaponOption,
        Option<bool>? noWeaponOption,
        Option<int>? rasterSizeOption,
        Option<bool>? exportEgtOption,
        Option<bool>? noBilinearOption,
        Option<bool>? noBumpOption,
        Option<bool>? noTexOption,
        Option<float?>? bumpStrengthOption,
        Option<bool>? gpuOption,
        Option<bool>? cpuOption,
        Option<bool>? skeletonOption,
        Option<bool>? wireframeOption,
        Option<bool>? isoOption,
        Option<float>? elevationOption,
        Option<bool>? sideOption,
        Option<bool>? trimetricOption,
        out NpcExportSettings? settings,
        out string? error)
    {
        return TryCreateSettings(
            parseResult,
            meshesBsaArgument,
            null,
            esmOption,
            texturesBsaOption,
            outputOption,
            npcOption,
            null,
            verboseOption,
            dmpOption,
            headOnlyOption,
            noEquipOption,
            noEgmOption,
            noEgtOption,
            bindPoseOption,
            animOption,
            weaponOption,
            noWeaponOption,
            rasterSizeOption,
            exportEgtOption,
            noBilinearOption,
            noBumpOption,
            noTexOption,
            bumpStrengthOption,
            gpuOption,
            cpuOption,
            skeletonOption,
            wireframeOption,
            isoOption,
            elevationOption,
            sideOption,
            trimetricOption,
            out settings,
            out error);
    }

    internal static bool TryCreateSettings(
        ParseResult parseResult,
        Argument<string> meshesBsaArgument,
        Option<string[]?>? extraMeshesBsaOption,
        Option<string> esmOption,
        Option<string[]?> texturesBsaOption,
        Option<string> outputOption,
        Option<string[]?> npcOption,
        Option<string?>? npcFileOption,
        Option<bool> verboseOption,
        Option<string?> dmpOption,
        Option<bool> headOnlyOption,
        Option<bool> noEquipOption,
        Option<bool> noEgmOption,
        Option<bool> noEgtOption,
        Option<bool> bindPoseOption,
        Option<string?> animOption,
        Option<bool> weaponOption,
        Option<bool>? noWeaponOption,
        Option<int>? rasterSizeOption,
        Option<bool>? exportEgtOption,
        Option<bool>? noBilinearOption,
        Option<bool>? noBumpOption,
        Option<bool>? noTexOption,
        Option<float?>? bumpStrengthOption,
        Option<bool>? gpuOption,
        Option<bool>? cpuOption,
        Option<bool>? skeletonOption,
        Option<bool>? wireframeOption,
        Option<bool>? isoOption,
        Option<float>? elevationOption,
        Option<bool>? sideOption,
        Option<bool>? trimetricOption,
        out NpcExportSettings? settings,
        out string? error)
    {
        _ = verboseOption;

        settings = null;
        error = null;

        if (!ValidateExclusiveWeaponFlags(parseResult, weaponOption, noWeaponOption, out error) ||
            !ValidateNoRasterOnlyFlags(
                parseResult,
                rasterSizeOption,
                exportEgtOption,
                noBilinearOption,
                noBumpOption,
                noTexOption,
                bumpStrengthOption,
                gpuOption,
                cpuOption,
                skeletonOption,
                wireframeOption,
                isoOption,
                elevationOption,
                sideOption,
                trimetricOption,
                out error))
        {
            return false;
        }

        if (!TryLoadNpcFilters(
                parseResult.GetValue(npcOption),
                npcFileOption != null ? parseResult.GetValue(npcFileOption) : null,
                out var npcFilters,
                out error))
        {
            return false;
        }

        var animOverride = parseResult.GetValue(animOption);
        var useBindPose = parseResult.GetValue(bindPoseOption) || string.IsNullOrWhiteSpace(animOverride);
        settings = new NpcExportSettings
        {
            MeshesBsaPath = parseResult.GetValue(meshesBsaArgument)!,
            ExtraMeshesBsaPaths = extraMeshesBsaOption != null ? parseResult.GetValue(extraMeshesBsaOption) : null,
            EsmPath = parseResult.GetValue(esmOption)!,
            ExplicitTexturesBsaPaths = parseResult.GetValue(texturesBsaOption),
            OutputDir = parseResult.GetValue(outputOption)!,
            NpcFilters = npcFilters,
            DmpPath = parseResult.GetValue(dmpOption),
            HeadOnly = parseResult.GetValue(headOnlyOption),
            NoEquip = parseResult.GetValue(noEquipOption),
            IncludeWeapon = parseResult.GetValue(weaponOption) &&
                            !(noWeaponOption != null && parseResult.GetValue(noWeaponOption)),
            NoEgm = parseResult.GetValue(noEgmOption),
            NoEgt = parseResult.GetValue(noEgtOption),
            BindPose = useBindPose,
            AnimOverride = useBindPose ? null : animOverride
        };

        return true;
    }

    internal static bool TryLoadNpcFilters(
        string[]? inlineFilters,
        string? filterFilePath,
        out string[]? filters,
        out string? error)
    {
        filters = null;
        error = null;

        var merged = new List<string>();
        AddFilters(merged, inlineFilters);

        if (!string.IsNullOrWhiteSpace(filterFilePath))
        {
            if (!File.Exists(filterFilePath))
            {
                error = $"NPC filter file not found: {filterFilePath}";
                return false;
            }

            AddFilters(merged, File.ReadLines(filterFilePath));
        }

        if (merged.Count == 0)
        {
            return true;
        }

        filters = merged
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return true;
    }

    private static bool ValidateExclusiveWeaponFlags(
        ParseResult parseResult,
        Option<bool> weaponOption,
        Option<bool>? noWeaponOption,
        out string? error)
    {
        error = null;
        if (noWeaponOption == null)
        {
            return true;
        }

        if (parseResult.GetValue(weaponOption) && parseResult.GetValue(noWeaponOption))
        {
            error = "--weapon and --no-weapon are mutually exclusive";
            return false;
        }

        return true;
    }

    private static bool ValidateNoRasterOnlyFlags(
        ParseResult parseResult,
        Option<int>? rasterSizeOption,
        Option<bool>? exportEgtOption,
        Option<bool>? noBilinearOption,
        Option<bool>? noBumpOption,
        Option<bool>? noTexOption,
        Option<float?>? bumpStrengthOption,
        Option<bool>? gpuOption,
        Option<bool>? cpuOption,
        Option<bool>? skeletonOption,
        Option<bool>? wireframeOption,
        Option<bool>? isoOption,
        Option<float>? elevationOption,
        Option<bool>? sideOption,
        Option<bool>? trimetricOption,
        out string? error)
    {
        error = null;

        if (IsExplicit(parseResult, rasterSizeOption))
        {
            error = "--size is not supported in GLB export mode";
            return false;
        }

        if (IsExplicitTrue(parseResult, exportEgtOption))
        {
            error = "--export-egt is not supported in GLB export mode";
            return false;
        }

        if (IsExplicitTrue(parseResult, noBilinearOption))
        {
            error = "--no-bilinear is not supported in GLB export mode";
            return false;
        }

        if (IsExplicitTrue(parseResult, noBumpOption))
        {
            error = "--no-bump is not supported in GLB export mode";
            return false;
        }

        if (IsExplicitTrue(parseResult, noTexOption))
        {
            error = "--no-tex is not supported in GLB export mode";
            return false;
        }

        if (IsExplicit(parseResult, bumpStrengthOption))
        {
            error = "--bump-strength is not supported in GLB export mode";
            return false;
        }

        if (IsExplicitTrue(parseResult, gpuOption))
        {
            error = "--gpu is not supported in GLB export mode";
            return false;
        }

        if (IsExplicitTrue(parseResult, cpuOption))
        {
            error = "--cpu is not supported in GLB export mode";
            return false;
        }

        if (IsExplicitTrue(parseResult, skeletonOption))
        {
            error = "--skeleton is not supported in GLB export mode";
            return false;
        }

        if (IsExplicitTrue(parseResult, wireframeOption))
        {
            error = "--wireframe is not supported in GLB export mode";
            return false;
        }

        if (IsExplicitTrue(parseResult, isoOption))
        {
            error = "--iso is not supported in GLB export mode";
            return false;
        }

        if (IsExplicit(parseResult, elevationOption))
        {
            error = "--elevation is not supported in GLB export mode";
            return false;
        }

        if (IsExplicitTrue(parseResult, sideOption))
        {
            error = "--side is not supported in GLB export mode";
            return false;
        }

        if (IsExplicitTrue(parseResult, trimetricOption))
        {
            error = "--trimetric is not supported in GLB export mode";
            return false;
        }

        return true;
    }

    private static bool IsExplicit<T>(ParseResult parseResult, Option<T>? option)
    {
        return option != null && parseResult.GetResult(option) is { Implicit: false };
    }

    private static bool IsExplicitTrue(ParseResult parseResult, Option<bool>? option)
    {
        return option != null &&
               parseResult.GetResult(option) is { Implicit: false } &&
               parseResult.GetValue(option);
    }

    private static void AddFilters(List<string> merged, IEnumerable<string>? values)
    {
        if (values == null)
        {
            return;
        }

        foreach (var rawValue in values)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                continue;
            }

            var value = rawValue.Trim();
            if (value.StartsWith('#') || value.StartsWith(';'))
            {
                continue;
            }

            merged.Add(value);
        }
    }
}
