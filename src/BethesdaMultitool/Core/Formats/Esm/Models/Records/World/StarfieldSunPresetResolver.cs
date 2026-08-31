namespace BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

/// <summary>Why a merged Starfield SUNP record did or did not resolve.</summary>
internal enum StarfieldSunPresetResolutionStatus
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
///     Pure resolution result for a SUNP inheritance chain. The chain is ordered root-first and
///     the effective patch is exposed only after every source record passes its contract checks.
/// </summary>
internal sealed record StarfieldSunPresetResolution(
    StarfieldSunPresetResolutionStatus Status,
    uint TargetFormId,
    StarfieldSunPresetPatch? EffectivePatch,
    IReadOnlyList<uint> InheritanceChain,
    uint? FailureFormId = null,
    string? FailureDetail = null)
{
    internal bool IsResolved =>
        Status == StarfieldSunPresetResolutionStatus.Resolved && EffectivePatch is not null;
}

/// <summary>
///     Resolves already load-order-merged Starfield SUNP records. RFDP supplies the traversal edge
///     and every retail DIFF explicitly authors an equal reflected pParent. Nullable nested leaves
///     are overlaid recursively, preserving authored zero and empty strings. No runtime sun or
///     dawn/dusk interpolation is inferred here.
/// </summary>
internal static class StarfieldSunPresetResolver
{
    internal const int DefaultMaxDepth = 64;

    internal static StarfieldSunPresetResolution Resolve(
        uint targetFormId,
        IReadOnlyDictionary<uint, StarfieldSunPresetRecord> mergedRecords,
        int maxDepth = DefaultMaxDepth)
    {
        ArgumentNullException.ThrowIfNull(mergedRecords);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDepth, 1);

        var traversal = new List<uint>();
        var patches = new List<StarfieldSunPresetPatch>();
        var visited = new HashSet<uint>();
        var currentFormId = targetFormId;

        while (true)
        {
            if (!visited.Add(currentFormId))
            {
                return Fail(
                    StarfieldSunPresetResolutionStatus.InheritanceCycle,
                    currentFormId,
                    $"SUNP inheritance revisits {currentFormId:X8}.");
            }

            if (traversal.Count >= maxDepth)
            {
                return Fail(
                    StarfieldSunPresetResolutionStatus.DepthLimitExceeded,
                    currentFormId,
                    $"SUNP inheritance exceeds the {maxDepth}-record depth cap.");
            }

            if (!mergedRecords.TryGetValue(currentFormId, out var record))
            {
                var status = traversal.Count == 0
                    ? StarfieldSunPresetResolutionStatus.TargetNotFound
                    : StarfieldSunPresetResolutionStatus.MissingParent;
                return Fail(status, currentFormId, $"SUNP {currentFormId:X8} is absent from the merged index.");
            }

            traversal.Add(currentFormId);

            if (record.DecodeFailure is not null)
            {
                return Fail(
                    StarfieldSunPresetResolutionStatus.DecodeFailure,
                    currentFormId,
                    record.DecodeFailure);
            }

            if (record.PayloadKind is not StarfieldSunPresetPayloadKind.FullObject and
                not StarfieldSunPresetPayloadKind.Diff)
            {
                return Fail(
                    StarfieldSunPresetResolutionStatus.UnknownPayloadKind,
                    currentFormId,
                    $"SUNP {currentFormId:X8} has no established reflection payload kind.");
            }

            if (record.Patch is null)
            {
                return Fail(
                    StarfieldSunPresetResolutionStatus.MissingPatch,
                    currentFormId,
                    $"SUNP {currentFormId:X8} has no decoded patch.");
            }

            patches.Add(record.Patch);

            if (record.IsFullDefinition)
            {
                if (record.ParentFormId is not null || record.Patch.ParentFormId != 0)
                {
                    return Fail(
                        StarfieldSunPresetResolutionStatus.ParentContractViolation,
                        currentFormId,
                        $"Full SUNP {currentFormId:X8} must omit RFDP and author reflected pParent=0.");
                }

                if (!IsComplete(record.Patch))
                {
                    return Fail(
                        StarfieldSunPresetResolutionStatus.MissingPatch,
                        currentFormId,
                        $"Full SUNP {currentFormId:X8} is missing one or more required reflected fields.");
                }

                var effective = patches[^1];
                for (var index = patches.Count - 2; index >= 0; index--)
                {
                    effective = Merge(effective, patches[index]);
                }

                return new StarfieldSunPresetResolution(
                    StarfieldSunPresetResolutionStatus.Resolved,
                    targetFormId,
                    effective,
                    RootwardChain(traversal));
            }

            if (record.ParentFormId is not { } parentFormId || parentFormId == 0)
            {
                return Fail(
                    StarfieldSunPresetResolutionStatus.MissingParent,
                    currentFormId,
                    $"Diff SUNP {currentFormId:X8} has no nonzero RFDP parent.");
            }

            if (record.Patch.ParentFormId is not { } reflectedParent ||
                reflectedParent != parentFormId)
            {
                return Fail(
                    StarfieldSunPresetResolutionStatus.ParentContractViolation,
                    currentFormId,
                    $"Diff SUNP {currentFormId:X8} must author a reflected pParent equal to RFDP.");
            }

            currentFormId = parentFormId;
        }

        StarfieldSunPresetResolution Fail(
            StarfieldSunPresetResolutionStatus status,
            uint failureFormId,
            string detail)
        {
            return new StarfieldSunPresetResolution(
                status,
                targetFormId,
                null,
                RootwardChain(traversal),
                failureFormId,
                detail);
        }
    }

    private static IReadOnlyList<uint> RootwardChain(IReadOnlyList<uint> targetFirstTraversal) =>
        Array.AsReadOnly(targetFirstTraversal.Reverse().ToArray());

    private static bool IsComplete(StarfieldSunPresetPatch patch) =>
        patch.ParentFormId.HasValue &&
        IsComplete(patch.SunColor) &&
        patch.SunIlluminance.HasValue &&
        IsComplete(patch.SunGlareColor) &&
        patch.SunDiskTexture is not null &&
        patch.SunDiskScreenSizeMin.HasValue &&
        patch.SunDiskScreenSizeMax.HasValue &&
        patch.DuskDawnPreset is
        {
            TransitionStartAngle: not null,
            TransitionEndAngle: not null
        } dawnDusk &&
        IsComplete(dawnDusk.DirectionalColor) &&
        patch.NightPreset is { DirectionalIlluminance: not null } night &&
        IsComplete(night.DirectionalColor) &&
        IsComplete(night.GlareColor);

    private static bool IsComplete(StarfieldSunPresetFloat4Patch? value) =>
        value is { X: not null, Y: not null, Z: not null, W: not null };

    private static StarfieldSunPresetPatch Merge(
        StarfieldSunPresetPatch inherited,
        StarfieldSunPresetPatch overlay)
    {
        return new StarfieldSunPresetPatch
        {
            ParentFormId = overlay.ParentFormId ?? inherited.ParentFormId,
            SunColor = Merge(inherited.SunColor, overlay.SunColor),
            SunIlluminance = overlay.SunIlluminance ?? inherited.SunIlluminance,
            SunGlareColor = Merge(inherited.SunGlareColor, overlay.SunGlareColor),
            SunDiskTexture = overlay.SunDiskTexture ?? inherited.SunDiskTexture,
            SunDiskScreenSizeMin =
                overlay.SunDiskScreenSizeMin ?? inherited.SunDiskScreenSizeMin,
            SunDiskScreenSizeMax =
                overlay.SunDiskScreenSizeMax ?? inherited.SunDiskScreenSizeMax,
            DuskDawnPreset = Merge(inherited.DuskDawnPreset, overlay.DuskDawnPreset),
            NightPreset = Merge(inherited.NightPreset, overlay.NightPreset)
        };
    }

    private static StarfieldSunPresetFloat4Patch? Merge(
        StarfieldSunPresetFloat4Patch? inherited,
        StarfieldSunPresetFloat4Patch? overlay)
    {
        if (overlay is null) return inherited;
        if (inherited is null) return overlay;
        return new StarfieldSunPresetFloat4Patch
        {
            X = overlay.X ?? inherited.X,
            Y = overlay.Y ?? inherited.Y,
            Z = overlay.Z ?? inherited.Z,
            W = overlay.W ?? inherited.W
        };
    }

    private static StarfieldSunPresetDawnDuskPatch? Merge(
        StarfieldSunPresetDawnDuskPatch? inherited,
        StarfieldSunPresetDawnDuskPatch? overlay)
    {
        if (overlay is null) return inherited;
        if (inherited is null) return overlay;
        return new StarfieldSunPresetDawnDuskPatch
        {
            DirectionalColor = Merge(inherited.DirectionalColor, overlay.DirectionalColor),
            TransitionStartAngle =
                overlay.TransitionStartAngle ?? inherited.TransitionStartAngle,
            TransitionEndAngle = overlay.TransitionEndAngle ?? inherited.TransitionEndAngle
        };
    }

    private static StarfieldSunPresetNightPatch? Merge(
        StarfieldSunPresetNightPatch? inherited,
        StarfieldSunPresetNightPatch? overlay)
    {
        if (overlay is null) return inherited;
        if (inherited is null) return overlay;
        return new StarfieldSunPresetNightPatch
        {
            DirectionalColor = Merge(inherited.DirectionalColor, overlay.DirectionalColor),
            DirectionalIlluminance =
                overlay.DirectionalIlluminance ?? inherited.DirectionalIlluminance,
            GlareColor = Merge(inherited.GlareColor, overlay.GlareColor)
        };
    }
}
