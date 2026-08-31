namespace BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

/// <summary>Why a merged Starfield WTHS record did or did not resolve.</summary>
internal enum StarfieldWeatherSettingsResolutionStatus
{
    Resolved,
    TargetNotFound,
    DecodeFailure,
    UnknownPayloadKind,
    MissingPatch,
    MissingParent,
    ParentContractViolation,
    InheritanceCycle,
    DepthLimitExceeded
}

/// <summary>
///     Pure, fail-closed result of applying a Starfield WTHS inheritance chain. The chain is ordered
///     from the rootward-most record reached to the requested record. <see cref="EffectivePatch" />
///     is populated only when every record in the chain resolves successfully.
/// </summary>
internal sealed record StarfieldWeatherSettingsResolution(
    StarfieldWeatherSettingsResolutionStatus Status,
    uint TargetFormId,
    StarfieldWeatherSettingsPatch? EffectivePatch,
    IReadOnlyList<uint> InheritanceChain,
    uint? FailureFormId = null,
    string? FailureDetail = null)
{
    internal bool IsResolved =>
        Status == StarfieldWeatherSettingsResolutionStatus.Resolved && EffectivePatch is not null;
}

/// <summary>
///     Resolves already load-order-merged Starfield WTHS records. Nullable leaves are overlaid
///     recursively, so an absent DIFF member inherits while an authored zero, false, or empty string
///     replaces its parent value.
/// </summary>
internal static class StarfieldWeatherSettingsResolver
{
    internal const int DefaultMaxDepth = 64;

    internal static StarfieldWeatherSettingsResolution Resolve(
        uint targetFormId,
        IReadOnlyDictionary<uint, StarfieldWeatherSettingsRecord> mergedRecords,
        int maxDepth = DefaultMaxDepth)
    {
        ArgumentNullException.ThrowIfNull(mergedRecords);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDepth, 1);

        var traversal = new List<uint>();
        var patches = new List<StarfieldWeatherSettingsPatch>();
        var visited = new HashSet<uint>();
        var currentFormId = targetFormId;

        while (true)
        {
            if (visited.Contains(currentFormId))
            {
                return Fail(
                    StarfieldWeatherSettingsResolutionStatus.InheritanceCycle,
                    currentFormId,
                    $"WTHS inheritance revisits {currentFormId:X8}.");
            }

            if (traversal.Count >= maxDepth)
            {
                return Fail(
                    StarfieldWeatherSettingsResolutionStatus.DepthLimitExceeded,
                    currentFormId,
                    $"WTHS inheritance exceeds the {maxDepth}-record depth cap.");
            }

            if (!mergedRecords.TryGetValue(currentFormId, out var record))
            {
                var status = traversal.Count == 0
                    ? StarfieldWeatherSettingsResolutionStatus.TargetNotFound
                    : StarfieldWeatherSettingsResolutionStatus.MissingParent;
                return Fail(status, currentFormId, $"WTHS {currentFormId:X8} is absent from the merged index.");
            }

            visited.Add(currentFormId);
            traversal.Add(currentFormId);

            if (record.DecodeFailure is not null)
            {
                return Fail(
                    StarfieldWeatherSettingsResolutionStatus.DecodeFailure,
                    currentFormId,
                    record.DecodeFailure);
            }

            if (record.PayloadKind is not StarfieldWeatherSettingsPayloadKind.FullObject and
                not StarfieldWeatherSettingsPayloadKind.Diff)
            {
                return Fail(
                    StarfieldWeatherSettingsResolutionStatus.UnknownPayloadKind,
                    currentFormId,
                    $"WTHS {currentFormId:X8} has no established reflection payload kind.");
            }

            if (record.Patch is null)
            {
                return Fail(
                    StarfieldWeatherSettingsResolutionStatus.MissingPatch,
                    currentFormId,
                    $"WTHS {currentFormId:X8} has no decoded patch.");
            }

            patches.Add(record.Patch);

            if (record.IsFullDefinition)
            {
                if (record.ParentFormId is not null || record.Patch.ParentFormId != 0)
                {
                    return Fail(
                        StarfieldWeatherSettingsResolutionStatus.ParentContractViolation,
                        currentFormId,
                        $"Full WTHS {currentFormId:X8} must omit RFDP and author reflected pParent=0.");
                }

                var effective = patches[^1];
                for (var i = patches.Count - 2; i >= 0; i--)
                {
                    effective = Merge(effective, patches[i]);
                }

                return new StarfieldWeatherSettingsResolution(
                    StarfieldWeatherSettingsResolutionStatus.Resolved,
                    targetFormId,
                    effective,
                    RootwardChain(traversal));
            }

            if (record.ParentFormId is not { } parentFormId || parentFormId == 0)
            {
                return Fail(
                    StarfieldWeatherSettingsResolutionStatus.MissingParent,
                    currentFormId,
                    $"Diff WTHS {currentFormId:X8} has no nonzero parent.");
            }

            if (record.Patch.ParentFormId != parentFormId)
            {
                return Fail(
                    StarfieldWeatherSettingsResolutionStatus.ParentContractViolation,
                    currentFormId,
                    $"Diff WTHS {currentFormId:X8} RFDP/reflected pParent values do not match.");
            }

            currentFormId = parentFormId;
        }

        StarfieldWeatherSettingsResolution Fail(
            StarfieldWeatherSettingsResolutionStatus status,
            uint failureFormId,
            string detail)
        {
            return new StarfieldWeatherSettingsResolution(
                status,
                targetFormId,
                null,
                RootwardChain(traversal),
                failureFormId,
                detail);
        }
    }

    private static IReadOnlyList<uint> RootwardChain(IReadOnlyList<uint> targetFirstTraversal)
    {
        var chain = targetFirstTraversal.Reverse().ToArray();
        return Array.AsReadOnly(chain);
    }

    private static StarfieldWeatherSettingsPatch Merge(
        StarfieldWeatherSettingsPatch inherited,
        StarfieldWeatherSettingsPatch overlay)
    {
        return new StarfieldWeatherSettingsPatch
        {
            ParentFormId = overlay.ParentFormId ?? inherited.ParentFormId,
            DisplayNameKeywordFormId = overlay.DisplayNameKeywordFormId ?? inherited.DisplayNameKeywordFormId,
            WeatherChoice = MergeNested(inherited.WeatherChoice, overlay.WeatherChoice, Merge),
            ImageSpaceFormId = overlay.ImageSpaceFormId ?? inherited.ImageSpaceFormId,
            ImageSpaceNightFormId = overlay.ImageSpaceNightFormId ?? inherited.ImageSpaceNightFormId,
            VolumetricLightingFormId = overlay.VolumetricLightingFormId ?? inherited.VolumetricLightingFormId,
            CloudsFormId = overlay.CloudsFormId ?? inherited.CloudsFormId,
            Colors = MergeNested(inherited.Colors, overlay.Colors, Merge),
            PrecipitationEffectFormId = overlay.PrecipitationEffectFormId ?? inherited.PrecipitationEffectFormId,
            OptionalPhotoModeEffectFormId =
                overlay.OptionalPhotoModeEffectFormId ?? inherited.OptionalPhotoModeEffectFormId,
            LensFlareFormId = overlay.LensFlareFormId ?? inherited.LensFlareFormId,
            LensFlareCloudOcclusionStrength =
                overlay.LensFlareCloudOcclusionStrength ?? inherited.LensFlareCloudOcclusionStrength,
            WindForceFormId = overlay.WindForceFormId ?? inherited.WindForceFormId,
            WindDirectionRange = MergeNested(inherited.WindDirectionRange, overlay.WindDirectionRange, Merge),
            WindTurbulence = MergeNested(inherited.WindTurbulence, overlay.WindTurbulence, Merge),
            WindDirectionOverrideEnabled =
                overlay.WindDirectionOverrideEnabled ?? inherited.WindDirectionOverrideEnabled,
            WindDirectionOverrideValue =
                MergeNested(inherited.WindDirectionOverrideValue, overlay.WindDirectionOverrideValue, Merge),
            TransDelta = overlay.TransDelta ?? inherited.TransDelta,
            VolatilityMultiplier =
                MergeNested(inherited.VolatilityMultiplier, overlay.VolatilityMultiplier, Merge),
            VisibilityMultiplier =
                MergeNested(inherited.VisibilityMultiplier, overlay.VisibilityMultiplier, Merge)
        };
    }

    private static StarfieldWeatherChoicePatch Merge(
        StarfieldWeatherChoicePatch inherited,
        StarfieldWeatherChoicePatch overlay)
    {
        return new StarfieldWeatherChoicePatch
        {
            Weight = overlay.Weight ?? inherited.Weight
        };
    }

    private static StarfieldWeatherColorSettingsPatch Merge(
        StarfieldWeatherColorSettingsPatch inherited,
        StarfieldWeatherColorSettingsPatch overlay)
    {
        return new StarfieldWeatherColorSettingsPatch
        {
            EffectLighting = MergeNested(inherited.EffectLighting, overlay.EffectLighting, Merge),
            FogFar = MergeNested(inherited.FogFar, overlay.FogFar, Merge),
            FogFarHigh = MergeNested(inherited.FogFarHigh, overlay.FogFarHigh, Merge),
            FogNear = MergeNested(inherited.FogNear, overlay.FogNear, Merge),
            FogNearHigh = MergeNested(inherited.FogNearHigh, overlay.FogNearHigh, Merge),
            Sun = MergeNested(inherited.Sun, overlay.Sun, Merge),
            SunGlare = MergeNested(inherited.SunGlare, overlay.SunGlare, Merge),
            Sunlight = MergeNested(inherited.Sunlight, overlay.Sunlight, Merge),
            MoonGlare = MergeNested(inherited.MoonGlare, overlay.MoonGlare, Merge),
            Moonlight = MergeNested(inherited.Moonlight, overlay.Moonlight, Merge)
        };
    }

    private static StarfieldBlendableColorPatch Merge(
        StarfieldBlendableColorPatch inherited,
        StarfieldBlendableColorPatch overlay)
    {
        return new StarfieldBlendableColorPatch
        {
            Operation = overlay.Operation ?? inherited.Operation,
            Value = MergeNested(inherited.Value, overlay.Value, Merge),
            BlendAmount = overlay.BlendAmount ?? inherited.BlendAmount
        };
    }

    private static StarfieldFloat4Patch Merge(
        StarfieldFloat4Patch inherited,
        StarfieldFloat4Patch overlay)
    {
        return new StarfieldFloat4Patch
        {
            X = overlay.X ?? inherited.X,
            Y = overlay.Y ?? inherited.Y,
            Z = overlay.Z ?? inherited.Z,
            W = overlay.W ?? inherited.W
        };
    }

    private static StarfieldBlendableFloatPatch Merge(
        StarfieldBlendableFloatPatch inherited,
        StarfieldBlendableFloatPatch overlay)
    {
        return new StarfieldBlendableFloatPatch
        {
            Operation = overlay.Operation ?? inherited.Operation,
            Value = overlay.Value ?? inherited.Value,
            BlendAmount = overlay.BlendAmount ?? inherited.BlendAmount
        };
    }

    private static T? MergeNested<T>(T? inherited, T? overlay, Func<T, T, T> merge)
        where T : class
    {
        if (overlay is null)
        {
            return inherited;
        }

        return inherited is null ? overlay : merge(inherited, overlay);
    }
}
